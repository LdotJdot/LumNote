using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace MarkdownEditor.Core;

/// <summary>
/// GitHub Flavored Markdown 解析器 - 100% 纯 C# 实现，无外部依赖
/// 针对实时渲染和高性能优化
/// </summary>
public static class MarkdownParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DocumentNode Parse(ReadOnlySpan<char> input, bool enableEmojiShortcodes = true)
    {
        var ctx = new MarkdownParseContext { EnableEmojiShortcodes = enableEmojiShortcodes };
        var lines = SplitLines(input);
        PreScanLinkReferences(lines, ctx);
        var blocks = ParseBlocks(lines, ctx);
        ExpandTableOfContentsBlocks(blocks);
        return new DocumentNode { Children = blocks };
    }

    public static DocumentNode Parse(string input, bool enableEmojiShortcodes = true)
    {
        return Parse(input.AsSpan(), enableEmojiShortcodes);
    }

    private static List<string> SplitLines(ReadOnlySpan<char> input)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i <= input.Length; i++)
        {
            if (i == input.Length || input[i] == '\n')
            {
                var line = input.Slice(start, i - start).ToString();
                if (line.EndsWith('\r'))
                    line = line[..^1];
                result.Add(line);
                start = i + 1;
            }
        }
        return result;
    }

    private static List<MarkdownNode> ParseBlocks(List<string> lines, MarkdownParseContext ctx)
    {
        var blocks = new List<MarkdownNode>();
        int i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                int emptyCount = 1;
                i++;
                while (i < lines.Count && lines[i].TrimStart().Length == 0)
                {
                    emptyCount++;
                    i++;
                }
                blocks.Add(new EmptyLineNode { LineCount = emptyCount });
                continue;
            }

            // 代码块
            if (trimmed.StartsWith("```"))
            {
                var (block, consumed) = ParseCodeBlock(lines, i);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 引用块
            if (trimmed.StartsWith(">"))
            {
                var (block, consumed) = ParseBlockquote(lines, i, ctx);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 无序列表
            if (IsBulletListItem(trimmed))
            {
                var (block, consumed) = ParseBulletList(lines, i, ctx);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 有序列表
            if (IsOrderedListItem(trimmed))
            {
                var (block, consumed) = ParseOrderedList(lines, i, ctx);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 分隔线
            if (IsHorizontalRule(trimmed))
            {
                blocks.Add(new HorizontalRuleNode());
                i++;
                continue;
            }

            // 表格：当前行含 |，且后续若干「续行」（每行含 |）之后出现分隔行（允许表头单元格内软换行）
            if (trimmed.Contains('|'))
            {
                var (block, consumed) = ParseTable(lines, i);
                if (block != null)
                {
                    blocks.Add(block);
                    i += consumed;
                    continue;
                }
            }

            // 标题
            if (trimmed.StartsWith("#") && TryParseHeading(trimmed, ctx, out var heading) && heading != null)
            {
                blocks.Add(heading);
                i++;
                continue;
            }

            // 缩进代码块（4空格或1tab）- 必须用原始行 line 判断；若内容像 HTML 标签则不当代码块，当普通段落（html不要放在块里）
            if (IsIndentedCodeLine(line.AsSpan()) && !IsIndentedHtmlLike(line))
            {
                var (block, consumed) = ParseIndentedCodeBlock(lines, i);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 块级数学公式 $$（仅当行以 $$ 开头，且后面不是普通说明文字时才视为公式块）
            if (IsMathBlockStart(trimmed))
            {
                var (block, consumed) = ParseMathBlock(lines, i);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 兼容单个 $ 包裹单行公式的旧式块级写法：
            // $
            // +123
            // $
            // 仅当起始 $ 与首个非空内容之间不存在空行，且内容行与结束 $ 行之间也不存在空行时才视为块级数学；
            // 若存在空行（例如 "$"、空行、"+123"、"$"），则整体按普通段落处理。
            if (trimmed.Length > 0 && trimmed[0] == '$')
            {
                if (TryParseSingleDollarMathBlock(lines, i, out var mathBlock, out var consumedLines))
                {
                    blocks.Add(mathBlock!);
                    i += consumedLines;
                    continue;
                }
            }

            // HTML 块：行首为 < 且紧跟字母、/、!、?（排除整行尖括号自动链接）
            if (IsHtmlBlockStart(trimmed) && !IsAngleBracketAutolinkLine(trimmed))
            {
                var (htmlBlock, consumed) = ParseHtmlBlock(lines, i);
                blocks.Add(htmlBlock);
                i += consumed;
                continue;
            }

            // 定义型列表：下一行以 : 开头
            if (i + 1 < lines.Count && IsDefinitionDefLine(lines[i + 1]))
            {
                var (block, consumed) = ParseDefinitionList(lines, i, ctx);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 链接引用定义 [label]: url "title"（非脚注）
            if (TryParseLinkReferenceDefinition(trimmed, ctx))
            {
                i++;
                continue;
            }

            // 脚注定义 [^id]:（可多行续行：行首 ≥2 空格或 tab）
            if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[1] == '^')
            {
                if (TryParseFootnoteDefFirstLine(trimmed, out var fnId, out var firstBody))
                {
                    var sb = new StringBuilder();
                    if (!string.IsNullOrEmpty(firstBody))
                        sb.Append(firstBody);
                    int j = i + 1;
                    while (j < lines.Count)
                    {
                        var raw = lines[j];
                        if (string.IsNullOrWhiteSpace(raw))
                            break;
                        var tr = raw.TrimStart();
                        if (TryParseLinkReferenceDefinition(tr, ctx))
                            break;
                        if (IsBlockStart(tr.AsSpan()))
                            break;
                        if (!IsFootnoteContinuationLine(raw))
                            break;
                        if (sb.Length > 0)
                            sb.Append('\n');
                        sb.Append(StripFootnoteContinuationPrefix(raw));
                        j++;
                    }

                    var body = sb.ToString();
                    var fnContent = string.IsNullOrEmpty(body)
                        ? []
                        : new List<MarkdownNode>
                        {
                            new ParagraphNode { Content = ParseInline(body, ctx) },
                        };
                    blocks.Add(new FootnoteDefNode { Id = fnId!, Content = fnContent });
                    i = j;
                    continue;
                }
            }

            // 目录占位（单独一行 [TOC]）
            if (string.Equals(trimmed, "[TOC]", StringComparison.OrdinalIgnoreCase))
            {
                blocks.Add(new TableOfContentsNode());
                i++;
                continue;
            }

            // Setext 标题（上一段式行 + === 或 ---）
            if (TryParseSetextHeading(lines, i, ctx, out var setextHeading, out int setextLines))
            {
                blocks.Add(setextHeading!);
                i += setextLines;
                continue;
            }

            // 段落 - 合并后续非空行
            var (paragraph, pConsumed) = ParseParagraph(lines, i, ctx);
            blocks.Add(paragraph);
            i += pConsumed;
        }

        return blocks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBulletListItem(ReadOnlySpan<char> line)
    {
        if (line.Length < 2)
            return false;
        return (line[0] == '-' || line[0] == '*' || line[0] == '+')
                && (line[1] == ' ' || line[1] == '\t')
            || (line.StartsWith("- [") || line.StartsWith("* [") || line.StartsWith("+ ["));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOrderedListItem(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        return i > 0
            && i < line.Length
            && (line[i] == '.' || line[i] == ')')
            && i + 1 < line.Length
            && (line[i + 1] == ' ' || line[i + 1] == '\t');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIndentedCodeLine(ReadOnlySpan<char> line)
    {
        if (line.Length < 4)
            return false;
        return (line[0] == ' ' && line[1] == ' ' && line[2] == ' ' && line[3] == ' ')
            || line[0] == '\t';
    }

    /// <summary>缩进行去掉前导 4 空格或 1 tab 后是否像 HTML 标签（以 &lt; 开头），这类不当代码块，当普通文本。</summary>
    private static bool IsIndentedHtmlLike(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        ReadOnlySpan<char> rest = line.StartsWith("\t") ? line.AsSpan(1) : (line.Length >= 4 && line[0] == ' ' && line[1] == ' ' && line[2] == ' ' && line[3] == ' ' ? line.AsSpan(4) : default);
        if (rest.Length == 0) return false;
        return rest[0] == '<' && (rest.Length == 1 || char.IsLetter(rest[1]) || rest[1] == '/' || rest[1] == '!');
    }

    private static bool IsHorizontalRule(ReadOnlySpan<char> line)
    {
        if (line.Length < 3)
            return false;
        return (line[0] == '-' || line[0] == '*' || line[0] == '_')
            && line.Trim(line[0]).Length == 0
            && line.Length >= 3;
    }

    private static bool IsDefinitionDefLine(string line)
    {
        var t = line.TrimStart();
        return t.Length >= 2 && t[0] == ':' && (t[1] == ' ' || t[1] == '\t');
    }

    private static (DefinitionListNode, int) ParseDefinitionList(List<string> lines, int start, MarkdownParseContext ctx)
    {
        var items = new List<DefinitionItemNode>();
        int i = start;

        while (i < lines.Count)
        {
            var termLine = lines[i];
            var trimmed = termLine.TrimStart();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }
            if (IsDefinitionDefLine(termLine))
                break;

            var term = ParseInline(trimmed, ctx);
            var definitions = new List<MarkdownNode>();
            i++;

            while (i < lines.Count)
            {
                var defLine = lines[i];
                var defTrimmed = defLine.TrimStart();
                if (defTrimmed.Length == 0)
                {
                    i++;
                    int j = i;
                    while (j < lines.Count && lines[j].TrimStart().Length == 0) j++;
                    if (j >= lines.Count || (!IsDefinitionDefLine(lines[j]) && !IsIndentedCodeLine(lines[j].AsSpan())))
                        break;
                    continue;
                }
                if (IsDefinitionDefLine(defLine))
                {
                    var content = defTrimmed.Length > 2 ? defTrimmed[2..].TrimStart() : "";
                    if (content.Length > 0)
                        definitions.Add(new ParagraphNode { Content = ParseInline(content, ctx) });
                    i++;
                    continue;
                }
                if (IsIndentedCodeLine(defLine.AsSpan()))
                {
                    var (code, consumed) = ParseIndentedCodeBlock(lines, i);
                    definitions.Add(code);
                    i += consumed;
                    continue;
                }
                break;
            }

            items.Add(new DefinitionItemNode { Term = term, Definitions = definitions });
        }

        return (new DefinitionListNode { Items = items }, i - start);
    }

    private static bool IsBlockStart(ReadOnlySpan<char> t)
    {
        if (t.Length == 0) return false;
        if (IsAngleBracketAutolinkLine(t)) return false;
        return t.StartsWith("#") || t.StartsWith(">") || t.StartsWith("```") || t.StartsWith("$$")
            || IsBulletListItem(t) || IsOrderedListItem(t) || IsHorizontalRule(t)
            || IsIndentedCodeLine(t) || IsHtmlBlockStart(t);
    }

    private static bool IsHtmlBlockStart(ReadOnlySpan<char> t)
    {
        return t.Length > 1 && t[0] == '<'
            && (char.IsLetter(t[1]) || t[1] == '/' || t[1] == '!' || t[1] == '?');
    }

    /// <summary>整行仅为 <c>&lt;http...&gt;</c> / <c>mailto:</c> 时当作段落自动链接，不当 HTML 块。</summary>
    private static bool IsAngleBracketAutolinkLine(ReadOnlySpan<char> trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] != '<')
            return false;
        int gt = trimmed.IndexOf('>');
        if (gt <= 1 || gt != trimmed.Length - 1)
            return false;
        var inner = trimmed[1..gt];
        return inner.StartsWith("http://".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith("https://".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith("mailto:".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static void PreScanLinkReferences(List<string> lines, MarkdownParseContext ctx)
    {
        bool inFence = false;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || t.Length == 0)
                continue;
            TryParseLinkReferenceDefinition(t, ctx);
        }
    }

    private static bool TryParseFootnoteDefFirstLine(string trimmed, out string? id, out string? firstBody)
    {
        id = null;
        firstBody = null;
        if (trimmed.Length < 5 || trimmed[0] != '[' || trimmed[1] != '^')
            return false;
        int endBracket = 2;
        while (endBracket < trimmed.Length && trimmed[endBracket] != ']')
            endBracket++;
        if (endBracket >= trimmed.Length || endBracket + 1 >= trimmed.Length || trimmed[endBracket + 1] != ':')
            return false;
        id = trimmed[2..endBracket].ToString();
        firstBody = endBracket + 2 <= trimmed.Length ? trimmed[(endBracket + 2)..].TrimStart() : "";
        return true;
    }

    private static bool IsFootnoteContinuationLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
        if (line[0] == '\t')
            return true;
        int spaces = 0;
        for (int i = 0; i < line.Length && line[i] == ' '; i++)
            spaces++;
        return spaces >= 2;
    }

    private static string StripFootnoteContinuationPrefix(string line)
    {
        if (line.StartsWith('\t'))
            return line.Length > 1 ? line[1..] : "";
        if (line.Length >= 2 && line[0] == ' ' && line[1] == ' ')
            return line[2..].TrimStart();
        return line.TrimStart();
    }

    private static bool TryParseLinkReferenceDefinition(string trimmed, MarkdownParseContext ctx)
    {
        if (trimmed.Length < 5 || trimmed[0] != '[')
            return false;
        if (trimmed[1] == '^')
            return false;
        int close = trimmed.IndexOf(']', 1);
        if (close < 0 || close + 1 >= trimmed.Length || trimmed[close + 1] != ':')
            return false;
        var label = trimmed[1..close];
        if (label.Length == 0)
            return false;
        var rest = trimmed[(close + 2)..].TrimStart();
        if (rest.Length == 0)
            return false;
        if (!TryParseReferenceLineDestinationAndTitle(rest.AsSpan(), out var url, out var title))
            return false;
        var key = MarkdownParseContext.NormalizeReferenceLabel(label.ToString());
        if (key.Length > 0)
            ctx.LinkReferences[key] = new LinkReferenceDefinition(url, title);
        return true;
    }

    /// <summary>解析引用定义行中冒号后的 destination 与可选 title（至行尾）。</summary>
    private static bool TryParseReferenceLineDestinationAndTitle(
        ReadOnlySpan<char> span,
        out string url,
        out string? title
    )
    {
        url = "";
        title = null;
        int i = 0;
        while (i < span.Length && char.IsWhiteSpace(span[i]))
            i++;
        if (i >= span.Length)
            return false;
        int urlStart = i;
        if (span[i] == '<')
        {
            int gt = span[i..].IndexOf('>');
            if (gt < 0)
                return false;
            gt += i;
            url = span[(i + 1)..gt].ToString();
            i = gt + 1;
        }
        else
        {
            while (i < span.Length)
            {
                if (span[i] == '\\' && i + 1 < span.Length)
                {
                    i += 2;
                    continue;
                }
                if (char.IsWhiteSpace(span[i]))
                    break;
                i++;
            }
            url = span[urlStart..i].ToString();
        }
        while (i < span.Length && char.IsWhiteSpace(span[i]))
            i++;
        if (i < span.Length)
        {
            if (!TryParseLinkTitleSuffix(span, i, out title, out var ti))
                return false;
            i += ti;
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;
            if (i < span.Length)
                return false;
        }
        return url.Length > 0;
    }

    private static bool TryParseLinkTitleSuffix(
        ReadOnlySpan<char> span,
        int i,
        out string? title,
        out int consumed
    )
    {
        title = null;
        consumed = 0;
        if (i >= span.Length)
            return false;
        char q = span[i];
        if (q == '"' || q == '\'')
        {
            var sb = new StringBuilder();
            int j = i + 1;
            while (j < span.Length)
            {
                if (span[j] == '\\' && j + 1 < span.Length)
                {
                    sb.Append(span[j + 1]);
                    j += 2;
                    continue;
                }
                if (span[j] == q)
                {
                    title = sb.ToString();
                    consumed = j - i + 1;
                    return true;
                }
                sb.Append(span[j]);
                j++;
            }
            return false;
        }
        if (q == '(')
        {
            int depth = 1;
            int j = i + 1;
            var sb = new StringBuilder();
            while (j < span.Length && depth > 0)
            {
                if (span[j] == '\\' && j + 1 < span.Length)
                {
                    sb.Append(span[j + 1]);
                    j += 2;
                    continue;
                }
                if (span[j] == '(')
                {
                    depth++;
                    sb.Append('(');
                    j++;
                    continue;
                }
                if (span[j] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        title = sb.ToString();
                        consumed = j - i + 1;
                        return true;
                    }
                    sb.Append(')');
                    j++;
                    continue;
                }
                sb.Append(span[j]);
                j++;
            }
            return false;
        }
        return false;
    }

    /// <summary>行内链接/图片的 <c>(destination optional-title)</c>，从 <paramref name="openIdx"/> 处的 <c>(</c> 开始。</summary>
    private static bool TryParseParenLink(
        ReadOnlySpan<char> span,
        int openIdx,
        out string url,
        out string? title,
        out int consumedFromOpen
    )
    {
        url = "";
        title = null;
        consumedFromOpen = 0;
        if (openIdx >= span.Length || span[openIdx] != '(')
            return false;
        int i = openIdx + 1;
        while (i < span.Length && span[i] == ' ')
            i++;
        if (i >= span.Length)
            return false;
        int urlStart = i;
        if (span[i] == '<')
        {
            int rel = span[i..].IndexOf('>');
            if (rel < 0)
                return false;
            int gt = i + rel;
            url = span[(i + 1)..gt].ToString();
            i = gt + 1;
            while (i < span.Length && span[i] == ' ')
                i++;
            if (i < span.Length && span[i] != ')')
            {
                if (!TryParseLinkTitleSuffix(span, i, out title, out var tl))
                    return false;
                i += tl;
                while (i < span.Length && span[i] == ' ')
                    i++;
            }
            if (i >= span.Length || span[i] != ')')
                return false;
            i++;
            consumedFromOpen = i - openIdx;
            return url.Length > 0;
        }

        int depth = 0;
        while (i < span.Length)
        {
            if (span[i] == '\\' && i + 1 < span.Length)
            {
                i += 2;
                continue;
            }
            if (span[i] == '(')
            {
                depth++;
                i++;
                continue;
            }
            if (span[i] == ')')
            {
                if (depth == 0)
                    break;
                depth--;
                i++;
                continue;
            }
            if (depth == 0 && char.IsWhiteSpace(span[i]))
            {
                int j = i;
                while (j < span.Length && char.IsWhiteSpace(span[j]))
                    j++;
                if (j < span.Length && (span[j] == '"' || span[j] == '\'' || span[j] == '('))
                    break;
            }
            i++;
        }

        if (i >= span.Length)
            return false;
        if (span[i] == ')')
        {
            url = span[urlStart..i].Trim().ToString();
            i++;
        }
        else
        {
            url = span[urlStart..i].Trim().ToString();
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;
            if (!TryParseLinkTitleSuffix(span, i, out title, out var tl))
                return false;
            i += tl;
            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;
            if (i >= span.Length || span[i] != ')')
                return false;
            i++;
        }
        consumedFromOpen = i - openIdx;
        return url.Length > 0;
    }

    private static bool TryParseSetextHeading(
        List<string> lines,
        int i,
        MarkdownParseContext ctx,
        out HeadingNode? heading,
        out int consumedLines
    )
    {
        heading = null;
        consumedLines = 0;
        if (i + 1 >= lines.Count)
            return false;
        var t0 = lines[i].Trim();
        if (t0.Length == 0)
            return false;
        if (t0[0] == '#')
            return false;
        var t1 = lines[i + 1].Trim();
        if (t1.Length < 3)
            return false;
        bool allEq = true;
        foreach (var c in t1)
        {
            if (c != '=')
            {
                allEq = false;
                break;
            }
        }
        if (allEq)
        {
            heading = new HeadingNode { Level = 1, Content = ParseInline(t0, ctx) };
            consumedLines = 2;
            return true;
        }
        bool allDash = true;
        foreach (var c in t1)
        {
            if (c != '-')
            {
                allDash = false;
                break;
            }
        }
        if (allDash)
        {
            heading = new HeadingNode { Level = 2, Content = ParseInline(t0, ctx) };
            consumedLines = 2;
            return true;
        }
        return false;
    }

    private static bool IsTableSeparator(string line)
    {
        var span = line.Trim().AsSpan();
        if (span.Length < 2)
            return false;
        // 必须包含 - 且只含 - : | 空格
        bool hasDash = false;
        for (int i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '-')
                hasDash = true;
            else if (c != ':' && c != '|' && c != ' ')
                return false;
        }
        return hasDash;
    }

    private static (CodeBlockNode, int) ParseIndentedCodeBlock(List<string> lines, int start)
    {
        var sb = new StringBuilder();
        int i = start;
        while (i < lines.Count && IsIndentedCodeLine(lines[i].AsSpan()))
        {
            var line = lines[i];
            var content = line.StartsWith("\t") ? line[1..] : (line.Length >= 4 ? line[4..] : "");
            // 仅缩进无内容的行（如行尾的 "    "）不追加，避免块内多出一行空行
            if (content.Length > 0)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(content);
            }
            i++;
        }
        return (new CodeBlockNode { Code = sb.ToString() }, i - start);
    }

    private static (CodeBlockNode, int) ParseCodeBlock(List<string> lines, int start)
    {
        var firstLine = lines[start].TrimStart();
        var lang = firstLine.Length > 3 ? firstLine[3..].Trim() : "";
        var sb = new StringBuilder();
        int i = start + 1;

        while (i < lines.Count && !lines[i].Trim().StartsWith("```"))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(lines[i]);
            i++;
        }

        return (
            new CodeBlockNode { Language = lang, Code = sb.ToString() },
            i - start + (i < lines.Count ? 1 : 0)
        );
    }

    private static (BlockquoteNode, int) ParseBlockquote(List<string> lines, int start, MarkdownParseContext ctx)
    {
        var innerLines = new List<string>();
        int i = start;

        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                // 空行：若后续仍有 > 行，则继续；否则结束
                int j = i + 1;
                while (j < lines.Count && lines[j].TrimStart().Length == 0)
                    j++;
                if (j >= lines.Count || !lines[j].TrimStart().StartsWith(">"))
                    break;
                innerLines.Add("");
                i++;
                continue;
            }
            if (!trimmed.StartsWith(">"))
                break;

            var content = trimmed.Length > 1 ? trimmed[1..].TrimStart() : "";
            if (content.Length > 0)
                innerLines.Add(content);
            else
                innerLines.Add("");
            i++;
        }

        var blocks = ParseBlocks(innerLines, ctx);
        return (new BlockquoteNode { Children = blocks }, i - start);
    }

    private static (BulletListNode, int) ParseBulletList(List<string> lines, int start, MarkdownParseContext ctx)
    {
        var items = new List<ListItemNode>();
        int i = start;

        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            bool isTask = false,
                isChecked = false;
            string content;
            int prefixLen = 0;

            if (
                (
                    trimmed.StartsWith("- [ ]")
                    || trimmed.StartsWith("* [ ]")
                    || trimmed.StartsWith("+ [ ]")
                )
            )
            {
                isTask = true;
                prefixLen = trimmed.StartsWith("- ") ? 5 : 5;
                content = trimmed[prefixLen..].TrimStart();
            }
            else if (
                trimmed.StartsWith("- [x]")
                || trimmed.StartsWith("- [X]")
                || trimmed.StartsWith("* [x]")
                || trimmed.StartsWith("* [X]")
                || trimmed.StartsWith("+ [x]")
                || trimmed.StartsWith("+ [X]")
            )
            {
                isTask = true;
                isChecked = true;
                // 与 "- [ ]" 相同为 5 个字符（x 仅占位空格），误用 6 会在 "]" 后无空格时吃掉正文首字符
                prefixLen = 5;
                content = trimmed[prefixLen..].TrimStart();
            }
            else if (
                (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+')
                && trimmed.Length > 1
                && (trimmed[1] == ' ' || trimmed[1] == '\t')
            )
            {
                content = trimmed[2..].TrimStart();
            }
            else if (!IsBulletListItem(trimmed) && items.Count > 0)
            {
                break;
            }
            else
            {
                i++;
                continue;
            }

            var itemBlocks = new List<MarkdownNode>
            {
                new ParagraphNode { Content = ParseInline(content, ctx) }
            };
            items.Add(
                new ListItemNode
                {
                    IsTask = isTask,
                    IsChecked = isChecked,
                    Content = itemBlocks
                }
            );
            i++;
        }

        return (new BulletListNode { Items = items }, i - start);
    }

    private static (OrderedListNode, int) ParseOrderedList(List<string> lines, int start, MarkdownParseContext ctx)
    {
        var items = new List<ListItemNode>();
        int i = start;
        int startNum = 1;

        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            if (!IsOrderedListItem(trimmed))
                break;

            int j = 0;
            while (j < trimmed.Length && char.IsDigit(trimmed[j]))
                j++;
            if (items.Count == 0 && j > 0)
                int.TryParse(trimmed[..j], out startNum);

            if (j >= trimmed.Length || (trimmed[j] != '.' && trimmed[j] != ')'))
                break;
            int cStart = j + 1;
            while (cStart < trimmed.Length && (trimmed[cStart] == ' ' || trimmed[cStart] == '\t'))
                cStart++;
            var content = trimmed[cStart..];
            var itemBlocks = new List<MarkdownNode>
            {
                new ParagraphNode { Content = ParseInline(content, ctx) }
            };
            items.Add(new ListItemNode { Content = itemBlocks });
            i++;
        }

        return (new OrderedListNode { Items = items, StartNumber = startNum }, i - start);
    }

    private static (TableNode?, int) ParseTable(List<string> lines, int start)
    {
        var headerSb = new StringBuilder();
        int j = start;
        while (j < lines.Count)
        {
            var line = lines[j];
            var t = line.Trim();
            if (t.Length == 0)
                return (null, 0);
            if (IsTableSeparator(line))
                break;
            if (!t.Contains('|'))
                return (null, 0);
            if (headerSb.Length > 0)
                headerSb.Append('\n');
            headerSb.Append(line);
            j++;
        }

        if (j >= lines.Count || headerSb.Length == 0)
            return (null, 0);

        var headerLine = headerSb.ToString();
        var headers = ParseTableRow(headerLine);
        if (headers.Count == 0)
            return (null, 0);

        var sepLine = lines[j];
        var alignments = ParseTableAlignment(sepLine, headers.Count);

        var rows = new List<List<string>>();
        int i = ConsumeTableBodyCore(idx => lines[idx], lines.Count, j + 1, headers.Count, rows);

        return (
            new TableNode
            {
                Headers = headers,
                Rows = rows,
                ColumnAlignments = alignments
            },
            i - start
        );
    }

    /// <summary>单元格内是否已出现至少两个换行（视为用户主动打断，仅接受一次跨行续写）。</summary>
    private static bool AnyCellHasMultipleNewlines(IReadOnlyList<string> cells)
    {
        foreach (var c in cells)
        {
            var i = c.IndexOf('\n');
            if (i >= 0 && c.IndexOf('\n', i + 1) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 解析表体：在列数与表头一致的前提下，将物理行合并为逻辑行（单元格内软换行）。
    /// 新行必须以含 <c>|</c> 的行开始；续行可无 <c>|</c>。若合并下一行会导致列数超过期望值，则当前行提前结束，下一行作为新行。
    /// 每个单元格最多接受<strong>一个</strong>由续行产生的换行；再续行则视为新逻辑行。
    /// </summary>
    private static int ConsumeTableBodyCore(
        Func<int, string> getLine,
        int lineCount,
        int bodyStart,
        int expectedCols,
        List<List<string>>? rows
    )
    {
        int i = bodyStart;
        while (i < lineCount)
        {
            var line = getLine(i);
            var t = line.Trim();
            if (t.Length == 0)
                break;
            if (!t.Contains('|'))
                break;

            var sb = new StringBuilder(line);
            i++;
            var row = ParseTableRow(sb.ToString());

            while (row.Count < expectedCols && i < lineCount)
            {
                var next = getLine(i);
                if (next.Trim().Length == 0)
                    break;
                var candidate = sb.ToString() + "\n" + next;
                var candRow = ParseTableRow(candidate);
                if (candRow.Count > expectedCols)
                    break;
                if (AnyCellHasMultipleNewlines(candRow))
                    break;
                sb.Clear();
                sb.Append(candidate);
                row = candRow;
                i++;
            }

            if (rows != null && (row.Count > 0 || line.Trim().Replace("|", "").Trim().Length > 0))
                rows.Add(row);
        }

        return i;
    }

    /// <summary>供块扫描等与 <see cref="ParseTable"/> 对齐的表体结束行号（不包含）。</summary>
    internal static int ConsumeTableBodyEndExclusive(
        Func<int, string> getLine,
        int lineCount,
        int bodyStart,
        int expectedCols
    ) => ConsumeTableBodyCore(getLine, lineCount, bodyStart, expectedCols, null);

    /// <summary>将单行管道文本拆成单元格（与表格解析一致）。</summary>
    internal static List<string> ParseTableCells(string line) => ParseTableRow(line);

    private static List<string> ParseTableRow(string line)
    {
        var result = new List<string>();
        var cell = new StringBuilder();
        bool escape = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (escape)
            {
                cell.Append(line[i]);
                escape = false;
                continue;
            }
            if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '|')
            {
                escape = true;
                continue;
            }
            if (line[i] == '|')
            {
                result.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
                cell.Append(line[i]);
        }
        result.Add(cell.ToString().Trim());
        int s = result.Count > 0 && result[0].Length == 0 ? 1 : 0;
        int e = result.Count > 1 && result[^1].Length == 0 ? result.Count - 1 : result.Count;
        if (s >= e)
            return [];
        return result.GetRange(s, e - s);
    }

    private static List<TableAlign>? ParseTableAlignment(string sepLine, int colCount)
    {
        var cells = ParseTableRow(sepLine);
        if (cells.Count == 0)
            return null;
        var list = new List<TableAlign>();
        for (int i = 0; i < Math.Min(cells.Count, colCount); i++)
        {
            var c = cells[i].Trim();
            if (c.StartsWith(':') && c.EndsWith(':'))
                list.Add(TableAlign.Center);
            else if (c.EndsWith(':'))
                list.Add(TableAlign.Right);
            else
                list.Add(TableAlign.Left);
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 判断是否为块级数学公式起始行（仅支持 $$ 开头的标准形式）。
    /// 规则：
    /// - 行首必须是 \"$$\"；
    /// - 若同一行内再次出现 \"$$\"（形如 \"$$...$$\"），则为单行块公式；
    /// - 若同一行内没有再次出现 \"$$\"，则为多行块公式的起始行（含 \"$$\" 独占一行、或 \"$$\\begin{eqnarray}\" 等首行即带内容），
    ///   与 <see cref=\"ParseMathBlock\" /> 一致；否则 \"$$\\sum a\" 这类会被误判为普通段落，导出时多出 $ 且多行环境无法成图。
    /// </summary>
    private static bool IsMathBlockStart(ReadOnlySpan<char> trimmed)
    {
        if (!trimmed.StartsWith("$$"))
            return false;
        if (trimmed.Length == 2)
            return true;

        var rest = trimmed[2..];
        var idx = rest.IndexOf("$$".AsSpan());
        if (idx >= 0)
            return true; // 单行 $$...$$ 公式

        // 多行：本行无闭合 $$，整行视为块公式起始（ParseMathBlock 会吞掉首行 $$ 之后的内容）
        return true;
    }

    private static (MathBlockNode, int) ParseMathBlock(List<string> lines, int start)
    {
        var firstLine = lines[start].TrimStart();
        var sb = new StringBuilder();
        int i = start;

        if (firstLine.StartsWith("$$") && firstLine.Length > 2)
        {
            var rest = firstLine[2..];
            var endIdx = rest.IndexOf("$$", StringComparison.Ordinal);
            if (endIdx >= 0)
                return (new MathBlockNode { LaTeX = rest[..endIdx].Trim() }, 1);
            sb.Append(rest);
            i++;
        }
        else if (firstLine.Trim() == "$$")
        {
            i = start + 1;
        }

        while (i < lines.Count)
        {
            var line = lines[i];
            var endIdx = line.IndexOf("$$", StringComparison.Ordinal);
            if (endIdx >= 0)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(line[..endIdx].Trim());
                return (new MathBlockNode { LaTeX = sb.ToString().Trim() }, i - start + 1);
            }
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
            i++;
        }
        return (new MathBlockNode { LaTeX = sb.ToString().Trim() }, i - start);
    }

    /// <summary>
    /// 兼容单个 $ 包裹「一行内容」的块级公式写法，支持多种等价形式：
    /// 1) 经典三行：
    ///    $
    ///    +123
    ///    $
    /// 2) 起始行带内容，结束行为单独一行：
    ///    $+123
    ///    $
    /// 3) 起始行为单独一行，下一行结尾带结束符：
    ///    $
    ///    +123$
    /// 4) 同行起止：
    ///    $+123$
    /// 以上几种都解析为单个 MathBlockNode，LaTeX 内容均为 \"+123\"。
    /// 约束：
    /// - 所有 $ 标记必须位于块的行首（忽略缩进）；
    /// - 不允许在起始 $ 与内容之间、或内容与结束 $ 之间插入空行；
    /// - 若一行内出现超过两个 $，则视为普通文本。
    /// </summary>
    private static bool TryParseSingleDollarMathBlock(
        List<string> lines,
        int start,
        out MathBlockNode? block,
        out int consumed)
    {
        block = null;
        consumed = 0;
        if (start >= lines.Count)
            return false;

        var firstLine = lines[start];
        var firstTrimmed = firstLine.Trim();
        if (firstTrimmed.Length == 0)
            return false;
        if (firstTrimmed[0] != '$')
            return false;
        // 避免与 $$ 数学块冲突
        if (firstTrimmed.Length >= 2 && firstTrimmed[1] == '$')
            return false;

        // 情形 4 / 同行起止：$...$
        int firstDollar = firstTrimmed.IndexOf('$');
        int secondDollar = firstTrimmed.IndexOf('$', firstDollar + 1);
        if (secondDollar >= 0)
        {
            // 不允许超过两个 $
            if (firstTrimmed.IndexOf('$', secondDollar + 1) >= 0)
                return false;

            var inner = firstTrimmed[(firstDollar + 1)..secondDollar].Trim();
            if (inner.Length == 0)
                return false;

            block = new MathBlockNode { LaTeX = inner };
            consumed = 1;
            return true;
        }

        // 只在起始处出现一个 $。
        // 根据起始行是否只有 $ 决定解析分支。
        if (string.Equals(firstTrimmed, "$", StringComparison.Ordinal))
        {
            int next = start + 1;
            if (next >= lines.Count)
                return false;

            var secondTrimmed = lines[next].Trim();
            if (secondTrimmed.Length == 0)
                return false; // 起始与内容之间不允许空行

            // 情形 3：
            // $
            // +123$
            if (secondTrimmed.Length > 1 && secondTrimmed[^1] == '$')
            {
                var inner = secondTrimmed[..^1].Trim();
                if (inner.Length == 0)
                    return false;

                block = new MathBlockNode { LaTeX = inner };
                consumed = 2;
                return true;
            }

            // 情形 1：经典三行
            if (secondTrimmed == "$")
                return false; // 起始后立刻结束且无内容，当作普通文本

            int closingIndex = next + 1;
            if (closingIndex >= lines.Count)
                return false;

            var closingTrimmed = lines[closingIndex].Trim();
            if (!string.Equals(closingTrimmed, "$", StringComparison.Ordinal))
                return false;

            var latex = secondTrimmed;
            block = new MathBlockNode { LaTeX = latex };
            consumed = closingIndex - start + 1;
            return true;
        }
        else
        {
            // 情形 2：
            // $+123
            // $
            var after = firstTrimmed[1..].Trim();
            if (after.Length == 0)
                return false;

            int next = start + 1;
            if (next >= lines.Count)
                return false;

            var secondTrimmed = lines[next].Trim();
            if (!string.Equals(secondTrimmed, "$", StringComparison.Ordinal))
                return false;

            block = new MathBlockNode { LaTeX = after };
            consumed = 2;
            return true;
        }
    }

    private static (HtmlBlockNode, int) ParseHtmlBlock(List<string> lines, int start)
    {
        var sb = new StringBuilder();
        int i = start;
        while (i < lines.Count)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0)
                break;
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
            i++;
            // 块级闭合标签可结束块
            if (t.StartsWith("</") && t.Length > 3)
            {
                var tagEnd = t.IndexOf('>');
                if (tagEnd > 2)
                {
                    var tag = t[2..tagEnd].Trim().ToLowerInvariant();
                    if (
                        tag
                        is "div"
                            or "table"
                            or "pre"
                            or "blockquote"
                            or "figure"
                            or "section"
                            or "article"
                            or "header"
                            or "footer"
                    )
                        break;
                }
            }
        }
        return (new HtmlBlockNode { RawHtml = sb.ToString() }, i - start);
    }

    private static bool TryParseHeading(ReadOnlySpan<char> line, MarkdownParseContext ctx, out HeadingNode? heading)
    {
        heading = null;
        int level = 0;
        while (level < line.Length && level < 7 && line[level] == '#')
            level++;
        if (level == 0 || level > 6)
            return false;
        var content = line[level..].TrimStart();
        heading = new HeadingNode { Level = level, Content = ParseInline(content.ToString(), ctx) };
        return true;
    }

    private static (ParagraphNode, int) ParseParagraph(List<string> lines, int start, MarkdownParseContext ctx)
    {
        var sb = new StringBuilder(lines[start]);
        int i = start + 1;

        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
                break;
            if (
                line.StartsWith("#")
                || line.StartsWith(">")
                || line.StartsWith("-")
                || line.StartsWith("*")
                || line.StartsWith("+")
                || line.StartsWith("```")
            )
            {
                if (char.IsDigit(line.TrimStart()[0]) && line.TrimStart().Contains('.'))
                    break;
                if (IsBulletListItem(line.TrimStart()) || IsOrderedListItem(line.TrimStart()))
                    break;
                // 其他以特殊前缀开头的块起始（如列表、标题等）已经在上面处理
            }

            // 数学块起始：遇到以 $$ 开头的行应结束当前段落，交由块级数学解析
            var trimmed = line.TrimStart();
            if (IsMathBlockStart(trimmed))
                break;

            // HTML 块或其他块级起始，也应结束当前段落
            if (IsBlockStart(trimmed))
            {
                // 但为了避免与上面的列表/标题重复判断，只在未命中的情况下处理
                break;
            }
            sb.Append('\n').Append(line);
            i++;
        }

        return (new ParagraphNode { Content = ParseInline(sb.ToString(), ctx) }, i - start);
    }

    /// <summary>
    /// 解析行内元素 - 支持 **bold** *italic* ***粗斜*** ~~strikethrough~~ `code` [link](url) ![img](url)
    /// baseOffset 用于标注节点在所属输入中的起始偏移，便于编辑区高亮按列定位。
    /// 对于「按行调用」的场景，建议传入该行内起始列号作为 baseOffset。
    /// </summary>
    public static List<InlineNode> ParseInline(string text, int baseOffset = 0) =>
        ParseInline(text, null, baseOffset);

    public static List<InlineNode> ParseInline(string text, MarkdownParseContext? ctx, int baseOffset = 0)
    {
        var result = new List<InlineNode>();
        var span = text.AsSpan();
        int pos = 0;

        while (pos < span.Length)
        {
            int start = pos;

            if (span[pos] == '\\' && pos + 1 < span.Length)
            {
                result.Add(
                    new TextNode
                    {
                        Content = span[pos + 1].ToString(),
                        Span = new SourceSpan(baseOffset + pos, 2),
                    }
                );
                pos += 2;
                continue;
            }

            if (TryParseAutolink(span, pos, out var autoLink, out int autoConsumed) && autoLink != null)
            {
                autoLink.Span = new SourceSpan(baseOffset + pos, autoConsumed);
                result.Add(autoLink);
                pos += autoConsumed;
                continue;
            }

            // 图片 ![alt](url) / ![alt][ref]
            if (pos + 1 < span.Length && span[pos] == '!' && span[pos + 1] == '[')
            {
                if (TryParseImage(span, pos, ctx, out var img, out int consumed) && img != null)
                {
                    img.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(img);
                    pos += consumed;
                    continue;
                }
            }

            // 脚注引用 [^id]
            if (pos < span.Length && span[pos] == '[' && pos + 2 < span.Length && span[pos + 1] == '^')
            {
                if (TryParseFootnoteRef(span, pos, out var fnRef, out int consumed) && fnRef != null)
                {
                    fnRef.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(fnRef);
                    pos += consumed;
                    continue;
                }
            }

            // 链接 [text](url) / [text][ref] / [text][]
            if (pos < span.Length && span[pos] == '[')
            {
                if (TryParseLink(span, pos, ctx, out var link, out int consumed) && link != null)
                {
                    link.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(link);
                    pos += consumed;
                    continue;
                }
            }

            // 行内数学 $...$
            if (span[pos] == '$' && pos + 1 < span.Length && span[pos + 1] != '$')
            {
                if (TryParseMathInline(span, pos, out var math, out int consumed) && math != null)
                {
                    math.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(math);
                    pos += consumed;
                    continue;
                }
            }

            // 行内代码 `code`
            if (span[pos] == '`')
            {
                if (TryParseCode(span, pos, out var code, out int consumed) && code != null)
                {
                    code.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(code);
                    pos += consumed;
                    continue;
                }
            }

            // 粗体+斜体 ***text***（须在 ** 与单 * 之前）
            if (pos + 2 < span.Length && span[pos] == '*' && span[pos + 1] == '*' && span[pos + 2] == '*')
            {
                if (TryParseBoldItalic(span, pos, '*', ctx, out var boldItalic, out int biConsumed) && boldItalic != null)
                {
                    boldItalic.Span = new SourceSpan(baseOffset + start, biConsumed);
                    result.Add(boldItalic);
                    pos += biConsumed;
                    continue;
                }
            }

            // 加粗 **text**
            if (pos + 1 < span.Length && span[pos] == '*' && span[pos + 1] == '*')
            {
                if (TryParseBold(span, pos, "**", ctx, out var bold, out int consumed) && bold != null)
                {
                    bold.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(bold);
                    pos += consumed;
                    continue;
                }
            }

            // 粗体+斜体 ___text___（须在 __ 与单 _ 之前）
            if (pos + 2 < span.Length && span[pos] == '_' && span[pos + 1] == '_' && span[pos + 2] == '_')
            {
                if (TryParseBoldItalic(span, pos, '_', ctx, out var boldItalicU, out int biuConsumed) && boldItalicU != null)
                {
                    boldItalicU.Span = new SourceSpan(baseOffset + start, biuConsumed);
                    result.Add(boldItalicU);
                    pos += biuConsumed;
                    continue;
                }
            }

            // 加粗 __text__
            if (pos + 1 < span.Length && span[pos] == '_' && span[pos + 1] == '_')
            {
                if (TryParseBold(span, pos, "__", ctx, out var bold, out int consumed) && bold != null)
                {
                    bold.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(bold);
                    pos += consumed;
                    continue;
                }
            }

            // 删除线 ~~text~~
            if (pos + 1 < span.Length && span[pos] == '~' && span[pos + 1] == '~')
            {
                if (TryParseStrikethrough(span, pos, ctx, out var strike, out int consumed) && strike != null)
                {
                    strike.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(strike);
                    pos += consumed;
                    continue;
                }
            }

            // 斜体 *text* 或 _text_
            if (span[pos] == '*' || span[pos] == '_')
            {
                if (TryParseItalic(span, pos, ctx, out var italic, out int consumed) && italic != null)
                {
                    italic.Span = new SourceSpan(baseOffset + start, consumed);
                    result.Add(italic);
                    pos += consumed;
                    continue;
                }
            }

            // GitHub / gemoji 风格 :name:（须在普通文本路径；行内代码等已提前 continue）
            if (
                span[pos] == ':'
                && TryParseEmojiShortcode(span, pos, ctx, baseOffset, out var emojiNode, out int emojiConsumed)
                && emojiNode != null
            )
            {
                result.Add(emojiNode);
                pos += emojiConsumed;
                continue;
            }

            ReadOnlySpan<char> tail = span[pos..];
            if (Rune.DecodeFromUtf16(tail, out Rune rune, out int runeLen) == OperationStatus.Done)
            {
                result.Add(
                    new TextNode
                    {
                        Content = rune.ToString(),
                        Span = new SourceSpan(baseOffset + pos, runeLen),
                    }
                );
                pos += runeLen;
                continue;
            }

            result.Add(
                new TextNode
                {
                    Content = span[pos].ToString(),
                    Span = new SourceSpan(baseOffset + pos, 1),
                }
            );
            pos++;
        }

        return result;
    }

    /// <summary>匹配 <c>:+1:</c>、<c>:non-potable_water:</c> 等（名称含字母数字、下划线、加号、减号）。</summary>
    private static bool TryParseEmojiShortcode(
        ReadOnlySpan<char> span,
        int pos,
        MarkdownParseContext? ctx,
        int baseOffset,
        out TextNode? node,
        out int consumed
    )
    {
        node = null;
        consumed = 0;
        if (ctx != null && !ctx.EnableEmojiShortcodes)
            return false;
        if (pos >= span.Length || span[pos] != ':')
            return false;
        int i = pos + 1;
        if (i >= span.Length)
            return false;
        int nameStart = i;
        while (i < span.Length)
        {
            char c = span[i];
            if (c == ':')
            {
                int nameLen = i - nameStart;
                if (nameLen == 0)
                    return false;
                var name = span.Slice(nameStart, nameLen).ToString();
                if (EmojiShortcodeRegistry.TryGet(name, out var emoji) && !string.IsNullOrEmpty(emoji))
                {
                    int total = i - pos + 1;
                    node = new TextNode
                    {
                        Content = emoji,
                        Span = new SourceSpan(baseOffset + pos, total),
                    };
                    consumed = total;
                    return true;
                }
                return false;
            }
            if (char.IsAsciiLetterOrDigit(c) || c == '_' || c == '+' || c == '-')
            {
                i++;
                continue;
            }
            break;
        }
        return false;
    }

    private static bool TryParseAutolink(
        ReadOnlySpan<char> span,
        int start,
        out LinkNode? link,
        out int consumed
    )
    {
        link = null;
        consumed = 0;
        if (start >= span.Length)
            return false;
        if (span[start] == '<')
        {
            int gt = span[start..].IndexOf('>');
            if (gt <= 0)
                return false;
            gt += start;
            var inner = span[(start + 1)..gt];
            if (inner.Length == 0)
                return false;
            var s = inner.ToString();
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                link = new LinkNode { Text = s, Url = s };
                consumed = gt - start + 1;
                return true;
            }
            return false;
        }

        ReadOnlySpan<char> rest = span[start..];
        if (rest.StartsWith("http://".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("https://".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            int i = 0;
            while (i < rest.Length && !char.IsWhiteSpace(rest[i]) && rest[i] != '<')
            {
                if ("()[]{}\"'".Contains(rest[i]))
                    break;
                i++;
            }
            if (i < 8)
                return false;
            var url = rest[..i].ToString();
            link = new LinkNode { Text = url, Url = url };
            consumed = i;
            return true;
        }

        if (rest.StartsWith("www.".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            if (start > 0)
            {
                char p = span[start - 1];
                if (char.IsLetterOrDigit(p) || p == '_' || p == '/')
                    return false;
            }
            int i = 0;
            while (i < rest.Length && !char.IsWhiteSpace(rest[i]) && rest[i] != '<')
            {
                if ("()[]{}\"'".Contains(rest[i]))
                    break;
                i++;
            }
            if (i <= 4)
                return false;
            var raw = rest[..i].ToString();
            var url = "http://" + raw;
            link = new LinkNode { Text = raw, Url = url };
            consumed = i;
            return true;
        }

        return false;
    }

    private static bool TryParseImage(
        ReadOnlySpan<char> span,
        int start,
        MarkdownParseContext? ctx,
        out ImageNode? img,
        out int consumed
    )
    {
        img = null;
        consumed = 0;
        if (start + 2 >= span.Length || span[start] != '!' || span[start + 1] != '[')
            return false;

        int endAlt = start + 2;
        while (endAlt < span.Length && span[endAlt] != ']')
            endAlt++;
        if (endAlt >= span.Length)
            return false;

        var alt = span[(start + 2)..endAlt].ToString();
        if (endAlt + 1 >= span.Length)
            return false;

        if (span[endAlt + 1] == '(')
        {
            if (
                !TryParseParenLink(span, endAlt + 1, out var url, out var title, out var parenLen)
            )
                return false;
            consumed = endAlt + 1 - start + parenLen;
            img = new ImageNode { Alt = alt, Url = url, Title = title };
            return true;
        }

        if (span[endAlt + 1] == '[' && ctx != null)
        {
            int endRef = endAlt + 2;
            while (endRef < span.Length && span[endRef] != ']')
                endRef++;
            if (endRef >= span.Length)
                return false;
            var refLabel = span[(endAlt + 2)..endRef];
            var key = MarkdownParseContext.NormalizeReferenceLabel(
                refLabel.Length > 0 ? refLabel.ToString() : alt
            );
            if (key.Length == 0 || !ctx.LinkReferences.TryGetValue(key, out var def))
                return false;
            consumed = endRef - start + 1;
            img = new ImageNode { Alt = alt, Url = def.Url, Title = def.Title };
            return true;
        }

        return false;
    }

    private static bool TryParseFootnoteRef(
        ReadOnlySpan<char> span,
        int start,
        out FootnoteRefNode? node,
        out int consumed
    )
    {
        node = null;
        consumed = 0;
        if (start + 3 >= span.Length || span[start] != '[' || span[start + 1] != '^')
            return false;
        int end = start + 2;
        while (end < span.Length && span[end] != ']')
            end++;
        if (end >= span.Length)
            return false;
        node = new FootnoteRefNode { Id = span[(start + 2)..end].ToString() };
        consumed = end - start + 1;
        return true;
    }

    private static bool TryParseLink(
        ReadOnlySpan<char> span,
        int start,
        MarkdownParseContext? ctx,
        out LinkNode? link,
        out int consumed
    )
    {
        link = null;
        consumed = 0;
        if (start + 1 >= span.Length || span[start] != '[')
            return false;

        int endText = start + 1;
        while (endText < span.Length && span[endText] != ']')
            endText++;
        if (endText >= span.Length)
            return false;

        var text = span[(start + 1)..endText].ToString();
        if (endText + 1 >= span.Length)
            return false;

        if (span[endText + 1] == '(')
        {
            if (
                !TryParseParenLink(span, endText + 1, out var url, out var title, out var parenLen)
            )
                return false;
            consumed = endText + 1 - start + parenLen;
            link = new LinkNode { Text = text, Url = url, Title = title };
            return true;
        }

        if (span[endText + 1] == '[' && ctx != null)
        {
            int endRef = endText + 2;
            while (endRef < span.Length && span[endRef] != ']')
                endRef++;
            if (endRef >= span.Length)
                return false;
            var refLabel = span[(endText + 2)..endRef];
            var key = MarkdownParseContext.NormalizeReferenceLabel(
                refLabel.Length > 0 ? refLabel.ToString() : text
            );
            if (key.Length == 0 || !ctx.LinkReferences.TryGetValue(key, out var def))
                return false;
            consumed = endRef - start + 1;
            link = new LinkNode { Text = text, Url = def.Url, Title = def.Title };
            return true;
        }

        return false;
    }

    private static bool TryParseMathInline(
        ReadOnlySpan<char> span,
        int start,
        out MathInlineNode? math,
        out int consumed
    )
    {
        math = null;
        consumed = 0;
        if (span[start] != '$' || start + 1 >= span.Length || span[start + 1] == '$')
            return false;
        int end = start + 1;
        while (end < span.Length && span[end] != '\n')
        {
            if (span[end] == '$' && (end + 1 >= span.Length || span[end + 1] != '$'))
            {
                math = new MathInlineNode { LaTeX = span[(start + 1)..end].ToString().Trim() };
                consumed = end - start + 1;
                return true;
            }
            end++;
        }
        return false;
    }

    private static bool TryParseCode(
        ReadOnlySpan<char> span,
        int start,
        out CodeNode? code,
        out int consumed
    )
    {
        code = null;
        consumed = 0;
        if (span[start] != '`')
            return false;

        int end = start + 1;
        while (end < span.Length && span[end] != '`')
            end++;
        if (end >= span.Length)
            return false;

        code = new CodeNode { Content = span[(start + 1)..end].ToString() };
        consumed = end - start + 1;
        return true;
    }

    private static bool TryParseBold(
        ReadOnlySpan<char> span,
        int start,
        string delim,
        MarkdownParseContext? ctx,
        out BoldNode? bold,
        out int consumed
    )
    {
        bold = null;
        consumed = 0;
        if (start + delim.Length * 2 > span.Length)
            return false;

        int innerStart = start + delim.Length;
        int end = innerStart;
        while (end + delim.Length <= span.Length)
        {
            if (span.Slice(end, delim.Length).SequenceEqual(delim.AsSpan()))
            {
                var inner = span[innerStart..end].ToString();
                bold = new BoldNode { Content = ParseInline(inner, ctx) };
                consumed = end - start + delim.Length;
                return true;
            }
            end++;
        }
        return false;
    }

    private static bool TryParseStrikethrough(
        ReadOnlySpan<char> span,
        int start,
        MarkdownParseContext? ctx,
        out StrikethroughNode? strike,
        out int consumed
    )
    {
        strike = null;
        consumed = 0;
        if (start + 4 > span.Length || !span.Slice(start, 2).SequenceEqual("~~".AsSpan()))
            return false;

        int end = start + 2;
        while (end + 2 <= span.Length)
        {
            if (span.Slice(end, 2).SequenceEqual("~~".AsSpan()))
            {
                var inner = span[(start + 2)..end].ToString();
                strike = new StrikethroughNode { Content = ParseInline(inner, ctx) };
                consumed = end - start + 2;
                return true;
            }
            end++;
        }
        return false;
    }

    private static bool TryParseItalic(
        ReadOnlySpan<char> span,
        int start,
        MarkdownParseContext? ctx,
        out ItalicNode? italic,
        out int consumed
    )
    {
        italic = null;
        consumed = 0;
        char delim = span[start];
        if (delim != '*' && delim != '_')
            return false;
        if (start + 2 >= span.Length)
            return false;

        int end = start + 1;
        while (end < span.Length)
        {
            if (span[end] == delim)
            {
                var inner = span[(start + 1)..end].ToString();
                italic = new ItalicNode { Content = ParseInline(inner, ctx) };
                consumed = end - start + 1;
                return true;
            }
            if (span[end] == '\n')
                break;
            end++;
        }
        return false;
    }

    /// <summary>
    /// 粗斜一体：<c>***text***</c>、<c>___text___</c>，AST 为 <see cref="BoldNode"/> 包裹单层 <see cref="ItalicNode"/>（与 CommonMark strong+em 一致）。
    /// </summary>
    private static bool TryParseBoldItalic(
        ReadOnlySpan<char> span,
        int start,
        char delim,
        MarkdownParseContext? ctx,
        out BoldNode? node,
        out int consumed
    )
    {
        node = null;
        consumed = 0;
        if (delim != '*' && delim != '_')
            return false;
        if (start + 7 > span.Length)
            return false;
        if (span[start] != delim || span[start + 1] != delim || span[start + 2] != delim)
            return false;
        // 四个及以上连续分隔符交给其它规则（如 **、**** 等）
        if (start + 3 < span.Length && span[start + 3] == delim)
            return false;

        int innerStart = start + 3;
        int end = innerStart;
        while (end + 2 < span.Length)
        {
            if (span[end] == delim && span[end + 1] == delim && span[end + 2] == delim)
            {
                var inner = span[innerStart..end].ToString();
                if (inner.Length == 0)
                    return false;
                var italicInner = ParseInline(inner, ctx);
                node = new BoldNode
                {
                    Content = new List<InlineNode> { new ItalicNode { Content = italicInner } },
                };
                consumed = end - start + 3;
                return true;
            }
            end++;
        }
        return false;
    }

    private static void ExpandTableOfContentsBlocks(List<MarkdownNode> blocks)
    {
        var hasToc = false;
        foreach (var b in blocks)
        {
            if (b is TableOfContentsNode)
            {
                hasToc = true;
                break;
            }
        }
        if (!hasToc)
            return;

        var headings = new List<HeadingNode>();
        foreach (var b in blocks)
            CollectHeadingsRecursive(b, headings);

        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is not TableOfContentsNode)
                continue;
            var items = new List<ListItemNode>();
            foreach (var h in headings)
            {
                var plain = FlattenInlinesPlain(h.Content);
                var prefix = new string(' ', Math.Max(0, (h.Level - 1) * 2));
                var line = string.IsNullOrEmpty(plain) ? prefix : prefix + plain;
                items.Add(
                    new ListItemNode
                    {
                        Content =
                        [
                            new ParagraphNode { Content = ParseInline(line) },
                        ],
                    }
                );
            }
            blocks[i] = new BulletListNode { Items = items };
        }
    }

    private static void CollectHeadingsRecursive(MarkdownNode? node, List<HeadingNode> list)
    {
        switch (node)
        {
            case HeadingNode h:
                list.Add(h);
                break;
            case BlockquoteNode bq:
                foreach (var c in bq.Children)
                    CollectHeadingsRecursive(c, list);
                break;
            case BulletListNode bl:
                foreach (var li in bl.Items)
                    foreach (var c in li.Content)
                        CollectHeadingsRecursive(c, list);
                break;
            case OrderedListNode ol:
                foreach (var li in ol.Items)
                    foreach (var c in li.Content)
                        CollectHeadingsRecursive(c, list);
                break;
            case DefinitionListNode dl:
                foreach (var di in dl.Items)
                {
                    foreach (var d in di.Definitions)
                        CollectHeadingsRecursive(d, list);
                }
                break;
        }
    }

    private static string FlattenInlinesPlain(List<InlineNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes)
        {
            if (n is TextNode t)
                sb.Append(t.Content);
            else if (n is BoldNode bn)
                sb.Append(FlattenInlinesPlain(bn.Content));
            else if (n is ItalicNode it)
                sb.Append(FlattenInlinesPlain(it.Content));
            else if (n is StrikethroughNode sn)
                sb.Append(FlattenInlinesPlain(sn.Content));
            else if (n is CodeNode cn)
                sb.Append(cn.Content);
            else if (n is LinkNode ln)
                sb.Append(ln.Text);
            else if (n is ImageNode img)
                sb.Append(img.Alt);
            else if (n is MathInlineNode m)
                sb.Append(m.LaTeX);
            else if (n is FootnoteRefNode fn)
                sb.Append("[^").Append(fn.Id).Append(']');
            else if (n is FootnoteMarkerNode fm)
                sb.Append('[').Append(fm.Number).Append(']');
        }
        return sb.ToString();
    }
}

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
    public static DocumentNode Parse(ReadOnlySpan<char> input)
    {
        var lines = SplitLines(input);
        var blocks = ParseBlocks(lines);
        return new DocumentNode { Children = blocks };
    }

    public static DocumentNode Parse(string input)
    {
        return Parse(input.AsSpan());
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

    private static List<MarkdownNode> ParseBlocks(List<string> lines)
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
                var (block, consumed) = ParseBlockquote(lines, i);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 无序列表
            if (IsBulletListItem(trimmed))
            {
                var (block, consumed) = ParseBulletList(lines, i);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 有序列表
            if (IsOrderedListItem(trimmed))
            {
                var (block, consumed) = ParseOrderedList(lines, i);
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

            // 表格：当前行含|，下一行是分隔行
            if (i + 1 < lines.Count && trimmed.Contains('|') && IsTableSeparator(lines[i + 1]))
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
            if (trimmed.StartsWith("#") && TryParseHeading(trimmed, out var heading))
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

            // HTML 块：行首为 < 且紧跟字母、/、!、?
            if (IsHtmlBlockStart(trimmed))
            {
                var (htmlBlock, consumed) = ParseHtmlBlock(lines, i);
                blocks.Add(htmlBlock);
                i += consumed;
                continue;
            }

            // 定义型列表：下一行以 : 开头
            if (i + 1 < lines.Count && IsDefinitionDefLine(lines[i + 1]))
            {
                var (block, consumed) = ParseDefinitionList(lines, i);
                blocks.Add(block);
                i += consumed;
                continue;
            }

            // 脚注定义 [^id]:
            if (TryParseFootnoteDef(trimmed, out var fnDef))
            {
                blocks.Add(fnDef);
                i++;
                continue;
            }

            // 段落 - 合并后续非空行
            var (paragraph, pConsumed) = ParseParagraph(lines, i);
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
            && line[i] == '.'
            && i + 1 < line.Length
            && (line[i + 1] == ' ' || line[i + 1] == ')');
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

    private static (DefinitionListNode, int) ParseDefinitionList(List<string> lines, int start)
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

            var term = ParseInline(trimmed);
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
                        definitions.Add(new ParagraphNode { Content = ParseInline(content) });
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
        return t.StartsWith("#") || t.StartsWith(">") || t.StartsWith("```") || t.StartsWith("$$")
            || IsBulletListItem(t) || IsOrderedListItem(t) || IsHorizontalRule(t)
            || IsIndentedCodeLine(t) || IsHtmlBlockStart(t);
    }

    private static bool IsHtmlBlockStart(ReadOnlySpan<char> t)
    {
        return t.Length > 1 && t[0] == '<'
            && (char.IsLetter(t[1]) || t[1] == '/' || t[1] == '!' || t[1] == '?');
    }

    private static bool TryParseFootnoteDef(string trimmed, out FootnoteDefNode? node)
    {
        node = null;
        if (trimmed.Length < 5 || trimmed[0] != '[' || trimmed[1] != '^')
            return false;
        int endBracket = 2;
        while (endBracket < trimmed.Length && trimmed[endBracket] != ']')
            endBracket++;
        if (endBracket >= trimmed.Length || endBracket + 2 > trimmed.Length || trimmed[endBracket + 1] != ':')
            return false;
        var id = trimmed[2..endBracket].ToString();
        var content = trimmed[(endBracket + 2)..].TrimStart();
        node = new FootnoteDefNode { Id = id, Content = [new ParagraphNode { Content = ParseInline(content) }] };
        return true;
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

    private static (BlockquoteNode, int) ParseBlockquote(List<string> lines, int start)
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

        var blocks = ParseBlocks(innerLines);
        return (new BlockquoteNode { Children = blocks }, i - start);
    }

    private static (BulletListNode, int) ParseBulletList(List<string> lines, int start)
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
                prefixLen = 6;
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
                new ParagraphNode { Content = ParseInline(content) }
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

    private static (OrderedListNode, int) ParseOrderedList(List<string> lines, int start)
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

            var content = trimmed[(j + 2)..].TrimStart();
            var itemBlocks = new List<MarkdownNode>
            {
                new ParagraphNode { Content = ParseInline(content) }
            };
            items.Add(new ListItemNode { Content = itemBlocks });
            i++;
        }

        return (new OrderedListNode { Items = items, StartNumber = startNum }, i - start);
    }

    private static (TableNode?, int) ParseTable(List<string> lines, int start)
    {
        var headerLine = lines[start];
        var headers = ParseTableRow(headerLine);
        if (headers.Count == 0)
            return (null, 0);

        var sepLine = lines[start + 1];
        var alignments = ParseTableAlignment(sepLine, headers.Count);

        var rows = new List<List<string>>();
        int i = start + 2;

        while (i < lines.Count)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0)
                break;
            if (!t.Contains('|'))
                break;
            var row = ParseTableRow(line);
            if (row.Count > 0 || t.Replace("|", "").Trim().Length > 0)
                rows.Add(row);
            i++;
        }

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

    private static List<string> ParseTableRow(string line)
    {
        var result = new List<string>();
        var parts = line.Split('|');
        int start = parts[0].Trim().Length == 0 ? 1 : 0;
        int end =
            parts.Length > 1 && parts[^1].Trim().Length == 0 ? parts.Length - 1 : parts.Length;
        for (int i = start; i < end; i++)
            result.Add(parts[i].Trim());
        return result;
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
    /// 判断是否为块级数学公式起始行（$$）
    /// 规则：
    /// - 行首必须是 $$
    /// - 若同一行内没有再次出现 $$，则 $$ 后面只能是空白，否则视为普通文本
    /// - 若同一行内再次出现 $$（形如 $$...$$），则允许行内有 LaTeX 内容
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

        // 无结束 $$，仅当 $$ 后只有空白时才视为块级公式起始
        for (int i = 0; i < rest.Length; i++)
        {
            if (!char.IsWhiteSpace(rest[i]))
                return false;
        }
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

    private static bool TryParseHeading(ReadOnlySpan<char> line, out HeadingNode? heading)
    {
        heading = null;
        int level = 0;
        while (level < line.Length && level < 7 && line[level] == '#')
            level++;
        if (level == 0 || level > 6)
            return false;
        var content = line[level..].TrimStart();
        heading = new HeadingNode { Level = level, Content = ParseInline(content.ToString()) };
        return true;
    }

    private static (ParagraphNode, int) ParseParagraph(List<string> lines, int start)
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

        return (new ParagraphNode { Content = ParseInline(sb.ToString()) }, i - start);
    }

    /// <summary>
    /// 解析行内元素 - 支持 **bold** *italic* ~~strikethrough~~ `code` [link](url) ![img](url)
    /// </summary>
    public static List<InlineNode> ParseInline(string text)
    {
        var result = new List<InlineNode>();
        var span = text.AsSpan();
        int pos = 0;

        while (pos < span.Length)
        {
            // 图片 ![alt](url)
            if (pos + 1 < span.Length && span[pos] == '!' && span[pos + 1] == '[')
            {
                if (TryParseImage(span, pos, out var img, out int consumed))
                {
                    result.Add(img);
                    pos += consumed;
                    continue;
                }
            }

            // 脚注引用 [^id]
            if (pos < span.Length && span[pos] == '[' && pos + 2 < span.Length && span[pos + 1] == '^')
            {
                if (TryParseFootnoteRef(span, pos, out var fnRef, out int consumed))
                {
                    result.Add(fnRef);
                    pos += consumed;
                    continue;
                }
            }

            // 链接 [text](url)
            if (pos < span.Length && span[pos] == '[')
            {
                if (TryParseLink(span, pos, out var link, out int consumed))
                {
                    result.Add(link);
                    pos += consumed;
                    continue;
                }
            }

            // 行内数学 $...$
            if (span[pos] == '$' && pos + 1 < span.Length && span[pos + 1] != '$')
            {
                if (TryParseMathInline(span, pos, out var math, out int consumed))
                {
                    result.Add(math);
                    pos += consumed;
                    continue;
                }
            }

            // 行内代码 `code`
            if (span[pos] == '`')
            {
                if (TryParseCode(span, pos, out var code, out int consumed))
                {
                    result.Add(code);
                    pos += consumed;
                    continue;
                }
            }

            // 加粗 **text**
            if (pos + 1 < span.Length && span[pos] == '*' && span[pos + 1] == '*')
            {
                if (TryParseBold(span, pos, "**", out var bold, out int consumed))
                {
                    result.Add(bold);
                    pos += consumed;
                    continue;
                }
            }

            // 加粗 __text__
            if (pos + 1 < span.Length && span[pos] == '_' && span[pos + 1] == '_')
            {
                if (TryParseBold(span, pos, "__", out var bold, out int consumed))
                {
                    result.Add(bold);
                    pos += consumed;
                    continue;
                }
            }

            // 删除线 ~~text~~
            if (pos + 1 < span.Length && span[pos] == '~' && span[pos + 1] == '~')
            {
                if (TryParseStrikethrough(span, pos, out var strike, out int consumed))
                {
                    result.Add(strike);
                    pos += consumed;
                    continue;
                }
            }

            // 斜体 *text* 或 _text_
            if (span[pos] == '*' || span[pos] == '_')
            {
                if (TryParseItalic(span, pos, out var italic, out int consumed))
                {
                    result.Add(italic);
                    pos += consumed;
                    continue;
                }
            }

            result.Add(new TextNode { Content = span[pos].ToString() });
            pos++;
        }

        return result;
    }

    private static bool TryParseImage(
        ReadOnlySpan<char> span,
        int start,
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
        if (endAlt + 2 >= span.Length || span[endAlt + 1] != '(')
            return false;

        int endUrl = endAlt + 2;
        while (endUrl < span.Length && span[endUrl] != ')' && span[endUrl] != ' ')
            endUrl++;
        if (endUrl >= span.Length || span[endUrl] != ')')
            return false;

        var url = span[(endAlt + 2)..endUrl].ToString();
        consumed = endUrl - start + 1;
        img = new ImageNode { Alt = alt, Url = url };
        return true;
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
        if (endText + 2 >= span.Length || span[endText + 1] != '(')
            return false;

        int endUrl = endText + 2;
        while (endUrl < span.Length && span[endUrl] != ')' && span[endUrl] != ' ')
            endUrl++;
        if (endUrl >= span.Length || span[endUrl] != ')')
            return false;

        var url = span[(endText + 2)..endUrl].ToString();
        consumed = endUrl - start + 1;
        link = new LinkNode { Text = text, Url = url };
        return true;
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
                bold = new BoldNode { Content = ParseInline(inner) };
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
                strike = new StrikethroughNode { Content = ParseInline(inner) };
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
                italic = new ItalicNode { Content = ParseInline(inner) };
                consumed = end - start + 1;
                return true;
            }
            if (span[end] == '\n')
                break;
            end++;
        }
        return false;
    }
}

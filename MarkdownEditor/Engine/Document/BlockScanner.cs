using System.Runtime.CompilerServices;

namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 块扫描器 - 极速首轮扫描，仅识别块边界
/// 用于虚拟化、视口计算，不解析内容
/// </summary>
public static class BlockScanner
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BlockSpan ScanNextBlock(IDocumentSource doc, int startLine)
    {
        if (startLine >= doc.LineCount)
            return default;
        var line = doc.GetLine(startLine);
        var trimmed = line.Trim();

        if (trimmed.Length == 0)
        {
            int end = startLine + 1;
            while (end < doc.LineCount && doc.GetLine(end).Trim().Length == 0)
                end++;
            return new BlockSpan(startLine, end, BlockKind.Paragraph); // 空块合并
        }

        // 代码块
        if (trimmed.StartsWith("```"))
        {
            int end = startLine + 1;
            while (end < doc.LineCount && !doc.GetLine(end).Trim().StartsWith("```"))
                end++;
            return new BlockSpan(startLine, Math.Min(end + 1, doc.LineCount), BlockKind.CodeBlock);
        }

        // 引用块（空行后若仍为 > 行则继续）
        if (trimmed.StartsWith(">"))
        {
            int end = startLine + 1;
            while (end < doc.LineCount)
            {
                var t = doc.GetLine(end).Trim();
                if (t.Length == 0)
                {
                    int j = end + 1;
                    while (j < doc.LineCount && doc.GetLine(j).Trim().Length == 0)
                        j++;
                    if (j >= doc.LineCount || !doc.GetLine(j).Trim().StartsWith(">"))
                        break;
                }
                else if (!t.StartsWith(">"))
                    break;
                end++;
            }
            return new BlockSpan(startLine, end, BlockKind.Blockquote);
        }

        // 列表
        if (IsListStart(trimmed))
        {
            int end = startLine + 1;
            while (end < doc.LineCount)
            {
                var t = doc.GetLine(end).Trim();
                if (t.Length == 0)
                {
                    end++;
                    continue;
                }
                if (!IsListStart(t) && !IsListContinuation(t))
                    break;
                end++;
            }
            return new BlockSpan(startLine, end, BlockKind.List);
        }

        // 分隔线
        if (IsHorizontalRule(trimmed))
            return new BlockSpan(startLine, startLine + 1, BlockKind.HorizontalRule);

        // 表格
        if (
            trimmed.Contains('|')
            && startLine + 1 < doc.LineCount
            && IsTableSeparator(doc.GetLine(startLine + 1))
        )
        {
            int end = startLine + 2;
            while (end < doc.LineCount && doc.GetLine(end).Trim().Contains('|'))
                end++;
            return new BlockSpan(startLine, end, BlockKind.Table);
        }

        // 标题
        if (trimmed.StartsWith("#") && IsHeading(trimmed))
            return new BlockSpan(startLine, startLine + 1, BlockKind.Heading);

        // 缩进代码块（4空格或1tab）- 使用原始行判断
        if (IsIndentedCodeLine(line))
        {
            int end = startLine + 1;
            while (end < doc.LineCount && IsIndentedCodeLine(doc.GetLine(end)))
                end++;
            return new BlockSpan(startLine, end, BlockKind.CodeBlock);
        }

        // 数学块（与 MarkdownParser 一致）：
        // - 单行：$$...$$（同一行内再次出现 $$）
        // - 多行：行首为 $$ 且 $$ 后只有空白，结束行再出现 $$。
        if (trimmed.StartsWith("$$"))
        {
            ReadOnlySpan<char> rest =
                trimmed.Length > 2 ? trimmed.Slice(2) : ReadOnlySpan<char>.Empty;

            // 单行 $$...$$：当前行内再次出现 $$
            if (!rest.IsEmpty && rest.IndexOf("$$".AsSpan()) >= 0)
            {
                return new BlockSpan(startLine, startLine + 1, BlockKind.MathBlock);
            }

            // 多行 $$ 起始：$$ 后只能是空白
            bool onlyWhitespace = true;
            foreach (var c in rest)
            {
                if (!char.IsWhiteSpace(c))
                {
                    onlyWhitespace = false;
                    break;
                }
            }
            if (trimmed.Length == 2 || onlyWhitespace)
            {
                int end = startLine + 1;
                while (
                    end < doc.LineCount
                    && doc.GetLine(end).IndexOf("$$", StringComparison.Ordinal) < 0
                )
                    end++;
                return new BlockSpan(
                    startLine,
                    Math.Min(end + 1, doc.LineCount),
                    BlockKind.MathBlock
                );
            }
        }

        // HTML 块
        if (
            trimmed.StartsWith("<")
            && trimmed.Length > 1
            && (
                char.IsLetter(trimmed[1])
                || trimmed[1] == '/'
                || trimmed[1] == '!'
                || trimmed[1] == '?'
            )
        )
        {
            int end = startLine + 1;
            while (end < doc.LineCount)
            {
                var t = doc.GetLine(end).Trim();
                if (t.Length == 0)
                    break;
                end++;
                if (t.StartsWith("</") && t.IndexOf('>') > 2)
                    break;
            }
            return new BlockSpan(startLine, end, BlockKind.HtmlBlock);
        }

        // 段落
        int pEnd = startLine + 1;
        while (pEnd < doc.LineCount)
        {
            var t = doc.GetLine(pEnd).Trim();
            if (t.Length == 0)
                break;
            if (IsBlockStart(t))
                break;
            if (IsFootnoteDefLine(t))
                break;
            pEnd++;
        }
        return new BlockSpan(startLine, pEnd, BlockKind.Paragraph);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFootnoteDefLine(ReadOnlySpan<char> t)
    {
        if (t.Length < 5 || t[0] != '[' || t[1] != '^') return false;
        int i = 2;
        while (i < t.Length && t[i] != ']') i++;
        return i + 1 < t.Length && t[i] == ']' && t[i + 1] == ':';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsListStart(ReadOnlySpan<char> t)
    {
        if (t.Length < 2)
            return false;
        return (t[0] == '-' || t[0] == '*' || t[0] == '+') && (t[1] == ' ' || t[1] == '\t')
            || (char.IsDigit(t[0]) && t.IndexOf('.') >= 0)
            || t.StartsWith("- [ ]", StringComparison.Ordinal)
            || t.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsListContinuation(ReadOnlySpan<char> t) =>
        t.Length >= 4 && (t[0] == ' ' || t[0] == '\t');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHorizontalRule(ReadOnlySpan<char> t)
    {
        if (t.Length < 3)
            return false;
        char c = t[0];
        return (c == '-' || c == '*' || c == '_') && t.Trim(c).Length == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTableSeparator(ReadOnlySpan<char> t)
    {
        var s = t.Trim();
        if (s.Length < 2)
            return false;
        bool hasDash = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '-')
                hasDash = true;
            else if (s[i] != ':' && s[i] != '|' && s[i] != ' ')
                return false;
        }
        return hasDash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHeading(ReadOnlySpan<char> t)
    {
        int i = 0;
        while (i < t.Length && i < 7 && t[i] == '#')
            i++;
        return i > 0 && i <= 6 && (i >= t.Length || t[i] == ' ');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIndentedCodeLine(ReadOnlySpan<char> line)
    {
        if (line.Length < 4)
            return false;
        return (line[0] == ' ' && line[1] == ' ' && line[2] == ' ' && line[3] == ' ')
            || line[0] == '\t';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBlockStart(ReadOnlySpan<char> t)
    {
        if (t.Length == 0)
            return false;
        return t.StartsWith("#")
            || t.StartsWith(">")
            || t.StartsWith("```")
            || t.StartsWith("$$")
            || IsListStart(t)
            || IsHorizontalRule(t)
            || IsIndentedCodeLine(t)
            || (
                t[0] == '<'
                && t.Length > 1
                && (char.IsLetter(t[1]) || t[1] == '/' || t[1] == '!' || t[1] == '?')
            );
    }
}

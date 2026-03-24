namespace MarkdownEditor.Core;

/// <summary>
/// 源码中的一段连续区间，使用相对于所属文本（如一行、一个块文本等）的起始偏移和长度表示。
/// 对于编辑区高亮而言，常用的是“相对于当前行字符串的列号”。
/// </summary>
public readonly struct SourceSpan
{
    public int Start { get; init; }
    public int Length { get; init; }

    public int End => Start + Length;

    public SourceSpan(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public static readonly SourceSpan Empty = new(0, 0);

    public bool IsEmpty => Length <= 0;
}

/// <summary>链接引用定义目标（<c>[label]: url "title"</c>）。</summary>
public readonly record struct LinkReferenceDefinition(string Url, string? Title);

/// <summary>解析过程状态：收集链接引用定义，供行内 <c>[text][ref]</c> 消解。</summary>
public sealed class MarkdownParseContext
{
    public Dictionary<string, LinkReferenceDefinition> LinkReferences { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标签折叠空白、去除首尾空白（大小写不敏感匹配由字典承担）。</summary>
    public static string NormalizeReferenceLabel(string label)
    {
        if (string.IsNullOrEmpty(label))
            return "";
        ReadOnlySpan<char> s = label.AsSpan().Trim();
        Span<char> buf = stackalloc char[s.Length];
        int w = 0;
        bool pendingSpace = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                pendingSpace = w > 0;
            }
            else
            {
                if (pendingSpace && w > 0)
                    buf[w++] = ' ';
                pendingSpace = false;
                buf[w++] = s[i];
            }
        }
        return new string(buf[..w]);
    }
}

/// <summary>文档内自动生成的目录占位（由解析器将单独一行的 <c>[TOC]</c> 识别为此节点）。</summary>
public sealed class TableOfContentsNode : MarkdownNode;

/// <summary>
/// Markdown 抽象语法树节点基类
/// </summary>
public abstract class MarkdownNode;

/// <summary>
/// 文档根节点
/// </summary>
public sealed class DocumentNode : MarkdownNode
{
    public required List<MarkdownNode> Children { get; init; }
}

/// <summary>
/// 标题
/// </summary>
public sealed class HeadingNode : MarkdownNode
{
    public required int Level { get; init; }
    public required List<InlineNode> Content { get; init; }
}

/// <summary>
/// 段落
/// </summary>
public sealed class ParagraphNode : MarkdownNode
{
    public required List<InlineNode> Content { get; init; }
}

/// <summary>
/// 代码块
/// </summary>
public sealed class CodeBlockNode : MarkdownNode
{
    public string Language { get; init; } = "";
    public required string Code { get; init; }
}

/// <summary>
/// 引用块
/// </summary>
public sealed class BlockquoteNode : MarkdownNode
{
    public required List<MarkdownNode> Children { get; init; }
}

/// <summary>
/// 无序列表
/// </summary>
public sealed class BulletListNode : MarkdownNode
{
    public required List<ListItemNode> Items { get; init; }
}

/// <summary>
/// 有序列表
/// </summary>
public sealed class OrderedListNode : MarkdownNode
{
    public required List<ListItemNode> Items { get; init; }
    public int StartNumber { get; init; } = 1;
}

/// <summary>
/// 列表项
/// </summary>
public sealed class ListItemNode : MarkdownNode
{
    public bool IsTask { get; init; }
    public bool IsChecked { get; init; }
    public required List<MarkdownNode> Content { get; init; }
}

/// <summary>
/// 表格列对齐方式
/// </summary>
public enum TableAlign { Left, Center, Right }

/// <summary>
/// 表格
/// </summary>
public sealed class TableNode : MarkdownNode
{
    public required List<string> Headers { get; init; }
    public required List<List<string>> Rows { get; init; }
    /// <summary>每列对齐，空表示默认左对齐</summary>
    public List<TableAlign>? ColumnAlignments { get; init; }
}

/// <summary>
/// 分隔线
/// </summary>
public sealed class HorizontalRuleNode : MarkdownNode;

/// <summary>
/// 空行（保留多行换行的视觉效果）
/// </summary>
public sealed class EmptyLineNode : MarkdownNode
{
    public int LineCount { get; init; } = 1;
}

/// <summary>
/// 块级数学公式 ($$ LaTeX $$)
/// </summary>
public sealed class MathBlockNode : MarkdownNode
{
    public required string LaTeX { get; init; }
}

/// <summary>
/// HTML 块 - 原始 HTML 标签
/// </summary>
public sealed class HtmlBlockNode : MarkdownNode
{
    public required string RawHtml { get; init; }
}

/// <summary>
/// 定义型列表（名词 : 定义）
/// </summary>
public sealed class DefinitionListNode : MarkdownNode
{
    public required List<DefinitionItemNode> Items { get; init; }
}

/// <summary>
/// 定义型列表项
/// </summary>
public sealed class DefinitionItemNode : MarkdownNode
{
    public required List<InlineNode> Term { get; init; }
    public required List<MarkdownNode> Definitions { get; init; }
}

/// <summary>
/// 脚注引用 [^id]
/// </summary>
public sealed class FootnoteRefNode : InlineNode
{
    public required string Id { get; init; }
}

/// <summary>
/// 脚注定义块
/// </summary>
public sealed class FootnoteDefNode : MarkdownNode
{
    public required string Id { get; init; }
    public required List<MarkdownNode> Content { get; init; }
}

/// <summary>
/// 归一化后的脚注引用标记（已编号），用于渲染为上标并跳转到文末脚注区。
/// </summary>
public sealed class FootnoteMarkerNode : InlineNode
{
    public required string Id { get; init; }
    public required int Number { get; init; }
}

/// <summary>
/// 归一化后的文末脚注区（由渲染引擎在解析后拼装插入）。
/// </summary>
public sealed class FootnoteSectionNode : MarkdownNode
{
    public required List<FootnoteEntry> Items { get; init; }
}

public sealed class FootnoteEntry
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    public required List<MarkdownNode> Content { get; init; }
}

/// <summary>
/// 行内元素基类
/// </summary>
public abstract class InlineNode
{
    /// <summary>
    /// 该行内节点在其所属输入文本中的源码区间。
    /// - 对于编辑区行级高亮，约定 Start 为列号，Length 为列数；
    /// - 当为默认值或 Length &lt;= 0 时，表示“未绑定具体源码位置”。
    /// </summary>
    public SourceSpan Span { get; set; }
}

/// <summary>
/// 纯文本
/// </summary>
public sealed class TextNode : InlineNode
{
    public required string Content { get; init; }
}

/// <summary>
/// 加粗
/// </summary>
public sealed class BoldNode : InlineNode
{
    public required List<InlineNode> Content { get; init; }
}

/// <summary>
/// 斜体
/// </summary>
public sealed class ItalicNode : InlineNode
{
    public required List<InlineNode> Content { get; init; }
}

/// <summary>
/// 删除线
/// </summary>
public sealed class StrikethroughNode : InlineNode
{
    public required List<InlineNode> Content { get; init; }
}

/// <summary>
/// 行内代码
/// </summary>
public sealed class CodeNode : InlineNode
{
    public required string Content { get; init; }
}

/// <summary>
/// 链接
/// </summary>
public sealed class LinkNode : InlineNode
{
    public required string Text { get; init; }
    public required string Url { get; init; }
    public string? Title { get; init; }
}

/// <summary>
/// 图片
/// </summary>
public sealed class ImageNode : InlineNode
{
    public required string Alt { get; init; }
    public required string Url { get; init; }
    public string? Title { get; init; }
}

/// <summary>
/// 行内数学公式 (LaTeX)
/// </summary>
public sealed class MathInlineNode : InlineNode
{
    public required string LaTeX { get; init; }
}

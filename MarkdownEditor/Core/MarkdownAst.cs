namespace MarkdownEditor.Core;

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
/// 行内元素基类
/// </summary>
public abstract class InlineNode;

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

using SkiaSharp;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 单次绘制运行 - 文本或占位
/// 用于布局结果，支持高效批量渲染
/// </summary>
public readonly struct LayoutRun
{
    public string Text { get; }
    public SKRect Bounds { get; }
    public RunStyle Style { get; }
    public int BlockIndex { get; }
    public int CharOffset { get; }
    public string? LinkUrl { get; }
    /// <summary>表格单元格水平对齐，仅对 TableHeaderCell/TableCell 有效。</summary>
    public TableCellAlign? TableAlign { get; }
    /// <summary>脚注引用 id（仅 RunStyle.FootnoteRef 时有效），用于收集引用位置以生成底部 ↩︎ 回链。</summary>
    public string? FootnoteRefId { get; }

    public LayoutRun(
        string text,
        SKRect bounds,
        RunStyle style,
        int blockIndex,
        int charOffset,
        string? linkUrl = null,
        TableCellAlign? tableAlign = null,
        string? footnoteRefId = null
    )
    {
        Text = text ?? "";
        Bounds = bounds;
        Style = style;
        BlockIndex = blockIndex;
        CharOffset = charOffset;
        LinkUrl = linkUrl;
        TableAlign = tableAlign;
        FootnoteRefId = footnoteRefId;
    }
}

/// <summary>表格单元格水平对齐，对应 Markdown :--- / :---: / ---: </summary>
public enum TableCellAlign { Left, Center, Right }

public enum RunStyle
{
    Normal,
    Bold,
    Italic,
    /// <summary>粗体+斜体（如 <c>***text***</c>）。</summary>
    BoldItalic,
    Strikethrough,
    Code,
    /// <summary>代码块内：关键字（语法高亮）</summary>
    CodeKeyword,
    /// <summary>代码块内：字符串</summary>
    CodeString,
    /// <summary>代码块内：注释</summary>
    CodeComment,
    /// <summary>代码块内：数字</summary>
    CodeNumber,
    /// <summary>代码块内：默认/其他</summary>
    CodeDefault,
    Link,
    /// <summary>加粗链接（嵌套强调时保留可点击与下划线）。</summary>
    LinkBold,
    /// <summary>斜体链接。</summary>
    LinkItalic,
    /// <summary>粗斜链接。</summary>
    LinkBoldItalic,
    Image,
    Heading1,
    Heading2,
    Heading3,
    Heading4,
    Heading5,
    Heading6,
    Math,
    TableHeaderCell,
    TableCell,
    FootnoteRef
}

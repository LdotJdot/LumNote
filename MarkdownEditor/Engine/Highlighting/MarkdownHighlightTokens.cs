namespace MarkdownEditor.Engine.Highlighting;

/// <summary>
/// 高亮种类（先实现一批常用的）
/// </summary>
public enum HighlightKind
{
    Normal,
    Heading,
    Strong,
    Emphasis,
    Strikethrough,
    CodeInline,
    CodeBlock,
    Link,
    ListMarker,
    BlockquoteMarker,
    TableSeparator,
    HorizontalRule,
    MathInline,
    MathBlock,
    FootnoteRef,
    Image
}

/// <summary>
/// 单个高亮 token：定位到文档中的行和列范围
/// </summary>
public readonly struct HighlightToken
{
    public int Line { get; }
    public int StartColumn { get; }
    public int Length { get; }
    public HighlightKind Kind { get; }

    public HighlightToken(int line, int startColumn, int length, HighlightKind kind)
    {
        Line = line;
        StartColumn = startColumn;
        Length = length;
        Kind = kind;
    }
}


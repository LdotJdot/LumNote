namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 块区间 - 轻量描述符，用于虚拟化
/// 只存行范围，AST 惰性解析
/// </summary>
public readonly struct BlockSpan
{
    public int StartLine { get; }
    public int EndLine { get; }
    public BlockKind Kind { get; }

    public BlockSpan(int start, int end, BlockKind kind)
    {
        StartLine = start;
        EndLine = end;
        Kind = kind;
    }

    public int LineCount => EndLine - StartLine;
}

public enum BlockKind
{
    Paragraph,
    Heading,
    CodeBlock,
    Blockquote,
    List,
    Table,
    HorizontalRule,
    MathBlock,
    HtmlBlock,
    DefinitionList,
    Footnotes,
    Unknown
}

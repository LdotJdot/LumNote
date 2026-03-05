using MarkdownEditor.Engine.Document;
using SkiaSharp;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 已布局块 - 包含测量后的行与运行
/// 供渲染器批量绘制，支持选区命中
/// </summary>
public sealed class LayoutBlock
{
    public int BlockIndex { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public SKRect Bounds { get; set; }
    public List<LayoutLine> Lines { get; } = [];
    public BlockKind Kind { get; set; }
    /// <summary> 表格网格信息，用于绘制线框和底纹 </summary>
    public TableLayoutInfo? TableInfo { get; set; }
}

/// <summary>
/// 表格布局信息 - 列宽、行高、单元格矩形
/// </summary>
public sealed class TableLayoutInfo
{
    public float[] ColumnWidths { get; set; } = [];
    public float[] RowHeights { get; set; } = [];
    public int ColCount { get; set; }
    public int RowCount { get; set; }
}

public sealed class LayoutLine
{
    public float Y { get; set; }
    public float Height { get; set; }
    public List<LayoutRun> Runs { get; } = [];
}

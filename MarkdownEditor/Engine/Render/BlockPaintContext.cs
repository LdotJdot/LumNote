using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Layout;
using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 块级绘制上下文 - 传入每个 BlockPainter，提供画布、选区及统一的 Run/选区绘制回调（类似 Chromium 的 PaintContext）。
/// 各块只负责绘制自己的背景与调用 Run 绘制，保证行为一致、易扩展。
/// </summary>
public sealed class BlockPaintContext
{
    public SKCanvas Canvas { get; }
    public float Scale { get; }
    public LayoutBlock Block { get; }
    public SelectionRange? Selection { get; }

    /// <summary>绘制单个 Run（文本/图片/公式等），由 SkiaRenderer 注入实现。</summary>
    public Action<LayoutRun> DrawRun { get; }

    /// <summary>绘制一行的选区高亮，由 SkiaRenderer 注入实现。</summary>
    public Action<LayoutLine> DrawSelectionForLine { get; }

    /// <summary>绘制当前块的块级装饰（引用条、代码背景、表格线等），由 SkiaRenderer 按 Block.Kind 注入。</summary>
    public Action DrawBlockBackground { get; }

    public BlockPaintContext(
        SKCanvas canvas,
        float scale,
        LayoutBlock block,
        SelectionRange? selection,
        Action<LayoutRun> drawRun,
        Action<LayoutLine> drawSelectionForLine,
        Action drawBlockBackground)
    {
        Canvas = canvas;
        Scale = scale;
        Block = block;
        Selection = selection;
        DrawRun = drawRun;
        DrawSelectionForLine = drawSelectionForLine;
        DrawBlockBackground = drawBlockBackground;
    }

    /// <summary>绘制块内所有行与 Run，以及选区高亮（统一内容绘制入口，各 BlockPainter 在绘制完背景后调用）。先画选区再画文字，避免选区矩形盖住文字。</summary>
    public void DrawBlockContent()
    {
        foreach (var line in Block.Lines)
        {
            DrawSelectionForLine(line);
            foreach (var run in line.Runs)
                DrawRun(run);
        }
    }
}

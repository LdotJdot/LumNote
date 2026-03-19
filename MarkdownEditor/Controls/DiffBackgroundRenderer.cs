using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using MarkdownEditor.Models;

namespace MarkdownEditor.Controls;

/// <summary>
/// 在编辑区按行绘制 diff 背景：新增行绿底，删除/修改行红底。已删除行不在正文区占位，避免与当前行重叠，仅由行号区 "-" 表示。
/// </summary>
public sealed class DiffBackgroundRenderer : IBackgroundRenderer
{
    private readonly Func<GitDiffLineMap?> _getLineMap;
    private readonly IBrush _addedBrush;
    private readonly IBrush _removedBrush;

    public DiffBackgroundRenderer(Func<GitDiffLineMap?> getLineMap, IBrush? addedBrush = null, IBrush? removedBrush = null)
    {
        _getLineMap = getLineMap;
        _addedBrush = addedBrush ?? new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x40, 0x00, 0xC0, 0x00));
        _removedBrush = removedBrush ?? new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x50, 0xC0, 0x00, 0x00));
    }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var map = _getLineMap();
        if (map == null || !map.HasAnyDiff) return;
        var document = textView.Document;
        if (document == null) return;
        double fullWidth = 0;
        if (textView is Avalonia.Controls.Control ctrl)
            fullWidth = ctrl.Bounds.Width;
        if (fullWidth <= 0) return;

        foreach (var line in textView.VisualLines)
        {
            var lineNumber = line.FirstDocumentLine?.LineNumber ?? 0;
            if (lineNumber <= 0) continue;
            var kind = map.Get(lineNumber);
            if (kind == GitDiffLineKind.Unchanged) continue;
            var brush = kind == GitDiffLineKind.Added ? _addedBrush : _removedBrush;
            var segs = BackgroundGeometryBuilder.GetRectsFromVisualSegment(textView, line, 0, line.VisualLength);
            foreach (var r in segs)
                drawingContext.DrawRectangle(brush, null, r);
            if (!segs.Any())
            {
                var y = line.VisualTop;
                var h = line.Height;
                drawingContext.DrawRectangle(brush, null, new Rect(0, y, fullWidth, h));
            }
        }

        // 已删除行不再在正文区绘制占位（会与当前行重叠）。删除信息仅通过行号区左侧的 "-" 和当前行的红底表示；
        // 若需查看已删除内容，可依赖版本对比或在下文“已删除内容”面板查看。
    }
}

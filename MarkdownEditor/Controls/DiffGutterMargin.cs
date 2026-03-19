using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using MarkdownEditor.Models;

namespace MarkdownEditor.Controls;

/// <summary>
/// 在行号区左侧绘制 diff 符号：+ 表示新增行，- 表示删除/修改行（含占位删除行）。
/// 需在 SetDiffMode 时插入到 TextArea.LeftMargins 索引 0，退出比对时移除。
/// </summary>
public sealed class DiffGutterMargin : Control
{
    private readonly Func<GitDiffLineMap?> _getLineMap;
    private TextView? _textView;
    private const double MarginWidth = 14;
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x40, 0xC0, 0x40));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x40, 0x40));

    public DiffGutterMargin(Func<GitDiffLineMap?> getLineMap)
    {
        _getLineMap = getLineMap ?? throw new ArgumentNullException(nameof(getLineMap));
        Width = MarginWidth;
        MinWidth = MarginWidth;
        ClipToBounds = true;
    }

    public void AddToTextView(TextView textView)
    {
        _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        _textView.VisualLinesChanged += OnVisualLinesChanged;
    }

    public void RemoveFromTextView(TextView textView)
    {
        if (_textView != null)
        {
            _textView.VisualLinesChanged -= OnVisualLinesChanged;
            _textView = null;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(MarginWidth, availableSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        var map = _getLineMap();
        var textView = _textView;
        if (map == null || !map.HasAnyDiff || textView == null)
            return;

        if (!textView.VisualLinesValid)
            return;

        var typeface = new Typeface("Consolas");
        const double fontSize = 12;
        var visualLines = textView.VisualLines;
        if (visualLines == null || visualLines.Count == 0)
            return;

        // 将文档坐标转换为当前视口：第一个可视行的 VisualTop 即为滚动偏移
        double scrollY = 0;
        try
        {
            if (visualLines.Count > 0)
                scrollY = visualLines[0].VisualTop;
        }
        catch (VisualLinesInvalidException)
        {
            return;
        }

        foreach (var line in visualLines)
        {
            var lineNumber = line.FirstDocumentLine?.LineNumber ?? 0;
            if (lineNumber <= 0) continue;
            var kind = map.Get(lineNumber);
            if (kind == GitDiffLineKind.Unchanged) continue;
            var ch = kind == GitDiffLineKind.Added ? "+" : "-";
            var brush = kind == GitDiffLineKind.Added ? AddedBrush : RemovedBrush;
            var y = line.VisualTop - scrollY;
            var formatted = new FormattedText(
                ch,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                brush);
            context.DrawText(formatted, new Point(2, y));
        }

        if (map.DeletedLines.Count == 0) return;
        var byAnchor = map.DeletedLines
            .GroupBy(d => d.ShowBeforeNewLine <= 0 ? 1 : d.ShowBeforeNewLine)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var kv in byAnchor)
        {
            var beforeLine = kv.Key;
            var list = kv.Value;
            VisualLine? anchorLine = null;
            foreach (var vl in visualLines)
            {
                var num = vl.FirstDocumentLine?.LineNumber ?? 0;
                if (num == beforeLine) { anchorLine = vl; break; }
            }
            if (anchorLine == null) continue;
            double h = anchorLine.Height;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                double y = anchorLine.VisualTop - (list.Count - i) * h - scrollY;
                var formatted = new FormattedText(
                    "-",
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    RemovedBrush);
                context.DrawText(formatted, new Point(2, y));
            }
        }
    }
}

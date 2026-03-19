using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using MarkdownEditor.Services;

namespace MarkdownEditor.Controls;

/// <summary>
/// 比对模式下替代默认行号边距：虚拟行（以 VirtualLinePrefix 开头）不显示行号，与 VS Code 一致。
/// </summary>
public sealed class DiffLineNumberMargin : Control
{
    private TextView? _textView;
    private const double MarginWidth = 36;
    private static readonly IBrush LineNumberBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Typeface Typeface = new("Consolas", FontStyle.Normal);

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
        var textView = _textView;
        var document = textView?.Document;
        if (textView == null || document == null) return;
        if (!textView.VisualLinesValid) return;
        var visualLines = textView.VisualLines;
        if (visualLines == null || visualLines.Count == 0) return;

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
            var lineText = GetLineText(document, lineNumber);
            if (lineText.StartsWith(DiffVirtualLineHelper.VirtualLinePrefix, StringComparison.Ordinal))
                continue;
            int realLineNumber = lineNumber - CountVirtualLinesBefore(document, lineNumber);
            var y = line.VisualTop - scrollY;
            var formatted = new FormattedText(
                realLineNumber.ToString(CultureInfo.InvariantCulture),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface,
                12,
                LineNumberBrush);
            context.DrawText(formatted, new Point(MarginWidth - formatted.Width - 4, y));
        }
    }

    private static string GetLineText(IDocument document, int oneBasedLineNumber)
    {
        try
        {
            var docLine = document.GetLineByNumber(oneBasedLineNumber);
            return document.GetText(docLine.Offset, docLine.Length) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static int CountVirtualLinesBefore(IDocument document, int beforeLineNumber)
    {
        int count = 0;
        for (var i = 1; i < beforeLineNumber; i++)
        {
            if (GetLineText(document, i).StartsWith(DiffVirtualLineHelper.VirtualLinePrefix, StringComparison.Ordinal))
                count++;
        }
        return count;
    }
}

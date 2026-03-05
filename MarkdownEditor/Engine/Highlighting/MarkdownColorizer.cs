using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace MarkdownEditor.Engine.Highlighting;

/// <summary>
/// 将 Analyze 产生的 HighlightToken 映射到 AvaloniaEdit 文档行上。
/// </summary>
public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private readonly MarkdownHighlightTheme _theme;
    private IReadOnlyList<HighlightToken> _tokens = Array.Empty<HighlightToken>();

    public MarkdownColorizer(MarkdownHighlightTheme theme)
    {
        _theme = theme;
    }

    public void UpdateTokens(IReadOnlyList<HighlightToken> tokens)
    {
        _tokens = tokens ?? Array.Empty<HighlightToken>();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_tokens.Count == 0)
            return;

        int lineNumber = line.LineNumber - 1; // AvaloniaEdit 行号从 1 开始
        var lineTokens = _tokens.Where(t => t.Line == lineNumber).ToList();
        if (lineTokens.Count == 0)
            return;

        foreach (var token in lineTokens)
        {
            int startOffset = line.Offset + token.StartColumn;
            int endOffset = Math.Min(startOffset + token.Length, line.EndOffset);
            if (endOffset <= startOffset)
                continue;

            ChangeLinePart(startOffset, endOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(GetBrush(token.Kind));
                if (token.Kind is HighlightKind.Heading or HighlightKind.Strong)
                {
                    element.TextRunProperties.SetTypeface(
                        new Typeface(
                            element.TextRunProperties.Typeface.FontFamily,
                            FontStyle.Normal,
                            FontWeight.Bold));
                }
                else if (token.Kind == HighlightKind.Emphasis)
                {
                    element.TextRunProperties.SetTypeface(
                        new Typeface(
                            element.TextRunProperties.Typeface.FontFamily,
                            FontStyle.Italic,
                            FontWeight.Normal));
                }
            });
        }
    }

    private IBrush GetBrush(HighlightKind kind) =>
        kind switch
        {
            HighlightKind.Heading => _theme.Heading,
            HighlightKind.Strong => _theme.Strong,
            HighlightKind.Emphasis => _theme.Emphasis,
            HighlightKind.Strikethrough => _theme.Strikethrough,
            HighlightKind.CodeInline => _theme.CodeInline,
            HighlightKind.CodeBlock => _theme.CodeBlock,
            HighlightKind.Link => _theme.Link,
            HighlightKind.ListMarker => _theme.ListMarker,
            HighlightKind.BlockquoteMarker => _theme.BlockquoteMarker,
            HighlightKind.TableSeparator => _theme.TableSeparator,
            HighlightKind.MathInline => _theme.Math,
            HighlightKind.MathBlock => _theme.Math,
            HighlightKind.FootnoteRef => _theme.Link,
            HighlightKind.Image => _theme.Link,
            _ => _theme.Foreground
        };
}


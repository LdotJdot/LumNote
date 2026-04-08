using System.Collections.Generic;
using MarkdownEditor.Core;
using SkiaSharp;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 布局环境 - 为块级布局提供统一的测量、字体、换行等能力（类似 Chromium 的 LayoutBox 依赖的 Font/Text 服务）。
/// 由布局引擎实现，各 BlockLayouter 通过上下文获取，保证扩展性与稳定测量。
/// </summary>
public interface ILayoutEnvironment
{
    float BaseFontSize { get; }
    float LineSpacing { get; }
    float ParaSpacing { get; }
    float BlockInnerPadding { get; }
    float CodeBlockPadding { get; }
    float BlockquotePadding { get; }
    float BlockquoteIndent { get; }
    float ListItemIndent { get; }
    float ListBlockMarginTop { get; }
    float ListBlockMarginBottom { get; }
    float ListItemGap { get; }
    float FootnoteTopMargin { get; }
    float DefinitionListIndent { get; }

    SKTypeface GetBodyTypeface();
    SKTypeface GetMathTypeface();
    SKFont GetFont(bool bold, bool italic, bool code);
    SKPaint GetMeasurePaint();

    (float width, float height, float depth) MeasureMathMetrics(string latex);
    float MeasureMathInline(string latex);
    (float w, float h) GetImageIntrinsicSize(string url, float maxWidth);

    IReadOnlyList<string> BreakTextWithWrap(string text, float maxWidth, SKFont font);
    string FlattenInlines(IReadOnlyList<InlineNode> nodes);
    (string text, RunStyle style, string? linkUrl, string? footnoteRefId) FlattenInline(InlineNode n);

    int MeasureTextOffset(string text, float x, RunStyle style);
}

using MarkdownEditor.Core;

namespace MarkdownEditor.Export;

/// <summary>
/// 统一导出选项（页宽、边距、主题样式等）。
/// </summary>
public record ExportOptions(
    int? PageWidthPx = null,
    float? MarginPt = null,
    /// <summary>当前预览主题样式，为 null 时使用默认深色。</summary>
    MarkdownStyleConfig? StyleConfig = null,
    /// <summary>导出进度回调；在后台线程也可能触发，由 <see cref="Progress{T}"/> 投递到 UI 线程。</summary>
    IProgress<ExportProgress>? Progress = null,
    /// <summary>
    /// PDF/长图导出时插图解码最长边（像素）。为 null 时按页宽与引擎默认比例推算。
    /// </summary>
    int? ExportImageMaxLongEdgePx = null,
    /// <summary>为 true 时插图按原分辨率解码（PDF 中嵌入位图会很大，仅用于需要高分辨率嵌入图时）。</summary>
    bool ExportImageFullResolution = false
);

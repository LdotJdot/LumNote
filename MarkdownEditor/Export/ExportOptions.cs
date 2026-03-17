using MarkdownEditor.Core;

namespace MarkdownEditor.Export;

/// <summary>
/// 统一导出选项（页宽、边距、主题样式等）。
/// </summary>
public record ExportOptions(
    int? PageWidthPx = null,
    float? MarginPt = null,
    /// <summary>当前预览主题样式，为 null 时使用默认深色。</summary>
    MarkdownStyleConfig? StyleConfig = null
);

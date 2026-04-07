using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Render;

namespace MarkdownEditor.Export;

/// <summary>
/// 导出场景下为 <see cref="BasePathImageLoader"/> 配置插图解码（降采样以减小 PDF/长图中嵌入位图体积）。
/// </summary>
internal static class ExportImageDecode
{
    public static void ConfigureForPdf(
        BasePathImageLoader imageLoader,
        EngineConfig config,
        float pageWidthPx,
        ExportOptions? options)
    {
        bool preferFull = options?.ExportImageFullResolution == true;
        if (preferFull)
        {
            imageLoader.ConfigurePreviewDecode(Math.Max(256, config.ImagePreviewMaxLongEdgeCap), true);
            return;
        }

        if (options?.ExportImageMaxLongEdgePx is int e && e > 0)
        {
            imageLoader.ConfigurePreviewDecode(e, false);
            return;
        }

        var exportBudget = (int)
            Math.Clamp(
                Math.Ceiling(pageWidthPx * Math.Max(1f, config.ImagePreviewViewportScale)),
                256,
                Math.Max(256, config.ImagePreviewMaxLongEdgeCap));
        imageLoader.ConfigurePreviewDecode(exportBudget, false);
    }

    public static void ConfigureForLongImage(
        BasePathImageLoader imageLoader,
        EngineConfig config,
        float widthPx,
        ExportOptions? options)
    {
        bool preferFull = options?.ExportImageFullResolution == true;
        if (preferFull)
        {
            imageLoader.ConfigurePreviewDecode(Math.Max(256, config.ImagePreviewMaxLongEdgeCap), true);
            return;
        }

        if (options?.ExportImageMaxLongEdgePx is int e && e > 0)
        {
            imageLoader.ConfigurePreviewDecode(e, false);
            return;
        }

        var exportBudget = (int)
            Math.Clamp(Math.Ceiling(widthPx * 2f), 256, Math.Max(256, config.ImagePreviewMaxLongEdgeCap));
        imageLoader.ConfigurePreviewDecode(exportBudget, false);
    }
}

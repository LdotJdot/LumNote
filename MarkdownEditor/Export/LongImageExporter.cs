using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Export;

/// <summary>
/// 将 Markdown 渲染为整页长图（PNG）。
/// </summary>
public sealed class LongImageExporter : IMarkdownExporter
{
    private const float DefaultWidth = 800f;

    public string FormatId => "png";
    public string DisplayName => "长图 (PNG)";
    public string[] FileExtensions => ["png"];

    public Task<ExportResult> ExportAsync(
        string markdown,
        string documentBasePath,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            var width = options?.PageWidthPx ?? (int)DefaultWidth;
            if (width < 100) width = (int)DefaultWidth;

            var config = EngineConfig.FromStyle(options?.StyleConfig) ?? new EngineConfig();
            var imageLoader = new BasePathImageLoader(documentBasePath ?? "");
            var doc = new StringDocumentSource(markdown ?? "");
            var engine = new RenderEngine(width, config, imageLoader);
            engine.EnsureFullLayout(doc);
            float totalHeight = engine.MeasureTotalHeight(doc);
            if (totalHeight <= 0) totalHeight = 100;

            int w = (int)width;
            int h = (int)Math.Ceiling((double)totalHeight);
            if (h <= 0) h = 100;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface == null)
                return Task.FromResult(new ExportResult(false, "无法创建绘图表面。"));

            var canvas = surface.Canvas;
            // 使用页面背景色清空画布，与预览区一致；若用 TextColor 会导致文字与背景同色不可见
            var bg = config.PageBackground;
            canvas.Clear(new SKColor(
                (byte)((bg >> 16) & 0xFF),
                (byte)((bg >> 8) & 0xFF),
                (byte)(bg & 0xFF),
                (byte)((bg >> 24) & 0xFF)));

            var ctx = new ExportSkiaRenderContext
            {
                Canvas = canvas,
                Size = new SKSize(w, h),
                Scale = 1f
            };
            engine.Render(ctx, doc, scrollY: 0, viewportHeight: totalHeight + 500, null);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null)
                return Task.FromResult(new ExportResult(false, "PNG 编码失败。"));

            using var stream = File.OpenWrite(outputPath);
            data.SaveTo(stream);
            return Task.FromResult(new ExportResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ExportResult(false, ex.Message));
        }
    }

    /// <summary>
    /// 供导出使用的 Skia 渲染上下文（仅 Engine 层，不依赖 Avalonia Controls）。
    /// </summary>
    private sealed class ExportSkiaRenderContext : ISkiaRenderContext
    {
        public SKCanvas Canvas { get; set; } = null!;
        public SKSize Size { get; set; }
        public float Scale { get; set; } = 1f;
    }
}

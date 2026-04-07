using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Export;

/// <summary>
/// 将 Markdown 渲染为分页 PDF。
/// </summary>
/// <remarks>
/// 正文经 Skia PDF 后端以矢量文本与路径绘制。若将来需要更细的 PDF 特性（复杂字体子集策略、
/// 标签化结构树等），可评估基于布局引擎产出的块与行盒坐标，用 iText、PdfSharp 等单独写入 PDF 文本与矢量指令。
/// </remarks>
public sealed class PdfExporter : IMarkdownExporter
{
    /// <summary>A4 尺寸（点，72 DPI）— 仅作为未指定页宽时的回退值</summary>
    private const float DefaultPageWidthPt = 595f;
    private const float DefaultPageHeightPt = 842f;

    public string FormatId => "pdf";
    public string DisplayName => "PDF";
    public string[] FileExtensions => ["pdf"];

    public Task<ExportResult> ExportAsync(
        string markdown,
        string documentBasePath,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            float pageWidth = options?.PageWidthPx is > 0 ? (float)options.PageWidthPx.Value : DefaultPageWidthPt;
            float pageHeight = pageWidth * DefaultPageHeightPt / DefaultPageWidthPt;

            options.ReportProgress("正在准备 PDF 渲染…", 5);
            var config = EngineConfig.FromStyle(options?.StyleConfig) ?? new EngineConfig();
            config.EnableBlockPictureCache = false;
            var imageLoader = new BasePathImageLoader(
                documentBasePath ?? "",
                Math.Clamp(config.ImagePreviewCacheMaxEntries, 4, 256));
            ExportImageDecode.ConfigureForPdf(imageLoader, config, pageWidth, options);
            var doc = new StringDocumentSource(markdown ?? "");
            var engine = new RenderEngine(pageWidth, config, imageLoader);
            options.ReportProgress("正在布局文档…", 15);
            engine.EnsureFullLayout(doc);
            float totalHeight = engine.MeasureTotalHeight(doc);
            if (totalHeight <= 0) totalHeight = pageHeight;

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var docPdf = SKDocument.CreatePdf(stream);

            float scrollY = 0;
            int pageIndex = 0;
            int estimatedPages = Math.Max(1, (int)Math.Ceiling(totalHeight / pageHeight));
            while (scrollY < totalHeight)
            {
                ct.ThrowIfCancellationRequested();
                pageIndex++;
                double pagePct = 15 + 75.0 * pageIndex / estimatedPages;
                options.ReportProgress($"正在写入 PDF 第 {pageIndex} 页…", Math.Clamp(pagePct, 15, 95));
                using var canvas = docPdf.BeginPage(pageWidth, pageHeight);
                var bg = config.PageBackground;
                canvas.Clear(new SKColor(
                    (byte)((bg >> 16) & 0xFF),
                    (byte)((bg >> 8) & 0xFF),
                    (byte)(bg & 0xFF),
                    (byte)((bg >> 24) & 0xFF)));

                var ctx = new PdfSkiaContext
                {
                    Canvas = canvas,
                    Size = new SKSize(pageWidth, pageHeight),
                    Scale = 1f
                };
                engine.Render(ctx, doc, scrollY, pageHeight, null, out float nextScrollY, fullBlocksOnly: true);
                docPdf.EndPage();
                if (nextScrollY <= scrollY)
                    nextScrollY = scrollY + pageHeight;
                scrollY = nextScrollY;
            }

            docPdf.Close();
            options.ReportProgress("正在完成 PDF…", 98);
            return Task.FromResult(new ExportResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ExportResult(false, ex.Message));
        }
    }

    private sealed class PdfSkiaContext : ISkiaRenderContext
    {
        public SKCanvas Canvas { get; set; } = null!;
        public SKSize Size { get; set; }
        public float Scale { get; set; } = 1f;
        public bool PreferDirectDraw => true;
    }
}

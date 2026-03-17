using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Export;

/// <summary>
/// 将 Markdown 渲染为分页 PDF。
/// </summary>
public sealed class PdfExporter : IMarkdownExporter
{
    /// <summary>A4 尺寸（点，72 DPI）</summary>
    private const float PageWidthPt = 595f;
    private const float PageHeightPt = 842f;

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
            var config = EngineConfig.FromStyle(options?.StyleConfig) ?? new EngineConfig();
            var imageLoader = new BasePathImageLoader(documentBasePath ?? "");
            var doc = new StringDocumentSource(markdown ?? "");
            var engine = new RenderEngine(PageWidthPt, config, imageLoader);
            engine.EnsureFullLayout(doc);
            float totalHeight = engine.MeasureTotalHeight(doc);
            if (totalHeight <= 0) totalHeight = PageHeightPt;

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var docPdf = SKDocument.CreatePdf(stream);

            float scrollY = 0;
            while (scrollY < totalHeight)
            {
                using var canvas = docPdf.BeginPage(PageWidthPt, PageHeightPt);
                // 使用页面背景色清空画布，与预览区一致；若用 TextColor 会导致文字与背景同色不可见
                var bg = config.PageBackground;
                canvas.Clear(new SKColor(
                    (byte)((bg >> 16) & 0xFF),
                    (byte)((bg >> 8) & 0xFF),
                    (byte)(bg & 0xFF),
                    (byte)((bg >> 24) & 0xFF)));

                var ctx = new PdfSkiaContext
                {
                    Canvas = canvas,
                    Size = new SKSize(PageWidthPt, PageHeightPt),
                    Scale = 1f
                };
                engine.Render(ctx, doc, scrollY, PageHeightPt, null, out float nextScrollY, fullBlocksOnly: true);
                docPdf.EndPage();
                if (nextScrollY <= scrollY)
                    nextScrollY = scrollY + PageHeightPt;
                scrollY = nextScrollY;
            }

            docPdf.Close();
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
    }
}

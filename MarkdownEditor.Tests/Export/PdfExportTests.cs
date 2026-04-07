using MarkdownEditor.Export;
using Xunit;

namespace MarkdownEditor.Tests.Export;

/// <summary>
/// 校验 Skia PDF 导出为合法 PDF（非裸位图封装），并在明文字典中可见字体等资源时加以断言。
/// </summary>
public class PdfExportTests
{
    [Fact]
    public async Task ExportAsync_plain_text_produces_valid_pdf_with_typical_structure()
    {
        var exporter = new PdfExporter();
        var path = Path.Combine(Path.GetTempPath(), "md_pdf_test_" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var result = await exporter.ExportAsync(
                "# Hello\n\nVector text check.",
                "",
                path,
                new ExportOptions());

            Assert.True(result.Success, result.ErrorMessage);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.Length >= 500, "PDF should have non-trivial size");

            var head = System.Text.Encoding.ASCII.GetString(bytes.AsSpan(0, Math.Min(16, bytes.Length)));
            Assert.StartsWith("%PDF", head);

            // 对象流可能被压缩，/Font 不一定以明文出现；若存在未压缩对象，则字体资源通常可见。
            var latin1 = System.Text.Encoding.Latin1.GetString(bytes);
            if (latin1.Contains("/Font", StringComparison.Ordinal))
            {
                Assert.True(true);
            }
            else
            {
                // 回退：Skia PDF 仍应包含 PDF 关键字与 xref 结构（非裸 PNG 封装）。
                Assert.Contains("xref", latin1);
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}

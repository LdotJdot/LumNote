namespace MarkdownEditor.Export;

/// <summary>
/// Markdown 导出器统一接口。
/// </summary>
public interface IMarkdownExporter
{
    /// <summary>格式标识，如 "html", "pdf", "png"。</summary>
    string FormatId { get; }

    /// <summary>显示名称，用于菜单与对话框。</summary>
    string DisplayName { get; }

    /// <summary>推荐的文件扩展名，如 [ "html", "htm" ]。</summary>
    string[] FileExtensions { get; }

    /// <summary>
    /// 执行导出。
    /// </summary>
    /// <param name="markdown">当前文档 Markdown 内容。</param>
    /// <param name="documentBasePath">文档所在目录，用于解析相对路径（如图片）。</param>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="options">可选导出选项。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ExportResult> ExportAsync(
        string markdown,
        string documentBasePath,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default);
}

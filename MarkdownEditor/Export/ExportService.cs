using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Export;

/// <summary>
/// 导出服务：按格式选择导出器，弹出保存对话框并执行导出。
/// </summary>
public sealed class ExportService
{
    private readonly IReadOnlyList<IMarkdownExporter> _exporters;

    public ExportService(IReadOnlyList<IMarkdownExporter> exporters)
    {
        _exporters = exporters ?? new List<IMarkdownExporter>();
    }

    public IReadOnlyList<IMarkdownExporter> Exporters => _exporters;

    /// <summary>
    /// 根据 FormatId 获取导出器。
    /// </summary>
    public IMarkdownExporter? GetExporter(string formatId)
    {
        return _exporters.FirstOrDefault(e => string.Equals(e.FormatId, formatId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 弹出保存对话框并执行导出。取消或未选路径返回 null；成功返回 true；失败返回 false 且 errorMessage 有值。
    /// </summary>
    /// <param name="renderAreaWidthPx">当前渲染区内容宽度（像素），用于 PDF/长图等导出的页宽；为 null 时使用各导出器默认值。</param>
    /// <param name="progress">导出进度；在保存对话框关闭后、实际写入文件前开始报告。</param>
    public async Task<(bool? Success, string? ErrorMessage)> ExportWithDialogAsync(
        MainViewModel vm,
        string formatId,
        IStorageProvider storageProvider,
        int? renderAreaWidthPx = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var exporter = GetExporter(formatId);
        if (exporter == null)
        {
            return (false, "未找到该格式的导出器。");
        }

        var suggestedName = string.IsNullOrEmpty(vm.CurrentFileName)
            ? "导出"
            : System.IO.Path.ChangeExtension(vm.CurrentFileName, exporter.FileExtensions.FirstOrDefault()) ?? "导出";

        var options = new FilePickerSaveOptions
        {
            Title = "导出为 " + exporter.DisplayName,
            SuggestedFileName = suggestedName,
            DefaultExtension = exporter.FileExtensions.FirstOrDefault() ?? "",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(exporter.DisplayName)
                {
                    Patterns = exporter.FileExtensions.Select(ext => "*." + ext.TrimStart('.')).ToArray()
                }
            },
            ShowOverwritePrompt = true
        };

        var file = await storageProvider.SaveFilePickerAsync(options);
        if (file == null)
        {
            return (null, null); // 用户取消
        }

        if (file.TryGetLocalPath() is not { } outputPath)
        {
            return (false, "无法获取保存路径。");
        }

        progress?.Report(new ExportProgress("正在导出…", null));

        var exportOptions = new ExportOptions(
            PageWidthPx: renderAreaWidthPx,
            StyleConfig: vm.Config.Markdown,
            Progress: progress);

        var result = await Task.Run(
                () => exporter.ExportAsync(
                    vm.CurrentMarkdown ?? "",
                    vm.DocumentBasePath ?? "",
                    outputPath,
                    exportOptions,
                    ct),
                ct)
            .ConfigureAwait(false);

        progress?.Report(new ExportProgress("完成", 100));

        if (result.Success)
            return (true, null);
        return (false, result.ErrorMessage ?? "导出失败。");
    }
}

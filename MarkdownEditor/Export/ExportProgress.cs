namespace MarkdownEditor.Export;

/// <summary>
/// 导出进度（用于进度条与状态文字）。
/// </summary>
/// <param name="Message">当前步骤说明。</param>
/// <param name="Percent">0–100 的完成百分比；为 null 时表示不确定进度（走马灯）。</param>
public readonly record struct ExportProgress(string Message, double? Percent);

/// <summary>
/// 在 <see cref="ExportOptions"/> 上报告进度的扩展方法。
/// </summary>
public static class ExportProgressExtensions
{
    public static void ReportProgress(this ExportOptions? options, string message, double? percent = null) =>
        options?.Progress?.Report(new ExportProgress(message, percent));
}

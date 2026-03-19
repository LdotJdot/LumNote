using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace MarkdownEditor.Helpers;

/// <summary>
/// 通过反射调用 Avalonia 新剪贴板 API（TryGetDataAsync / TryGetFilesAsync / TryGetBitmapAsync），
/// 以便在未升级到包含该 API 的 Avalonia 版本时仍可编译；运行时若 API 存在则支持粘贴文件和图片。
/// </summary>
/// <remarks>
/// <para><strong>AOT 发布说明</strong>：使用 Native AOT 或激进裁剪（PublishTrimmed）发布时，
/// 反射可能找不到上述方法（目标可能被裁掉或无法通过反射调用），届时粘贴文件/图片会失效，仅保留文本粘贴。</para>
/// <para>若需在 AOT 发布下仍支持粘贴文件和图片，请将 Avalonia 升级至包含新剪贴板 API 的版本（如 11.3+），
/// 并改为直接调用 IClipboard.TryGetDataAsync、IAsyncDataTransfer.TryGetFilesAsync / TryGetBitmapAsync，删除本反射实现。</para>
/// </remarks>
internal static class ClipboardPasteHelper
{
    public sealed record FileOrImageResult(string? FirstFilePath, Bitmap? Bitmap);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async System.Threading.Tasks.Task<FileOrImageResult?> TryGetFileOrImageAsync(object? clipboard)
    {
        if (clipboard == null) return null;
        var clipboardType = clipboard.GetType();
        var tryGetData = clipboardType.GetMethod("TryGetDataAsync", Type.EmptyTypes);
        if (tryGetData == null) return null;

        object? dataTransfer = null;
        try
        {
            var task = tryGetData.Invoke(clipboard, null);
            if (task is not Task t) return null;
            await t.ConfigureAwait(false);
            var resultProp = t.GetType().GetProperty("Result");
            dataTransfer = resultProp?.GetValue(t);
            if (dataTransfer == null) return null;

            // TryGetFilesAsync() on IAsyncDataTransfer
            var tryGetFiles = dataTransfer.GetType().GetMethod("TryGetFilesAsync", Type.EmptyTypes);
            if (tryGetFiles != null)
            {
                var filesTask = tryGetFiles.Invoke(dataTransfer, null);
                if (filesTask is Task ft)
                {
                    await ft.ConfigureAwait(false);
                    var arr = ft.GetType().GetProperty("Result")?.GetValue(ft) as Array;
                    if (arr != null && arr.Length > 0)
                    {
                        var first = arr.GetValue(0);
                        var tryGetPath = first?.GetType().GetMethod("TryGetLocalPath", Type.EmptyTypes);
                        if (tryGetPath?.Invoke(first, null) is string path && !string.IsNullOrEmpty(path))
                            return new FileOrImageResult(path, null);
                    }
                }
            }

            // TryGetBitmapAsync() on IAsyncDataTransfer
            var tryGetBitmap = dataTransfer.GetType().GetMethod("TryGetBitmapAsync", Type.EmptyTypes);
            if (tryGetBitmap != null)
            {
                var bitmapTask = tryGetBitmap.Invoke(dataTransfer, null);
                if (bitmapTask is Task bt)
                {
                    await bt.ConfigureAwait(false);
                    if (bt.GetType().GetProperty("Result")?.GetValue(bt) is Bitmap bmp)
                        return new FileOrImageResult(null, bmp);
                }
            }
        }
        finally
        {
            if (dataTransfer is IAsyncDisposable asyncD)
                await asyncD.DisposeAsync().ConfigureAwait(false);
            else if (dataTransfer is IDisposable d)
                d.Dispose();
        }

        return null;
    }
}

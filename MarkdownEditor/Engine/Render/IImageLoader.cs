using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 图片加载器 - 用于从 URL 或路径加载图片
/// </summary>
public interface IImageLoader : IDisposable
{
    /// <summary>
    /// 尝试获取已加载的图片，若未加载则返回 null
    /// </summary>
    /// <remarks>
    /// 返回的位图由加载器缓存持有，可能与缓存淘汰并发而不宜在锁外长期使用；
    /// 布局与绘制应优先使用 <see cref="WithImage"/>。
    /// </remarks>
    SKBitmap? TryGetImage(string url);

    /// <summary>
    /// 在加载器内部同步块内调用 <paramref name="action"/>，避免访问 <see cref="SKBitmap"/> 时与缓存淘汰的 <see cref="IDisposable.Dispose"/> 并发。
    /// </summary>
    void WithImage(string url, Action<SKBitmap?> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrEmpty(url))
        {
            action(null);
            return;
        }
        action(TryGetImage(url));
    }

    /// <summary>
    /// 在已解码时返回像素宽高（托管数据）；布局线程应优先使用本方法，避免在测量路径上反复访问 Skia 原生位图属性。
    /// </summary>
    bool TryGetImagePixelSize(string url, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrEmpty(url))
            return false;
        var ok = false;
        var w = 0;
        var h = 0;
        WithImage(url, bmp =>
        {
            if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0)
                return;
            w = bmp.Width;
            h = bmp.Height;
            ok = true;
        });
        if (ok)
        {
            width = w;
            height = h;
        }
        return ok;
    }

    /// <summary>
    /// 按视口/导出需求配置嵌入预览的解码最长边（档位量化）及是否优先全分辨率解码。
    /// </summary>
    void ConfigurePreviewDecode(int maxLongEdgePixels, bool preferFullDecode = false) { }

    /// <summary>
    /// 异步加载完成后触发，便于界面重绘（可选实现）。
    /// </summary>
    event Action? ImageLoaded;

    /// <summary>
    /// 释放加载器持有的缓存与非托管资源（如位图像素缓冲）。
    /// </summary>
    void IDisposable.Dispose() { }
}

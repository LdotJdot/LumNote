using MarkdownEditor.Core;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 基于文档目录解析相对路径的图片加载器，用于导出等场景。
/// </summary>
public sealed class BasePathImageLoader : IImageLoader
{
    private readonly string _basePath;
    private readonly DefaultImageLoader _inner;

    public BasePathImageLoader(string basePath, int maxCachedPreviewImages = 28)
    {
        _basePath = basePath ?? "";
        _inner = new DefaultImageLoader(maxCachedPreviewImages);
    }

    public event Action? ImageLoaded
    {
        add => _inner.ImageLoaded += value;
        remove => _inner.ImageLoaded -= value;
    }

    private string ResolveInnerKey(string url)
    {
        var raw = PathSanitizer.Sanitize(url);
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>')
            raw = raw[1..^1].Trim();
        var resolved = raw;
        if (DefaultImageLoader.IsNetworkImageUrl(raw) || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return resolved;

        if (!Path.IsPathRooted(raw) && !string.IsNullOrEmpty(_basePath))
        {
            var sep = raw.Replace('/', Path.DirectorySeparatorChar);
            try
            {
                var baseFull = Path.GetFullPath(_basePath.TrimEnd(Path.DirectorySeparatorChar, '/'));
                resolved = Path.GetFullPath(Path.Combine(baseFull, sep));
            }
            catch
            {
                resolved = Path.GetFullPath(Path.Combine(_basePath, sep));
            }
        }
        return resolved;
    }

    public SkiaSharp.SKBitmap? TryGetImage(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        return _inner.TryGetImage(ResolveInnerKey(url));
    }

    public void WithImage(string url, Action<SkiaSharp.SKBitmap?> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrEmpty(url))
        {
            action(null);
            return;
        }
        _inner.WithImage(ResolveInnerKey(url), action);
    }

    public bool TryGetImagePixelSize(string url, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrEmpty(url))
            return false;
        return _inner.TryGetImagePixelSize(ResolveInnerKey(url), out width, out height);
    }

    public void ConfigurePreviewDecode(int maxLongEdgePixels, bool preferFullDecode = false) =>
        _inner.ConfigurePreviewDecode(maxLongEdgePixels, preferFullDecode);

    public void Dispose() => _inner.Dispose();
}

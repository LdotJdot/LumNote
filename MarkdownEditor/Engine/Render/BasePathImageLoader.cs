using MarkdownEditor.Core;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 基于文档目录解析相对路径的图片加载器，用于导出等场景。
/// </summary>
public sealed class BasePathImageLoader : IImageLoader
{
    private readonly string _basePath;
    private readonly DefaultImageLoader _inner = new();

    public BasePathImageLoader(string basePath)
    {
        _basePath = basePath ?? "";
    }

    public event Action? ImageLoaded
    {
        add => _inner.ImageLoaded += value;
        remove => _inner.ImageLoaded -= value;
    }

    public SkiaSharp.SKBitmap? TryGetImage(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var raw = PathSanitizer.Sanitize(url);
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>')
            raw = raw[1..^1].Trim();
        var resolved = raw;
        if (DefaultImageLoader.IsNetworkImageUrl(raw) || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return _inner.TryGetImage(resolved);

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
        return _inner.TryGetImage(resolved);
    }
}

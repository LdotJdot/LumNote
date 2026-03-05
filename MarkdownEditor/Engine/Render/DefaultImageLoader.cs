using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 默认图片加载器 - 本地文件同步加载（立显），网络 URL 异步加载并触发 ImageLoaded 便于重绘
/// </summary>
public sealed class DefaultImageLoader : IImageLoader
{
    private readonly ConcurrentDictionary<string, SKBitmap?> _cache = new();

    public event Action? ImageLoaded;

    public SKBitmap? TryGetImage(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        if (_cache.TryGetValue(url, out var cached))
            return cached;

        var isHttp =
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!isHttp)
        {
            // 本地路径：同步加载，首次即可显示
            var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? url[7..]
                : url;
            SKBitmap? bmp = null;
            try
            {
                if (File.Exists(path))
                    bmp = SKBitmap.Decode(path);
            }
            catch { }
            _cache[url] = bmp;
            return bmp;
        }

        // 网络 URL：先占位，异步加载完成后触发 ImageLoaded 以便重绘
        _cache[url] = null;
        _ = Task.Run(async () =>
        {
            SKBitmap? bmp = null;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);
                bmp = SKBitmap.Decode(bytes);
            }
            catch { }
            _cache[url] = bmp;
            try
            {
                ImageLoaded?.Invoke();
            }
            catch { }
        });

        return null;
    }
}

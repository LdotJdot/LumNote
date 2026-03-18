using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using MarkdownEditor.Core;
using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 图片加载：仅对 http(s) 与协议相对 // 使用网络请求；其余一律按本地文件用 File 流读取（不走 Http/fetch）。
/// </summary>
public sealed class DefaultImageLoader : IImageLoader
{
    private readonly ConcurrentDictionary<string, SKBitmap?> _cache = new();
    private readonly ConcurrentQueue<string> _evictionOrder = new();
    private readonly object _addLock = new();
    private const int MaxCachedImages = 48;

    public event Action? ImageLoaded;

    private void AddToCache(string url, SKBitmap? bmp)
    {
        lock (_addLock)
        {
            _cache[url] = bmp;
            _evictionOrder.Enqueue(url);
            while (_evictionOrder.TryDequeue(out var oldKey) && _cache.Count > MaxCachedImages)
            {
                if (_cache.TryRemove(oldKey, out var oldBmp) && oldBmp != null)
                    oldBmp.Dispose();
            }
        }
    }

    /// <summary>是否应按网络 URL 加载（仅此路径使用 HttpClient）。</summary>
    internal static bool IsNetworkImageUrl(string u)
    {
        if (string.IsNullOrWhiteSpace(u))
            return false;
        u = u.TrimStart();
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return true;
        // 协议相对：//cdn.example.com/a.png
        if (u.Length > 2
            && u[0] == '/'
            && u[1] == '/'
            && u[2] != '/')
            return true;
        return false;
    }

    /// <summary>Markdown 中的本地路径转为可打开的文件路径（file:、尖括号、百分号编码）。</summary>
    internal static string NormalizeLocalFilePath(string url)
    {
        var s = PathSanitizer.Sanitize(url);
        if (s.Length >= 2 && s[0] == '<' && s[^1] == '>')
            s = s[1..^1].Trim();
        s = s.Trim('"', '\'');

        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(s);
                return uri.LocalPath;
            }
            catch
            {
                var t = s.AsSpan(7).TrimStart('/');
                return Uri.UnescapeDataString(t.ToString().Replace('/', Path.DirectorySeparatorChar));
            }
        }

        if (s.Contains('%', StringComparison.Ordinal))
        {
            try
            {
                s = Uri.UnescapeDataString(s);
            }
            catch { /* 保持原样 */ }
        }

        return s;
    }

    private static SKBitmap? DecodeFileFromDisk(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        if (!File.Exists(path))
            return null;

        try
        {
            using var fs = File.OpenRead(path);
            var b = SKBitmap.Decode(fs);
            if (b != null && b.Width > 0 && b.Height > 0)
                return b;
            b?.Dispose();
        }
        catch { }

        try
        {
            var b = SKBitmap.Decode(File.ReadAllBytes(path));
            if (b != null && b.Width > 0 && b.Height > 0)
                return b;
            b?.Dispose();
        }
        catch { }

        return null;
    }

    public SKBitmap? TryGetImage(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        if (_cache.TryGetValue(url, out var cached))
            return cached;

        var trimmed = PathSanitizer.Sanitize(url);

        if (IsNetworkImageUrl(trimmed))
        {
            var fetchUrl = trimmed.StartsWith("//", StringComparison.Ordinal)
                ? "https:" + trimmed
                : trimmed;
            AddToCache(url, null);
            _ = Task.Run(async () =>
            {
                SKBitmap? bmp = null;
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var bytes = await client.GetByteArrayAsync(fetchUrl).ConfigureAwait(false);
                    bmp = SKBitmap.Decode(bytes);
                }
                catch { }
                lock (_addLock)
                {
                    _cache[url] = bmp;
                    _evictionOrder.Enqueue(url);
                    while (_evictionOrder.TryDequeue(out var oldKey) && _cache.Count > MaxCachedImages)
                    {
                        if (_cache.TryRemove(oldKey, out var oldBmp) && oldBmp != null)
                            oldBmp.Dispose();
                    }
                }
                try
                {
                    ImageLoaded?.Invoke();
                }
                catch { }
            });
            return null;
        }

        // 本地路径：同步文件读取，绝不走 HTTP
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            AddToCache(url, null);
            return null;
        }

        var localPath = NormalizeLocalFilePath(trimmed);
        var bmpLocal = DecodeFileFromDisk(localPath);
        AddToCache(url, bmpLocal);
        return bmpLocal;
    }
}

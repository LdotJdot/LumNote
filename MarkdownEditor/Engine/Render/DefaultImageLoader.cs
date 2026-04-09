using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using MarkdownEditor.Core;
using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 图片加载：网络 URL 异步拉取；本地文件按预览最长边解码以控制内存，点击查看原图由宿主用文件路径全分辨率打开。
/// </summary>
public sealed class DefaultImageLoader : IImageLoader
{
    private sealed class CacheEntry
    {
        public SKBitmap? Bitmap;
        public int IntrinsicW;
        public int IntrinsicH;
        public int DecodedMaxLongEdge;
        public bool FullDecode;
        public bool Loading;
        public long ApproxBytes;
        public LinkedListNode<string>? LruNode;
    }

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly object _addLock = new();
    private readonly int _maxCachedImages;

    private bool _decodeFull;
    private int _previewMaxLongEdge = 1024;
    private bool _disposed;
    private long _currentBytes;

    public event Action? ImageLoaded;

    public DefaultImageLoader(int maxCachedPreviewImages = 28)
    {
        _maxCachedImages = Math.Clamp(maxCachedPreviewImages, 4, 256);
    }

    /// <inheritdoc />
    public void ConfigurePreviewDecode(int maxLongEdgePixels, bool preferFullDecode = false)
    {
        lock (_addLock)
        {
            _decodeFull = preferFullDecode;
            _previewMaxLongEdge = QuantizeMaxLongEdge(Math.Clamp(maxLongEdgePixels, 128, 16384));
        }
    }

    private static int QuantizeMaxLongEdge(int edge)
    {
        ReadOnlySpan<int> steps = [384, 512, 768, 1024, 1280, 1536, 2048, 2560, 3072, 4096];
        foreach (var s in steps)
        {
            if (edge <= s)
                return s;
        }
        return 4096;
    }

    private static long EstimateBytes(SKBitmap? bmp)
    {
        if (bmp == null)
            return 0;
        var w = bmp.Width;
        var h = bmp.Height;
        if (w <= 0 || h <= 0)
            return 0;
        return (long)w * h * 4;
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
        if (u.Length > 2 && u[0] == '/' && u[1] == '/' && u[2] != '/')
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
            try { s = Uri.UnescapeDataString(s); }
            catch { /* 保持原样 */ }
        }

        return s;
    }

    /// <summary>必须在持有 <see cref="_addLock"/> 时调用。</summary>
    private void TouchLru(string url, CacheEntry e)
    {
        if (e.LruNode != null)
            _lru.Remove(e.LruNode);
        e.LruNode = _lru.AddLast(url);
    }

    /// <summary>必须在持有 <see cref="_addLock"/> 时调用。</summary>
    private void RemoveEntryUnderLock(string url, CacheEntry e)
    {
        if (e.LruNode != null)
            _lru.Remove(e.LruNode);
        if (e.Bitmap != null)
        {
            try { e.Bitmap.Dispose(); } catch { }
            _currentBytes -= e.ApproxBytes;
        }
        _cache.Remove(url);
    }

    /// <summary>必须在持有 <see cref="_addLock"/> 时调用。</summary>
    private void EnforceCapacityUnderLock()
    {
        while (_cache.Count > _maxCachedImages && _lru.First != null)
        {
            var key = _lru.First.Value;
            if (_cache.TryGetValue(key, out var e))
                RemoveEntryUnderLock(key, e);
            else
                _lru.RemoveFirst();
        }
    }

    /// <summary>预览解码：保持 <paramref name="intrinsicW"/>/<paramref name="intrinsicH"/> 为原图尺寸供布局比例；位图为缩小后的预览。</summary>
    private static SKBitmap? DecodeWithPreviewBudget(string path, int maxLongEdge, bool fullDecode, out int intrinsicW, out int intrinsicH)
    {
        intrinsicW = intrinsicH = 0;
        try { path = Path.GetFullPath(path); }
        catch { return null; }

        if (!File.Exists(path))
            return null;

        try
        {
            using var codec = SKCodec.Create(path);
            if (codec == null)
                return null;
            var bmp = DecodeFromCodec(codec, maxLongEdge, fullDecode, out intrinsicW, out intrinsicH);
            if (bmp != null)
                return bmp;
        }
        catch { }

        try
        {
            using var fs = File.OpenRead(path);
            using var codec = SKCodec.Create(fs);
            if (codec == null)
                return null;
            var bmp = DecodeFromCodec(codec, maxLongEdge, fullDecode, out intrinsicW, out intrinsicH);
            if (bmp != null)
                return bmp;
        }
        catch { }

        // 部分 PNG 等无法在解码阶段缩小：仅在原图不太大时退回全图解码，避免 OOM。
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec != null)
            {
                var info = codec.Info;
                if (info.Width > 0 && info.Height > 0 && (long)info.Width * info.Height <= 16_000_000)
                {
                    var full = SKBitmap.Decode(path);
                    if (full != null && full.Width > 0 && full.Height > 0)
                    {
                        intrinsicW = full.Width;
                        intrinsicH = full.Height;
                        if (!fullDecode && Math.Max(full.Width, full.Height) > maxLongEdge)
                        {
                            var scaled = full.Resize(
                                ComputeResizeInfo(full.Width, full.Height, maxLongEdge),
                                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                            full.Dispose();
                            return scaled;
                        }
                        return full;
                    }
                    full?.Dispose();
                }
            }
        }
        catch { }

        return null;
    }

    private static SKImageInfo ComputeResizeInfo(int w, int h, int maxLongEdge)
    {
        float maxDim = Math.Max(w, h);
        if (maxDim <= maxLongEdge)
            return new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        float s = maxLongEdge / maxDim;
        int nw = Math.Max(1, (int)(w * s));
        int nh = Math.Max(1, (int)(h * s));
        return new SKImageInfo(nw, nh, SKColorType.Rgba8888, SKAlphaType.Premul);
    }

    private static SKBitmap? DecodeBytesWithPreviewBudget(byte[] bytes, int maxLongEdge, bool fullDecode, out int intrinsicW, out int intrinsicH)
    {
        intrinsicW = intrinsicH = 0;
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null)
                return null;
            return DecodeFromCodec(codec, maxLongEdge, fullDecode, out intrinsicW, out intrinsicH);
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap? DecodeFromCodec(SKCodec codec, int maxLongEdge, bool fullDecode, out int intrinsicW, out int intrinsicH)
    {
        var info = codec.Info;
        intrinsicW = info.Width;
        intrinsicH = info.Height;
        if (intrinsicW <= 0 || intrinsicH <= 0)
            return null;

        if ((long)intrinsicW * intrinsicH > 80_000_000L)
            return null;

        if (fullDecode || Math.Max(intrinsicW, intrinsicH) <= maxLongEdge)
            return SKBitmap.Decode(codec);

        float scale = Math.Min(1f, maxLongEdge / (float)Math.Max(intrinsicW, intrinsicH));
        var dim = codec.GetScaledDimensions(scale);
        if (dim.Width < 1 || dim.Height < 1)
            return null;

        const int maxArea = 2_400_000;
        int guard = 0;
        while (dim.Width * dim.Height > maxArea && scale > 0.04f && guard++ < 12)
        {
            scale *= 0.82f;
            dim = codec.GetScaledDimensions(scale);
            if (dim.Width < 1 || dim.Height < 1)
                return null;
        }

        var dstInfo = new SKImageInfo(dim.Width, dim.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var scaled = SKBitmap.Decode(codec, dstInfo);
        if (scaled != null && scaled.Width > 0 && scaled.Height > 0)
            return scaled;
        scaled?.Dispose();
        return null;
    }

    /// <summary>必须在持有 <see cref="_addLock"/> 时调用。</summary>
    private SKBitmap? TryGetImageUnderLock(string url)
    {
        if (_disposed)
            return null;

        if (!_cache.TryGetValue(url, out var entry))
        {
            entry = new CacheEntry();
            _cache[url] = entry;
            TouchLru(url, entry);
            EnforceCapacityUnderLock();
        }
        else
        {
            TouchLru(url, entry);
            if (entry.Bitmap != null && (!_decodeFull || entry.FullDecode))
                return entry.Bitmap;
            if (entry.Loading)
                return null;
        }

        var trimmed = PathSanitizer.Sanitize(url);

        if (IsNetworkImageUrl(trimmed))
        {
            var fetchUrl = trimmed.StartsWith("//", StringComparison.Ordinal) ? "https:" + trimmed : trimmed;
            entry.Loading = true;
            var edge = _previewMaxLongEdge;
            var full = _decodeFull;
            _ = Task.Run(async () =>
            {
                SKBitmap? bmp = null;
                var iw = 0;
                var ih = 0;
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var bytes = await client.GetByteArrayAsync(fetchUrl).ConfigureAwait(false);
                    bmp = DecodeBytesWithPreviewBudget(bytes, edge, full, out iw, out ih);
                }
                catch { }

                lock (_addLock)
                {
                    if (_disposed)
                    {
                        bmp?.Dispose();
                        return;
                    }
                    if (!_cache.TryGetValue(url, out var e))
                    {
                        bmp?.Dispose();
                        return;
                    }
                    e.Loading = false;
                    if (e.Bitmap != null)
                    {
                        try { e.Bitmap.Dispose(); } catch { }
                        _currentBytes -= e.ApproxBytes;
                    }
                    e.Bitmap = bmp;
                    e.IntrinsicW = iw;
                    e.IntrinsicH = ih;
                    e.DecodedMaxLongEdge = edge;
                    e.FullDecode = full;
                    e.ApproxBytes = EstimateBytes(bmp);
                    _currentBytes += e.ApproxBytes;
                    TouchLru(url, e);
                    EnforceCapacityUnderLock();
                }

                try { ImageLoaded?.Invoke(); } catch { }
            });
            return null;
        }

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var localPath = NormalizeLocalFilePath(trimmed);
        var maxEdge = _previewMaxLongEdge;
        var fullRes = _decodeFull;
        var bmpLocal = DecodeWithPreviewBudget(localPath, maxEdge, fullRes, out var ow, out var oh);

        if (entry.Bitmap != null)
        {
            try { entry.Bitmap.Dispose(); } catch { }
            _currentBytes -= entry.ApproxBytes;
        }
        entry.Bitmap = bmpLocal;
        entry.IntrinsicW = ow;
        entry.IntrinsicH = oh;
        entry.DecodedMaxLongEdge = maxEdge;
        entry.FullDecode = fullRes;
        entry.ApproxBytes = EstimateBytes(bmpLocal);
        _currentBytes += entry.ApproxBytes;
        TouchLru(url, entry);
        EnforceCapacityUnderLock();
        return entry.Bitmap;
    }

    public void WithImage(string url, Action<SKBitmap?> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrEmpty(url))
        {
            action(null);
            return;
        }
        lock (_addLock)
        {
            if (_disposed)
            {
                action(null);
                return;
            }
            var bmp = TryGetImageUnderLock(url);
            action(bmp);
        }
    }

    /// <inheritdoc />
    public bool TryGetImagePixelSize(string url, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrEmpty(url))
            return false;
        lock (_addLock)
        {
            if (_disposed)
                return false;

            if (_cache.TryGetValue(url, out var e) && e.IntrinsicW > 0 && e.IntrinsicH > 0)
            {
                TouchLru(url, e);
                width = e.IntrinsicW;
                height = e.IntrinsicH;
                return true;
            }

            var trimmed = PathSanitizer.Sanitize(url);
            if (IsNetworkImageUrl(trimmed) || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return false;

            var localPath = NormalizeLocalFilePath(trimmed);
            try
            {
                using var codec = SKCodec.Create(localPath);
                if (codec == null)
                    return false;
                var info = codec.Info;
                if (info.Width <= 0 || info.Height <= 0)
                    return false;

                if (!_cache.TryGetValue(url, out e))
                {
                    e = new CacheEntry();
                    _cache[url] = e;
                    TouchLru(url, e);
                    EnforceCapacityUnderLock();
                }
                e.IntrinsicW = info.Width;
                e.IntrinsicH = info.Height;
                TouchLru(url, e);
                width = info.Width;
                height = info.Height;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public SKBitmap? TryGetImage(string url)
    {
        if (string.IsNullOrEmpty(url))
            return null;
        lock (_addLock)
        {
            if (_disposed)
                return null;
            return TryGetImageUnderLock(url);
        }
    }

    public void Dispose()
    {
        lock (_addLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var kv in _cache)
            {
                try { kv.Value.Bitmap?.Dispose(); } catch { }
            }
            _cache.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }
}

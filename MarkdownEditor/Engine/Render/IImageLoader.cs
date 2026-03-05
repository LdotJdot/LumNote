using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 图片加载器 - 用于从 URL 或路径加载图片
/// </summary>
public interface IImageLoader
{
    /// <summary>
    /// 尝试获取已加载的图片，若未加载则返回 null
    /// </summary>
    SKBitmap? TryGetImage(string url);

    /// <summary>
    /// 异步加载完成后触发，便于界面重绘（可选实现）。
    /// </summary>
    event Action? ImageLoaded;
}

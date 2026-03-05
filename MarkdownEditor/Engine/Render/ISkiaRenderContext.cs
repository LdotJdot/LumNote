using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// Skia 渲染上下文 - 抽象绘制目标
/// 支持 Avalonia Canvas、离屏缓冲区、导出
/// </summary>
public interface ISkiaRenderContext
{
    SKCanvas Canvas { get; }
    SKSize Size { get; }
    float Scale { get; }
}

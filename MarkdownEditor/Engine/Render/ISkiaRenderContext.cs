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

    /// <summary>
    /// 为 true 时跳过块级 SKPicture 缓存并直接绘制（如 PDF 导出），
    /// 便于内容流保留矢量文本指令、避免多余 Form 对象。
    /// </summary>
    bool PreferDirectDraw => false;
}

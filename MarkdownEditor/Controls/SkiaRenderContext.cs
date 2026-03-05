using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Controls;

internal sealed class SkiaRenderContext : ISkiaRenderContext
{
    public SKCanvas Canvas { get; set; } = null!;
    public SKSize Size { get; set; }
    public float Scale { get; set; } = 1f;
}

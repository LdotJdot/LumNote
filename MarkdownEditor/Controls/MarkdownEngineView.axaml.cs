using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;

namespace MarkdownEditor.Controls;

/// <summary>
/// 基于自研引擎的 Markdown 预览 - Skia 渲染，虚拟化，高性能
/// </summary>
public partial class MarkdownEngineView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownEngineView, string?>(nameof(Markdown));

    public static readonly StyledProperty<MarkdownStyleConfig?> StyleConfigProperty =
        AvaloniaProperty.Register<MarkdownEngineView, MarkdownStyleConfig?>(nameof(StyleConfig));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownStyleConfig? StyleConfig
    {
        get => GetValue(StyleConfigProperty);
        set => SetValue(StyleConfigProperty, value);
    }

    private string? _lastMarkdown;
    private DispatcherTimer? _debounce;

    public MarkdownEngineView()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            UpdateDocument();
        };

        MarkdownProperty.Changed.AddClassHandler<MarkdownEngineView>((c, _) =>
        {
            c._debounce?.Stop();
            c._debounce?.Start();
        });

        StyleConfigProperty.Changed.AddClassHandler<MarkdownEngineView>((c, _) =>
        {
            if (c.RenderControl != null)
                c.RenderControl.StyleConfig = c.StyleConfig;
        });

        if (Scroll != null)
            Scroll.ScrollChanged += (_, _) =>
            {
                if (RenderControl != null && Scroll != null)
                {
                    RenderControl.ScrollOffset = (float)(Scroll.Offset.Y);
                    RenderControl.InvalidateVisual();
                }
            };

        if (RenderControl != null)
            RenderControl.RequestScrollToY += (contentY) =>
            {
                if (Scroll != null)
                {
                    var maxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
                    Scroll.Offset = new Avalonia.Vector(Scroll.Offset.X, Math.Clamp(contentY, 0, maxY));
                }
            };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (RenderControl != null)
            RenderControl.StyleConfig = StyleConfig;
        if (Markdown != _lastMarkdown) UpdateDocument();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RenderControl?.InvalidateVisual();
    }

    private void UpdateDocument()
    {
        var md = Markdown ?? "";
        if (md == _lastMarkdown) return;
        _lastMarkdown = md;
        if (RenderControl != null)
        {
            RenderControl.Document = new StringDocumentSource(md);
            RenderControl.InvalidateVisual();
            RenderControl.InvalidateMeasure();
        }
    }
}

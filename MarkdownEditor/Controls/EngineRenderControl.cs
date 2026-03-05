using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Controls;

/// <summary>
/// Skia 渲染控件 - 作为 ScrollViewer 的内容，支持文本选择
/// </summary>
public class EngineRenderControl : Control
{
    public static readonly StyledProperty<IDocumentSource?> DocumentProperty =
        AvaloniaProperty.Register<EngineRenderControl, IDocumentSource?>(nameof(Document));

    public static readonly StyledProperty<float> ScrollOffsetProperty = AvaloniaProperty.Register<
        EngineRenderControl,
        float
    >(nameof(ScrollOffset));

    public static readonly StyledProperty<MarkdownStyleConfig?> StyleConfigProperty =
        AvaloniaProperty.Register<EngineRenderControl, MarkdownStyleConfig?>(nameof(StyleConfig));

    public IDocumentSource? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public float ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    public MarkdownStyleConfig? StyleConfig
    {
        get => GetValue(StyleConfigProperty);
        set => SetValue(StyleConfigProperty, value);
    }

    private RenderEngine? _engine;
    private SelectionRange? _selection;
    private (int block, int offset)? _anchor;
    private const float ContentPaddingX = 24;
    private const float ContentPaddingY = 16;

    /// <summary>左右各 24，引擎内容宽度 = 控件宽度 - 48</summary>
    private const float ContentPaddingHorizontalTotal = 48f;
    private bool _bottomLogged;

    /// <summary>请求滚动到内容坐标 contentY，用于脚注上标/↩︎ 跳转。</summary>
    public event Action<float>? RequestScrollToY;

    public EngineRenderControl()
    {
        ClipToBounds = true;
        Focusable = true;
        StyleConfigProperty.Changed.AddClassHandler<EngineRenderControl>(
            (c, _) => c._engine = null
        );
        DocumentProperty.Changed.AddClassHandler<EngineRenderControl>(
            (c, _) =>
            {
                c._selection = null;
                c._anchor = null;
                c._bottomLogged = false;
            }
        );
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerEnteredEvent, OnPointerEntered, RoutingStrategies.Tunnel);
        AddHandler(PointerExitedEvent, OnPointerExited, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private RenderEngine? GetOrCreateEngine()
    {
        if (_engine != null)
            return _engine;
        var w = (float)Math.Max(1, Bounds.Width - ContentPaddingHorizontalTotal);
        var config = EngineConfig.FromStyle(StyleConfig);
        _engine = new RenderEngine(w, config);
        if (_engine.GetImageLoader() is DefaultImageLoader loader)
            loader.ImageLoaded += () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
        return _engine;
    }

    /// <summary>与渲染时一致的滚动值（裁剪到有效范围），用于命中测试与光标对齐。</summary>
    private float GetClampedScrollY()
    {
        var doc = Document;
        if (doc == null)
            return 0;
        var engine = GetOrCreateEngine();
        var totalHeight = engine.MeasureTotalHeight(doc);
        var viewportH = (float)Math.Max(0, Bounds.Height);
        var maxScroll = Math.Max(0, totalHeight - viewportH);
        return Math.Clamp(ScrollOffset, 0, maxScroll);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Document != null)
        {
            var pt = e.GetPosition(this);
            var scrollY = GetClampedScrollY();
            var contentY = (float)(pt.Y - ContentPaddingY + scrollY);
            var contentX = (float)(pt.X - ContentPaddingX);
            var engine = GetOrCreateEngine();
            var hit = engine?.HitTest(Document, contentX, contentY);
            if (hit is { } h)
            {
                if (!string.IsNullOrEmpty(h.linkUrl))
                {
                    var url = h.linkUrl;
                    if (url.StartsWith("footnote:", StringComparison.Ordinal))
                    {
                        var y = engine?.GetContentYForFootnoteSection(Document);
                        if (y.HasValue)
                        {
                            RequestScrollToY?.Invoke(Math.Max(0, y.Value - 20));
                            e.Handled = true;
                            return;
                        }
                    }
                    else if (url.StartsWith("footnote-back:", StringComparison.Ordinal))
                    {
                        var rest = url.AsSpan()["footnote-back:".Length..];
                        int colon = rest.IndexOf(':');
                        if (
                            colon >= 0
                            && int.TryParse(rest[..colon].ToString(), out int bi)
                            && int.TryParse(rest[(colon + 1)..].ToString(), out int co)
                        )
                        {
                            var y = engine?.GetContentYForBlockOffset(Document, bi, co);
                            if (y.HasValue)
                            {
                                RequestScrollToY?.Invoke(Math.Max(0, y.Value - 40));
                                e.Handled = true;
                                return;
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            if (
                                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            )
                                url = "https://" + url;
                            _ = System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = url,
                                    UseShellExecute = true
                                }
                            );
                        }
                        catch { }
                    }
                    e.Handled = true;
                    return;
                }
                _anchor = (h.blockIndex, h.charOffset);
                _selection = new SelectionRange(
                    h.blockIndex,
                    h.charOffset,
                    h.blockIndex,
                    h.charOffset
                );
                e.Handled = true;
                Focus();
                InvalidateVisual();
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        var scrollY = GetClampedScrollY();
        var contentY = (float)(pt.Y - ContentPaddingY + scrollY);
        var contentX = (float)(pt.X - ContentPaddingX);
        var engine = GetOrCreateEngine();

        if (
            _anchor is { } a
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && Document != null
        )
        {
            var hit = engine?.HitTest(Document, contentX, contentY);
            if (hit is { } h)
            {
                int startBlock,
                    startOff,
                    endBlock,
                    endOff;
                if (a.block < h.blockIndex || (a.block == h.blockIndex && a.offset <= h.charOffset))
                {
                    startBlock = a.block;
                    startOff = a.offset;
                    endBlock = h.blockIndex;
                    endOff = h.charOffset;
                }
                else
                {
                    startBlock = h.blockIndex;
                    startOff = h.charOffset;
                    endBlock = a.block;
                    endOff = a.offset;
                }
                _selection = new SelectionRange(startBlock, startOff, endBlock, endOff);
                e.Handled = true;
                InvalidateVisual();
            }
        }
        else
        {
            UpdateCursor(engine, contentX, contentY);
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (Document != null)
        {
            var pt = e.GetPosition(this);
            var scrollY = GetClampedScrollY();
            var contentY = (float)(pt.Y - ContentPaddingY + scrollY);
            var contentX = (float)(pt.X - ContentPaddingX);
            UpdateCursor(GetOrCreateEngine(), contentX, contentY);
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        Cursor = Avalonia.Input.Cursor.Default;
        ToolTip.SetTip(this, null);
    }

    private void UpdateCursor(RenderEngine? engine, float contentX, float contentY)
    {
        if (engine == null || Document == null)
            return;
        var hit = engine.HitTest(Document, contentX, contentY);
        if (hit is { } h)
        {
            Cursor = !string.IsNullOrEmpty(h.linkUrl)
                ? new Avalonia.Input.Cursor(StandardCursorType.Hand)
                : h.isSelectable
                    ? new Avalonia.Input.Cursor(StandardCursorType.Ibeam)
                    : Avalonia.Input.Cursor.Default;
            ToolTip.SetTip(this, !string.IsNullOrEmpty(h.linkUrl) ? h.linkUrl : null);
        }
        else
        {
            Cursor = Avalonia.Input.Cursor.Default;
            ToolTip.SetTip(this, null);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (
            e.GetCurrentPoint(this).Properties.PointerUpdateKind
            is PointerUpdateKind.LeftButtonReleased
        )
            _anchor = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            e.Key == Key.C
            && (e.KeyModifiers & KeyModifiers.Control) != 0
            && _selection is { } sel
            && !sel.IsEmpty
            && Document != null
        )
        {
            var text = GetOrCreateEngine()?.GetSelectedText(Document, sel);
            if (!string.IsNullOrEmpty(text))
            {
                _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
                e.Handled = true;
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var doc = Document;
        if (doc == null)
            return new Size(availableSize.Width, 100);

        var engine = GetOrCreateEngine();
        var w = (float)Math.Max(1, availableSize.Width - ContentPaddingHorizontalTotal);
        engine.SetWidth(w);
        float h = engine.MeasureTotalHeight(doc);
        return new Size(availableSize.Width, Math.Max(100, h));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var doc = Document;
        if (doc == null)
            return;

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var rawScrollY = ScrollOffset;
        var w = (float)Math.Max(1, bounds.Width - ContentPaddingHorizontalTotal);
        var config = EngineConfig.FromStyle(StyleConfig);

        if (_engine == null)
            _engine = new RenderEngine(w, config);
        else
            _engine.SetWidth(w);

        var viewportHeight = (float)bounds.Height;
        var totalHeight = _engine.MeasureTotalHeight(doc);

        // 将滚动偏移裁剪在 [0, max(0, totalHeight - viewportHeight)] 范围内，
        // 避免当 ScrollViewer 允许滚动超过内容高度时，内容整体被平移出视口只剩空白。
        var maxScroll = Math.Max(0, totalHeight - viewportHeight);
        var scrollY = Math.Clamp(rawScrollY, 0, maxScroll);

        context.Custom(
            new EngineDrawOp(
                new Rect(0, 0, bounds.Width, bounds.Height),
                doc,
                _engine,
                scrollY,
                w,
                viewportHeight,
                _selection
            )
        );
    }

    private sealed class EngineDrawOp : ICustomDrawOperation
    {
        private readonly Rect _rect;
        private readonly IDocumentSource _doc;
        private readonly RenderEngine _engine;
        private readonly float _scrollY;
        private readonly float _width;
        private readonly float _height;
        private readonly SelectionRange? _selection;

        public EngineDrawOp(
            Rect rect,
            IDocumentSource doc,
            RenderEngine engine,
            float scrollY,
            float width,
            float height,
            SelectionRange? selection
        )
        {
            _rect = rect;
            _doc = doc;
            _engine = engine;
            _scrollY = scrollY;
            _width = width;
            _height = height;
            _selection = selection;
        }

        public Rect Bounds => _rect;

        public bool HitTest(Point p) => true;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext ctx)
        {
            var lease = ctx.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease == null)
                return;

            using var api = lease.Lease();
            var canvas = api.SkCanvas;
            if (canvas == null)
                return;

            var skCtx = new SkiaRenderContext
            {
                Canvas = canvas,
                Size = new SKSize(_width, _height),
                Scale = 1f
            };
            canvas.Save();
            canvas.Translate(ContentPaddingX, ContentPaddingY);
            _engine.Render(
                skCtx,
                _doc,
                _scrollY,
                _height,
                _selection.HasValue && !_selection.Value.IsEmpty ? _selection : null
            );
            canvas.Restore();
        }
    }
}

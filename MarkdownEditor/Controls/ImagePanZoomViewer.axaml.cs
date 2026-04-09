using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cursor = Avalonia.Input.Cursor;

namespace MarkdownEditor.Controls;

/// <summary>
/// 传统图片查看交互：滚轮缩放（以光标为中心）、按住左键拖动平移；双击适应窗口。
/// </summary>
public partial class ImagePanZoomViewer : UserControl
{
    public static readonly StyledProperty<string?> SourcePathProperty = AvaloniaProperty.Register<
        ImagePanZoomViewer,
        string?
    >(nameof(SourcePath));

    private Border? _viewport;
    private Image? _previewImage;
    private double _scale = 1;
    private double _panX;
    private double _panY;
    private Point _dragStart;
    private Point _panAtDragStart;
    private bool _dragging;
    private TransformGroup? _transformGroup;
    private ScaleTransform? _scaleTransform;
    private TranslateTransform? _translateTransform;

    private const double MinScale = 0.05;
    private const double MaxScale = 16;
    private const double WheelZoomFactor = 1.12;

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public ImagePanZoomViewer()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewport = this.FindControl<Border>("Viewport");
        _previewImage = this.FindControl<Image>("PreviewImage");
        if (_previewImage == null || _viewport == null) return;

        _scaleTransform = new ScaleTransform(1, 1);
        _translateTransform = new TranslateTransform();
        _transformGroup = new TransformGroup();
        _transformGroup.Children.Add(_scaleTransform);
        _transformGroup.Children.Add(_translateTransform);
        _previewImage.RenderTransform = _transformGroup;
        _previewImage.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);

        _viewport.PointerWheelChanged += OnPointerWheelChanged;
        _viewport.PointerPressed += OnPointerPressed;
        _viewport.PointerMoved += OnPointerMoved;
        _viewport.PointerReleased += OnPointerReleased;
        _viewport.PointerCaptureLost += OnPointerCaptureLost;
        _viewport.DoubleTapped += OnDoubleTapped;
        _viewport.SizeChanged += OnViewportSizeChanged;

        _previewImage.PropertyChanged += OnPreviewImagePropertyChanged;
    }

    private void OnPreviewImagePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Image.SourceProperty)
            global::Avalonia.Threading.Dispatcher.UIThread.Post(ResetViewToFit, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewport != null)
        {
            _viewport.PointerWheelChanged -= OnPointerWheelChanged;
            _viewport.PointerPressed -= OnPointerPressed;
            _viewport.PointerMoved -= OnPointerMoved;
            _viewport.PointerReleased -= OnPointerReleased;
            _viewport.PointerCaptureLost -= OnPointerCaptureLost;
            _viewport.DoubleTapped -= OnDoubleTapped;
            _viewport.SizeChanged -= OnViewportSizeChanged;
        }

        if (_previewImage != null)
            _previewImage.PropertyChanged -= OnPreviewImagePropertyChanged;

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourcePathProperty)
            global::Avalonia.Threading.Dispatcher.UIThread.Post(ResetViewToFit, DispatcherPriority.Loaded);
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(SourcePath)) return;
        if (e.PreviousSize.Width <= 1 && e.NewSize.Width > 1)
            ResetViewToFit();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        ResetViewToFit();
    }

    private void ResetViewToFit()
    {
        _scale = 1;
        _panX = 0;
        _panY = 0;
        TryFitToViewport();
    }

    private void TryFitToViewport()
    {
        if (_previewImage?.Source is not Bitmap bmp || _viewport == null || _scaleTransform == null || _translateTransform == null)
        {
            ApplyTransform();
            return;
        }

        var vw = _viewport.Bounds.Width;
        var vh = _viewport.Bounds.Height;
        if (vw <= 1 || vh <= 1)
        {
            ApplyTransform();
            return;
        }

        var iw = bmp.PixelSize.Width;
        var ih = bmp.PixelSize.Height;
        if (iw <= 0 || ih <= 0)
        {
            ApplyTransform();
            return;
        }

        var fit = Math.Min(vw / iw, vh / ih);
        if (fit > 1)
            fit = 1;
        _scale = fit;
        _panX = (vw - iw * _scale) / 2;
        _panY = (vh - ih * _scale) / 2;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (_scaleTransform == null || _translateTransform == null) return;
        _scaleTransform.ScaleX = _scale;
        _scaleTransform.ScaleY = _scale;
        _translateTransform.X = _panX;
        _translateTransform.Y = _panY;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewport == null || _previewImage?.Source is not Bitmap bmp) return;
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.0001) return;

        e.Handled = true;
        var mouse = e.GetPosition(_viewport);
        var iw = bmp.PixelSize.Width;
        var ih = bmp.PixelSize.Height;
        if (iw <= 0 || ih <= 0) return;

        var ix = (mouse.X - _panX) / _scale;
        var iy = (mouse.Y - _panY) / _scale;
        var factor = delta > 0 ? WheelZoomFactor : 1 / WheelZoomFactor;
        var newScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        _panX = mouse.X - ix * newScale;
        _panY = mouse.Y - iy * newScale;
        _scale = newScale;
        ApplyTransform();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewport == null) return;
        var p = e.GetCurrentPoint(_viewport);
        if (!p.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        _dragging = true;
        _dragStart = e.GetPosition(_viewport);
        _panAtDragStart = new Point(_panX, _panY);
        _viewport.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(_viewport);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _viewport == null) return;
        var pos = e.GetPosition(_viewport);
        _panX = _panAtDragStart.X + (pos.X - _dragStart.X);
        _panY = _panAtDragStart.Y + (pos.Y - _dragStart.Y);
        ApplyTransform();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_viewport != null)
        {
            _viewport.Cursor = Cursor.Default;
            if (e.Pointer.Captured == _viewport)
                e.Pointer.Capture(null);
        }
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        if (_viewport != null)
            _viewport.Cursor = Cursor.Default;
    }
}

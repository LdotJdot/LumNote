using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Controls;

/// <summary>
/// 基于自研引擎的 Markdown 预览 - Skia 渲染，虚拟化，高性能
/// </summary>
public partial class MarkdownEngineView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<
        MarkdownEngineView,
        string?
    >(nameof(Markdown));

    public static readonly StyledProperty<MarkdownStyleConfig?> StyleConfigProperty =
        AvaloniaProperty.Register<MarkdownEngineView, MarkdownStyleConfig?>(nameof(StyleConfig));

    /// <summary>与 ViewModel.PreviewZoomLevel 绑定，变化时触发布局刷新。</summary>
    public static readonly StyledProperty<double> ZoomLevelProperty = AvaloniaProperty.Register<
        MarkdownEngineView,
        double
    >(nameof(ZoomLevel), 1.0);

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

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
    private MarkdownEditor.Engine.Document.MutableStringDocumentSource? _documentSource;

    /// <summary>布局应用后待恢复的滚动比例 [0,1]，null 表示不恢复。</summary>
    private double? _pendingScrollRatio;

    public MarkdownEngineView()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _debounce.Tick += (_, _) =>
        {
            try
            {
                _debounce.Stop();
                UpdateDocument();
            }
            catch
            {
                _debounce.Stop();
            }
        };

        MarkdownProperty.Changed.AddClassHandler<MarkdownEngineView>(
            (c, e) =>
            {
                var view = c;
                view._debounce?.Stop();
                var newMd = view.Markdown ?? "";
                // 首次从空内容切换到有内容时，直接触发布局，避免初始化阶段预览长时间空白。
                if (string.IsNullOrEmpty(view._lastMarkdown) && !string.IsNullOrEmpty(newMd))
                {
                    view.UpdateDocument();
                    return;
                }
                // 仅单字符变更（如 todo 勾选）时立即刷新预览，避免延迟与不同步
                if (
                    view._lastMarkdown != null
                    && newMd.Length == view._lastMarkdown.Length
                    && IsSingleCharChange(view._lastMarkdown, newMd)
                )
                {
                    view.UpdateDocument();
                    return;
                }
                view._debounce?.Start();
            }
        );

        StyleConfigProperty.Changed.AddClassHandler<MarkdownEngineView>(
            (c, _) =>
            {
                if (c.RenderControl != null)
                {
                    c.RenderControl.StyleConfig = c.StyleConfig;
                    // 主题/样式发生变化时，重建引擎并重新布局，以便立即刷新背景与文本样式。
                    c.RenderControl.ResetEngine();
                    if (c.Markdown != null)
                    {
                        c.RenderControl.RequestParseAndLayout();
                    }
                    else
                    {
                        c.RenderControl.InvalidateVisual();
                    }
                }
            }
        );

        ZoomLevelProperty.Changed.AddClassHandler<MarkdownEngineView>(
            (c, _) =>
            {
                if (c.RenderControl == null) return;
                c.RenderControl.ResetEngine();
                if (c.Markdown != null)
                    c.RenderControl.RequestParseAndLayout();
                else
                    c.RenderControl.InvalidateVisual();
            }
        );

        AddHandler(PointerWheelChangedEvent, OnPreviewPointerWheelZoom, RoutingStrategies.Tunnel);

        if (Scroll != null)
        {
            Scroll.ScrollChanged += (_, _) =>
            {
                if (RenderControl == null || Scroll == null)
                    return;
                RenderControl.ScrollOffset = (float)(Scroll.Offset.Y);
                var maxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
                if (maxY > 0 && DataContext is ViewModels.MainViewModel vm)
                    vm.CurrentPreviewScrollRatio = Math.Clamp(Scroll.Offset.Y / maxY, 0, 1);
            };
        }

        if (RenderControl != null)
            RenderControl.RequestScrollToY += (contentY) =>
            {
                if (Scroll != null)
                {
                    var maxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
                    Scroll.Offset = new Avalonia.Vector(
                        Scroll.Offset.X,
                        Math.Clamp(contentY, 0, maxY)
                    );
                }
            };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (RenderControl != null)
        {
            RenderControl.StyleConfig = StyleConfig;
            RenderControl.DocumentBasePath = (DataContext is MainViewModel vm) ? (vm.DocumentBasePath ?? "") : "";
            RenderControl.LayoutApplied -= OnRenderControlLayoutApplied;
            RenderControl.LayoutApplied += OnRenderControlLayoutApplied;
            RenderControl.ClearPendingScrollRestore = () => _pendingScrollRatio = null;
        }
        UpdateRenderControlViewportHeight();
        if (Markdown != _lastMarkdown)
            UpdateDocument();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SaveScrollRatioForRestore();
        UpdateRenderControlViewportHeight();
    }

    private void SaveScrollRatioForRestore()
    {
        // 优先使用 ViewModel 中持续维护的 CurrentPreviewScrollRatio，
        // 避免在窗口状态变化时 Scroll.Offset 已被重置导致记录到错误的 0 位置。
        if (DataContext is MainViewModel vm)
        {
            _pendingScrollRatio = Math.Clamp(vm.CurrentPreviewScrollRatio, 0, 1);
            return;
        }

        if (Scroll != null)
        {
            var maxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
            _pendingScrollRatio = maxY > 0 ? Math.Clamp(Scroll.Offset.Y / maxY, 0, 1) : 0;
        }
    }

    private void OnRenderControlLayoutApplied()
    {
        var ratio = _pendingScrollRatio;
        _pendingScrollRatio = null;
        if (ratio == null || Scroll == null)
            return;
        // 延后到布局完成后再恢复，确保 Extent 已更新
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    if (Scroll != null && RenderControl != null)
                    {
                        var newMaxY = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
                        var newY = ratio!.Value * newMaxY;
                        RenderControl.SuppressNextScrollLayout();
                        Scroll.Offset = new Avalonia.Vector(Scroll.Offset.X, newY);
                    }
                }
                catch { }
            },
            DispatcherPriority.Loaded
        );
    }

    private void UpdateRenderControlViewportHeight()
    {
        if (RenderControl != null && Scroll != null && Scroll.Viewport.Height > 0)
            RenderControl.ViewportHeight = (float)Scroll.Viewport.Height;
    }

    private void ClearSkipEditorToPreviewScrollSync()
    {
        if (DataContext is MainViewModel vm)
            vm.SkipEditorToPreviewScrollSync = false;
    }

    private void OnPreviewPointerWheelZoom(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        if (DataContext is not MainViewModel vm)
            return;
        vm.ActivePane = "Preview";
        if (e.Delta.Y > 0)
            vm.ZoomPreviewInCommand.Execute(null);
        else if (e.Delta.Y < 0)
            vm.ZoomPreviewOutCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }

    /// <summary>计算两段文本的差异行区间 [firstChangedLine, lastChangedLineExclusive)，用于增量解析。无差异或全量替换时返回 (null, null)。</summary>
    private static (int? lineStart, int? lineEnd) GetChangedLineRange(
        string? oldText,
        string? newText
    )
    {
        if (string.IsNullOrEmpty(oldText) || string.IsNullOrEmpty(newText))
            return (null, null);

        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');
        int firstDiff = -1;
        int lastDiff = -1;

        for (int i = 0; i < Math.Min(oldLines.Length, newLines.Length); i++)
        {
            if (oldLines[i] != newLines[i])
            {
                if (firstDiff < 0)
                    firstDiff = i;
                lastDiff = i;
            }
        }

        if (oldLines.Length != newLines.Length)
        {
            int extraFrom = Math.Min(oldLines.Length, newLines.Length);
            if (firstDiff < 0)
                firstDiff = extraFrom;
            lastDiff = Math.Max(lastDiff, Math.Max(oldLines.Length, newLines.Length) - 1);
        }

        if (firstDiff < 0)
            return (null, null);

        return (firstDiff, lastDiff + 1);
    }

    /// <summary>是否为单字符差异（如 todo [ ]↔[x]），用于跳过 debounce 立即刷新。</summary>
    private static bool IsSingleCharChange(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        int diff = 0;
        for (int i = 0; i < a.Length && diff <= 1; i++)
            if (a[i] != b[i])
                diff++;
        return diff == 1;
    }

    private void UpdateDocument()
    {
        var md = Markdown ?? "";
        if (md == _lastMarkdown)
            return;

        var (lineStart, lineEnd) = GetChangedLineRange(_lastMarkdown, md);
        if (
            DataContext is ViewModels.MainViewModel vm && vm.PendingPreviewScrollRatio is { } stored
        )
        {
            vm.PendingPreviewScrollRatio = null;
            _pendingScrollRatio = stored;
        }
        else
        {
            bool isNewDoc = string.IsNullOrEmpty(_lastMarkdown) || (lineStart == 0);
            _pendingScrollRatio = isNewDoc ? 0 : null;
            if (!isNewDoc)
                SaveScrollRatioForRestore();
        }
        _lastMarkdown = md;
        UpdateRenderControlViewportHeight();
        if (RenderControl == null)
            return;

        if (_documentSource == null)
            _documentSource = new MarkdownEditor.Engine.Document.MutableStringDocumentSource(md);
        else
            _documentSource.SetText(md);

        RenderControl.Document = _documentSource;
        RenderControl.DocumentBasePath = (DataContext is ViewModels.MainViewModel m) ? (m.DocumentBasePath ?? "") : "";
        // 整篇替换（如快速切换文档）时强制全量解析，避免 ReparseRange 基于旧文档块合并导致只渲染一部分
        int newLineCount = string.IsNullOrEmpty(md) ? 0 : md.Split('\n').Length;
        bool forceFullParse = lineStart.HasValue && lineEnd.HasValue && lineStart.Value == 0 && lineEnd.Value >= newLineCount;
        RenderControl.RequestParseAndLayout(forceFullParse ? null : lineStart, forceFullParse ? null : lineEnd);
        ClearSkipEditorToPreviewScrollSync();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        try
        {
            _debounce?.Stop();
            if (RenderControl != null)
            {
                RenderControl.LayoutApplied -= OnRenderControlLayoutApplied;
                RenderControl.ClearPendingScrollRestore = null;
            }
        }
        catch { }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>将预览区当前选区复制到剪贴板。供窗口 Ctrl+C 在预览激活时调用。</summary>
    public System.Threading.Tasks.Task<bool> TryCopySelectionAsync() =>
        RenderControl?.TryCopySelectionToClipboardAsync()
        ?? System.Threading.Tasks.Task.FromResult(false);

}

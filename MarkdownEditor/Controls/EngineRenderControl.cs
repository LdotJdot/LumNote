using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;
using MarkdownEditor.Engine.Render;
using MarkdownEditor.ViewModels;
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
    private bool _selectionInvalidateScheduled;

    private readonly LayoutTaskScheduler _layoutScheduler = new();
    private readonly IncrementalParseManager _parseManager = new();
    /// <summary>滚动触发布局防抖：停止滚动后约此毫秒数再触发布局，避免实时布局卡顿。</summary>
    private const int ScrollLayoutDebounceMs = 120;
    private DispatcherTimer? _scrollLayoutDebounceTimer;
    /// <summary>布局刚应用后的下一次 ScrollOffset 变化忽略（由 extent 变化引起），避免“布局完成→ScrollChanged→再次布局”的级联。</summary>
    private bool _suppressScrollLayoutAfterApply;

    /// <summary>上次测量时使用的有效宽度，避免 Document 变更触发的测量收到 0/NaN 导致宽度闪动。</summary>
    private double _lastValidMeasureWidth = 400;

    /// <summary>上次 OnSizeChanged 时的宽度，仅当宽度变化时重置引擎，避免高度变化（内容布局完成）触发重置导致闪烁循环。</summary>
    private double _lastSizeChangedWidth = -1;

    /// <summary>高度大于此值视为已有有效布局；小于等于则 SetWidth 清空布局也无妨。</summary>
    private const float LayoutHeightThreshold = 10f;
    /// <summary>宽度变化小于此值视为未变，SetWidth 不会清空布局。</summary>
    private const float WidthTolerance = 0.1f;

    private EngineConfig? _cachedEffectiveConfig;
    private MarkdownStyleConfig? _cachedConfigStyleRef;
    private double _cachedConfigZoom = double.NaN;

    /// <summary>当前生效的引擎配置（已应用 ZoomLevel），用于边距与布局（与 RenderEngine 一致）。缓存以避免每帧分配。</summary>
    private EngineConfig EffectiveConfig
    {
        get
        {
            var style = StyleConfig;
            var zoom = style?.ZoomLevel ?? 1.0;
            if (
                _cachedEffectiveConfig != null
                && ReferenceEquals(_cachedConfigStyleRef, style)
                && Math.Abs(_cachedConfigZoom - zoom) < 1e-6
            )
                return _cachedEffectiveConfig;
            _cachedConfigStyleRef = style;
            _cachedConfigZoom = zoom;
            _cachedEffectiveConfig = (
                EngineConfig.FromStyle(style) ?? new EngineConfig()
            ).WithZoomApplied();
            return _cachedEffectiveConfig;
        }
    }

    /// <summary>当前文档所在目录，用于解析相对路径图片（如 ![alt](C3.png)）。</summary>
    public string? DocumentBasePath
    {
        get => _documentBasePath;
        set
        {
            if (_documentBasePath == value) return;
            _documentBasePath = value;
            _engine = null;
            InvalidateVisual();
        }
    }
    private string? _documentBasePath;

    /// <summary>请求滚动到内容坐标 contentY，用于脚注上标/↩︎ 跳转。</summary>
    public event Action<float>? RequestScrollToY;

    /// <summary>布局快照应用完成后触发，供父级在刷新/窗口尺寸改变后按滚动百分比恢复位置。</summary>
    public event Action? LayoutApplied;

    /// <summary>视口高度（ScrollViewer 可见区域），用于 ComputeSlim 可见窗口计算。由父级 MarkdownEngineView 设置；未设置时用 Bounds.Height 可能误用内容总高。</summary>
    public float ViewportHeight { get; set; }

    /// <summary>下次 ScrollOffset 变化时暂不触发布局防抖（用于程序化恢复滚动时避免级联布局）。</summary>
    public void SuppressNextScrollLayout() => _suppressNextScrollLayout = true;

    /// <summary>按全文字符偏移滚动预览（如编辑区连按两次 Ctrl 对齐光标）。成功返回 true。</summary>
    public bool TryScrollToDocumentOffset(int documentOffset)
    {
        var engine = GetOrCreateEngine();
        if (engine == null || Document == null)
            return false;
        var y = engine.GetContentYForDocumentOffset(Document, documentOffset);
        if (!y.HasValue)
            return false;
        RequestScrollToY?.Invoke(Math.Max(0f, y.Value - 40f));
        RequestLayoutForViewportAfterProgrammaticScroll();
        return true;
    }

    private bool _suppressNextScrollLayout;

    /// <summary>由父级设置：滚动触发布局前调用，用于清除待恢复的滚动比例，避免覆盖用户滚动位置。</summary>
    public Action? ClearPendingScrollRestore { get; set; }

    /// <summary>布局任务是否正在执行。为 true 时，脚注跳转、Todo 勾选等依赖布局的交互可被限制或提示“等待更新”。</summary>
    public bool IsLayoutPending => _isLayoutPending;

    private bool _isLayoutPending;

    public EngineRenderControl()
    {
        ClipToBounds = true;
        Focusable = true;
        StyleConfigProperty.Changed.AddClassHandler<EngineRenderControl>(
            (c, _) =>
            {
                c._engine = null;
                c._cachedEffectiveConfig = null;
                c._cachedConfigStyleRef = null;
                // 样式（包括背景色、字体大小等）变化时，确保当前帧重绘。
                c.InvalidateMeasure();
                c.InvalidateVisual();
            }
        );
        DocumentProperty.Changed.AddClassHandler<EngineRenderControl>(
            (c, _) =>
            {
                c._selection = null;
                c._anchor = null;
            }
        );
        // 全量布局仅需重绘；长文档（ComputeSlim）滚动时防抖后再重新布局可见区域
        ScrollOffsetProperty.Changed.AddClassHandler<EngineRenderControl>(
            (c, _) => c.OnScrollOffsetChanged()
        );
        _scrollLayoutDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollLayoutDebounceMs) };
        _scrollLayoutDebounceTimer.Tick += OnScrollLayoutDebounceTick;
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerEnteredEvent, OnPointerEntered, RoutingStrategies.Tunnel);
        AddHandler(PointerExitedEvent, OnPointerExited, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>清除引擎缓存，下次渲染时用当前 StyleConfig（含 ZoomLevel）重建。用于缩放等配置变更后刷新。</summary>
    public void ResetEngine()
    {
        _engine = null;
        _cachedEffectiveConfig = null;
        _cachedConfigStyleRef = null;
        InvalidateVisual();
    }

    private RenderEngine? GetOrCreateEngine()
    {
        if (IsDocumentEmpty(Document))
            return null;
        if (_engine != null)
            return _engine;
        var config = EffectiveConfig;
        var w = (float)Math.Max(1, Bounds.Width - config.ContentPaddingX * 2);
        // 始终用 BasePathImageLoader：有文档目录时解析相对路径；绝对路径与网络图仍走内层 DefaultImageLoader（本地只读文件、网络才 Http）
        var imageLoader = new BasePathImageLoader(_documentBasePath ?? "");
        _engine = new RenderEngine(w, config, imageLoader);
        if (_engine.GetImageLoader() is { } loader)
            loader.ImageLoaded += () =>
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        InvalidateVisual();
                    }
                    catch { }
                });
        return _engine;
    }

    /// <summary>请求重新解析与布局（文档或滚动变化时调用）。可由外部在文档内容变更后调用。</summary>
    public void RequestParseAndLayout() => TriggerParseAndLayout(null, null);

    /// <summary>请求增量解析与布局，指定受影响的源码行区间 [lineStart, lineEnd)。若区间无效则退化为全量解析。</summary>
    public void RequestParseAndLayout(int? lineStart, int? lineEnd) =>
        TriggerParseAndLayout(lineStart, lineEnd);

    /// <summary>文档无实质内容（0 行或仅 1 行且为空）时不创建渲染引擎，降低空文档内存。</summary>
    private static bool IsDocumentEmpty(IDocumentSource? doc)
    {
        if (doc == null) return true;
        if (doc.LineCount == 0) return true;
        if (doc.LineCount == 1) return doc.GetLine(0).Trim().IsEmpty;
        return false;
    }

    /// <summary>触发后台解析+布局任务，完成后在 UI 线程应用快照并刷新。</summary>
    private void TriggerParseAndLayout(int? lineStart, int? lineEnd)
    {
        var doc = Document;
        if (doc == null)
            return;
        if (IsDocumentEmpty(doc))
        {
            _engine = null;
            InvalidateVisual();
            return;
        }

        var engine = GetOrCreateEngine();
        if (engine == null)
            return;
        var config = EffectiveConfig;
        var w = (float)
            Math.Max(1, (Bounds.Width > 0 ? Bounds.Width : 400) - config.ContentPaddingX * 2);
        engine.SetWidth(w);
        var scrollY = ScrollOffset;
        // 必须用视口高度，不能用 Bounds.Height（作为 ScrollViewer 内容时 Bounds.Height=文档总高）
        var viewportH = ViewportHeight > 0 ? ViewportHeight : 800f;

        bool useIncrementalLayout;
        BlockListSnapshot blockSnapshot;
        if (
            lineStart.HasValue
            && lineEnd.HasValue
            && lineStart.Value >= 0
            && lineEnd.Value > lineStart.Value
            && _parseManager.Blocks.Count > 0
        )
        {
            blockSnapshot = _parseManager.ReparseRange(doc, lineStart.Value, lineEnd.Value);
            useIncrementalLayout = true;
        }
        else
        {
            blockSnapshot = _parseManager.ReparseFull(doc);
            useIncrementalLayout = false;
        }

        // 在清空引擎前保存上一帧 cum，供 ComputeSlim 复用以保持布局一致性（ComputeSlim 会校验长度）
        float[]? previousCum = engine.GetCumulativeYSnapshot();
        engine.ApplyBlocksSnapshot(blockSnapshot, doc);

        _isLayoutPending = true;
        // 全量解析（如文档切换）时用 ComputeFull 整篇布局，避免 ComputeSlim 用旧 scrollY 只布局首屏导致下面空白
        float? scrollYArg = (useIncrementalLayout && viewportH > 0) ? scrollY : null;
        float? viewportArg = (useIncrementalLayout && viewportH > 0) ? viewportH : null;
        _layoutScheduler.EnqueueLayoutFromBlocks(
            blockSnapshot,
            w,
            scrollYArg,
            viewportArg,
            engine.GetLayoutEngine(),
            engine.GetConfig(),
            previousCum,
            (snapshot, version) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var curVer = _layoutScheduler.CurrentVersion;
                        if (version != curVer)
                            return;
                        var targetEngine = GetOrCreateEngine();
                        if (targetEngine != null)
                        {
                            targetEngine.ApplyBlocksSnapshot(blockSnapshot, doc);
                            targetEngine.ApplyLayoutSnapshot(snapshot);
                        }
                        _isLayoutPending = false;
                        _suppressScrollLayoutAfterApply = true;
                        ToolTip.SetTip(this, null);
                        InvalidateMeasure();
                        InvalidateVisual();
                        LayoutApplied?.Invoke();
                    }
                    catch
                    {
                        _isLayoutPending = false;
                    }
                });
            }
        );
    }

    /// <summary>滚动偏移变化时立即重绘；布局则防抖，停止滚动约 ScrollLayoutDebounceMs 后再触发，避免实时布局卡顿。</summary>
    private void OnScrollOffsetChanged()
    {
        InvalidateVisual();
        if (_suppressNextScrollLayout)
        {
            _suppressNextScrollLayout = false;
            return;
        }
        if (_suppressScrollLayoutAfterApply)
        {
            _suppressScrollLayoutAfterApply = false;
            return;
        }
        _scrollLayoutDebounceTimer?.Stop();
        _scrollLayoutDebounceTimer?.Start();
    }

    private void OnScrollLayoutDebounceTick(object? sender, EventArgs e)
    {
        _scrollLayoutDebounceTimer?.Stop();
        try
        {
            ClearPendingScrollRestore?.Invoke();
            if (TriggerLayoutOnlyIfCached())
                return;
            TriggerParseAndLayout(null, null);
        }
        catch { }
    }

    /// <summary>
    /// 脚注跳转等程序化改变 Scroll.Offset 之后必须按新滚动位置立即提交 slim 布局。
    /// 布局刚完成时会抑制下一次滚动触发的防抖布局；若紧接着发生脚注程序化滚动，该次变化会误消费抑制标志，
    /// 导致不排队新布局，画面仍用旧视口块直至用户再滚动。
    /// </summary>
    public void RequestLayoutForViewportAfterProgrammaticScroll()
    {
        _scrollLayoutDebounceTimer?.Stop();
        _suppressScrollLayoutAfterApply = false;
        try
        {
            ClearPendingScrollRestore?.Invoke();
            if (!TriggerLayoutOnlyIfCached())
                TriggerParseAndLayout(null, null);
            InvalidateVisual();
        }
        catch { }
    }

    /// <summary>若已有解析缓存则仅触发布局（复用 blocks，传当前 scrollY 走 ComputeSlim），不阻塞 UI；返回 true 表示已提交布局任务。</summary>
    private bool TriggerLayoutOnlyIfCached()
    {
        var doc = Document;
        if (doc == null)
            return false;
        var blockSnapshot = _parseManager.GetCurrentSnapshot();
        if (blockSnapshot == null)
            return false;

        var engine = GetOrCreateEngine();
        if (engine == null)
            return false;
        var config = EffectiveConfig;
        var w = (float)
            Math.Max(1, (Bounds.Width > 0 ? Bounds.Width : 400) - config.ContentPaddingX * 2);
        engine.SetWidth(w);
        var scrollY = ScrollOffset;
        var viewportH = ViewportHeight > 0 ? ViewportHeight : 800f;
        float[]? previousCum = engine.GetCumulativeYSnapshot();

        _isLayoutPending = true;
        _layoutScheduler.EnqueueLayoutFromBlocks(
            blockSnapshot,
            w,
            scrollY,
            viewportH,
            engine.GetLayoutEngine(),
            engine.GetConfig(),
            previousCum,
            (snapshot, version) =>
            {
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        try
                        {
                            var curVer = _layoutScheduler.CurrentVersion;
                            if (version != curVer)
                                return;
                            var targetEngine = GetOrCreateEngine();
                            if (targetEngine != null)
                            {
                                targetEngine.ApplyBlocksSnapshot(blockSnapshot, doc);
                                targetEngine.ApplyLayoutSnapshot(snapshot);
                            }
                            _isLayoutPending = false;
                            _suppressScrollLayoutAfterApply = true;
                            ToolTip.SetTip(this, null);
                            InvalidateMeasure();
                            InvalidateVisual();
                            LayoutApplied?.Invoke();
                        }
                        catch
                        {
                            _isLayoutPending = false;
                        }
                    });
            });
        return true;
    }

    /// <summary>与渲染时一致的滚动值（裁剪到有效范围），用于命中测试与光标对齐。</summary>
    private float GetClampedScrollY()
    {
        var doc = Document;
        if (doc == null)
            return 0;
        var engine = GetOrCreateEngine();
        if (engine == null)
            return 0;
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
            var contentY = (float)(pt.Y - EffectiveConfig.ContentPaddingY + scrollY);
            var contentX = (float)(pt.X - EffectiveConfig.ContentPaddingX);
            var engine = GetOrCreateEngine();
            var hit = engine?.HitTest(Document, contentX, contentY);
            if (hit is { } h)
            {
                if (!string.IsNullOrEmpty(h.linkUrl))
                {
                    var url = h.linkUrl;
                    if (
                        _isLayoutPending
                        && (
                            url.StartsWith("footnote:", StringComparison.Ordinal)
                            || url.StartsWith("footnote-back:", StringComparison.Ordinal)
                            || url.StartsWith("todo-toggle:", StringComparison.Ordinal)
                        )
                    )
                    {
                        ToolTip.SetTip(this, "等待更新…");
                        e.Handled = true;
                        return;
                    }
                    if (url.StartsWith("footnote:", StringComparison.Ordinal))
                    {
                        var y = engine?.GetContentYForFootnoteSection(Document);
                        if (y.HasValue)
                        {
                            RequestScrollToY?.Invoke(Math.Max(0, y.Value - 20));
                            RequestLayoutForViewportAfterProgrammaticScroll();
                            e.Handled = true;
                            return;
                        }
                    }
                    else if (url.StartsWith("footnote-back:", StringComparison.Ordinal))
                    {
                        var rest = url.AsSpan()["footnote-back:".Length..];
                        // 新格式：footnote-back:<id>（通过 id 在已布局 runs 中查找第一次出现的脚注引用位置）
                        // 旧格式：footnote-back:<blockIndex>:<charOffset>（兼容已有链接）
                        int colon = rest.IndexOf(':');
                        if (colon < 0)
                        {
                            var id = rest.ToString();
                            var y = engine?.GetContentYForFirstFootnoteRefId(Document, id);
                            if (y.HasValue)
                            {
                                RequestScrollToY?.Invoke(Math.Max(0, y.Value - 40));
                                RequestLayoutForViewportAfterProgrammaticScroll();
                                e.Handled = true;
                                return;
                            }
                        }
                        else if (
                            int.TryParse(rest[..colon].ToString(), out int bi)
                            && int.TryParse(rest[(colon + 1)..].ToString(), out int co)
                        )
                        {
                            var y = engine?.GetContentYForBlockOffset(Document, bi, co);
                            if (y.HasValue)
                            {
                                RequestScrollToY?.Invoke(Math.Max(0, y.Value - 40));
                                RequestLayoutForViewportAfterProgrammaticScroll();
                                e.Handled = true;
                                return;
                            }
                        }
                    }
                    else if (url.StartsWith("todo-toggle:", StringComparison.Ordinal))
                    {
                        if (DataContext is MainViewModel vm && Document != null)
                        {
                            int blockIndex = h.blockIndex;
                            int lineIndexInBlock = h.lineIndexInBlock;
                            var lineOpt = engine?.GetTodoSourceLineForListBlock(
                                Document,
                                blockIndex,
                                lineIndexInBlock
                            );
                            if (lineOpt.HasValue)
                            {
                                var text = vm.CurrentMarkdown ?? string.Empty;
                                var idx = FindTodoMarkerOnLineByIndex(text, lineOpt.Value);
                                if (
                                    idx >= 0
                                    && idx + 2 < text.Length
                                    && text[idx] == '['
                                    && text[idx + 2] == ']'
                                )
                                {
                                    var chars = text.ToCharArray();
                                    var c = chars[idx + 1];
                                    chars[idx + 1] = (c == 'x' || c == 'X') ? ' ' : 'x';
                                    // 只修改编辑区内容，刷新由编辑区变更统一触发，渲染区不单独触发
                                    vm.CurrentMarkdown = new string(chars);
                                    e.Handled = true;
                                    return;
                                }
                            }
                        }
                    }
                    else
                    {
                        // 脚注伪协议仅用于预览内滚动，解析失败时勿交给 TryOpenLink（否则会打开 https://footnote-back/... 等并抢走焦点）
                        if (
                            !url.StartsWith("footnote:", StringComparison.Ordinal)
                            && !url.StartsWith("footnote-back:", StringComparison.Ordinal)
                        )
                            TryOpenLink(url);
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

    /// <summary>
    /// 在给定文本中，定位第 lineIndex 行（0-based）上的 ToDo 标记 "[ ]" 或 "[x]" 的全局起始位置。
    /// 若该行无 ToDo 或行号越界则返回 -1。避免 Substring 以减 GC。
    /// </summary>
    private static int FindTodoMarkerOnLineByIndex(string text, int lineIndex)
    {
        if (string.IsNullOrEmpty(text) || lineIndex < 0)
            return -1;
        int lineStart = 0;
        for (int i = 0; i < lineIndex; i++)
        {
            int next = text.IndexOf('\n', lineStart);
            if (next < 0)
                return -1;
            lineStart = next + 1;
        }
        int lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (lineEnd <= lineStart + 2)
            return -1;

        for (int i = lineStart; i + 3 <= lineEnd; i++)
        {
            if (text[i] == '[' && text[i + 2] == ']')
            {
                char c = text[i + 1];
                if (c == ' ' || c == 'x' || c == 'X')
                    return i;
            }
        }
        return -1;
    }

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".txt" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };

    /// <summary>解析链接：本地路径（相对当前文档或绝对）则打开文件；否则用浏览器打开。</summary>
    private void TryOpenLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        url = PathSanitizer.Sanitize(url);
        if (url.Length >= 2 && url[0] == '<' && url[^1] == '>')
            url = url[1..^1].Trim();
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                url = new Uri(url).LocalPath;
            }
            catch
            {
                return;
            }
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Core.OpenUrlService.Open(url);
            return;
        }
        string? resolved = null;
        if (DataContext is MainViewModel vm && !string.IsNullOrEmpty(vm.CurrentFilePath))
        {
            var baseDir = Path.GetDirectoryName(vm.CurrentFilePath) ?? "";
            var normalized = url.Replace('/', Path.DirectorySeparatorChar);
            resolved = Path.IsPathRooted(normalized) ? Path.GetFullPath(normalized) : Path.GetFullPath(Path.Combine(baseDir, normalized));
        }
        else if (Path.IsPathRooted(url))
        {
            resolved = Path.GetFullPath(url.Replace('/', Path.DirectorySeparatorChar));
        }
        if (resolved != null && File.Exists(resolved))
        {
            var ext = Path.GetExtension(resolved);
            if (DocExtensions.Contains(ext))
            {
                if (DataContext is MainViewModel m)
                    m.OpenDocument(resolved);
                return;
            }
            if (ImageExtensions.Contains(ext))
            {
                try
                {
                    if (DataContext is MainViewModel m)
                        m.OpenImageInTab(resolved);
                    else
                        Process.Start(
                            new ProcessStartInfo { FileName = resolved, UseShellExecute = true }
                        );
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch { }
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = resolved, UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch { }
            return;
        }

        // 已是本地盘符/UNC 等但文件不存在：勿交给 OpenUrl（旧逻辑会拼 https:// 导致 Win32Exception）
        var checkLocal = url.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(checkLocal) || checkLocal.StartsWith("\\\\", StringComparison.Ordinal))
        {
            try
            {
                checkLocal = Path.GetFullPath(checkLocal);
            }
            catch
            {
                return;
            }
            if (!File.Exists(checkLocal) && !Directory.Exists(checkLocal))
                return;
        }

        Core.OpenUrlService.Open(url);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        var scrollY = GetClampedScrollY();
        var contentY = (float)(pt.Y - EffectiveConfig.ContentPaddingY + scrollY);
        var contentX = (float)(pt.X - EffectiveConfig.ContentPaddingX);
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
                if (!_selectionInvalidateScheduled)
                {
                    _selectionInvalidateScheduled = true;
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            try
                            {
                                _selectionInvalidateScheduled = false;
                                InvalidateVisual();
                            }
                            catch
                            {
                                _selectionInvalidateScheduled = false;
                            }
                        },
                        DispatcherPriority.Background);
                }
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
            var contentY = (float)(pt.Y - EffectiveConfig.ContentPaddingY + scrollY);
            var contentX = (float)(pt.X - EffectiveConfig.ContentPaddingX);
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
            bool isTodo =
                h.linkUrl != null && h.linkUrl.StartsWith("todo-toggle:", StringComparison.Ordinal);
            ToolTip.SetTip(
                this,
                isTodo ? null : (string.IsNullOrEmpty(h.linkUrl) ? null : h.linkUrl)
            );
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
        {
            _anchor = null;
            InvalidateVisual();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            e.Key == Key.C
            && (e.KeyModifiers & KeyModifiers.Control) != 0
            && TryCopySelectionToClipboardAsync().GetAwaiter().GetResult()
        )
        {
            e.Handled = true;
        }
    }

    /// <summary>将当前选区文本写入剪贴板。供本控件 KeyDown 与窗口 Ctrl+C 统一调用。有选区且写入成功返回 true。</summary>
    public async System.Threading.Tasks.Task<bool> TryCopySelectionToClipboardAsync()
    {
        if (_selection is not { } sel || sel.IsEmpty || Document == null)
            return false;
        var text = GetOrCreateEngine()?.GetSelectedText(Document, sel);
        if (string.IsNullOrEmpty(text))
            return false;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        if (double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0)
            _lastValidMeasureWidth = e.NewSize.Width;

        // 仅当宽度变化时重置引擎；高度变化通常由内容布局完成触发，若此时重置会导致闪烁循环
        double newW = e.NewSize.Width;
        bool widthChanged =
            _lastSizeChangedWidth >= 0 && Math.Abs(newW - _lastSizeChangedWidth) > 1;
        _lastSizeChangedWidth = newW;

        if (widthChanged && Document != null)
        {
            // 仅宽度变化时只重算布局（复用已有解析），不重置引擎、不全量解析，避免超长文档卡顿
            InvalidateMeasure();
            if (TriggerLayoutOnlyIfCached())
                return;
            ResetEngine();
            RequestParseAndLayout();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var doc = Document;
        if (doc == null || IsDocumentEmpty(doc))
        {
            double minH = ViewportHeight > 0 ? (double)ViewportHeight : 100;
            double emptyW = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : (Bounds.Width > 0 ? Bounds.Width : 400);
            return ClampMeasureSize(emptyW, Math.Max(100, minH));
        }

        // 一次性算准宽度：无效或零时用当前 Bounds 或上次有效值，避免变窄后复原的闪烁
        double availW =
            double.IsFinite(availableSize.Width) && availableSize.Width > 0
                ? availableSize.Width
                : (Bounds.Width > 0 ? Bounds.Width : _lastValidMeasureWidth);
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
            _lastValidMeasureWidth = availableSize.Width;

        var engine = GetOrCreateEngine();
        if (engine == null)
            return ClampMeasureSize(availW, 100);
        var w = (float)Math.Max(1, availW - EffectiveConfig.ContentPaddingX * 2);
        var cfg = EffectiveConfig;
        float hBefore = engine.MeasureTotalHeight(doc);
        bool widthUnchanged = Math.Abs(w - engine.GetWidth()) <= WidthTolerance;
        bool hasLayout = hBefore > cfg.ExtraBottomPadding + LayoutHeightThreshold;
        if (!hasLayout || widthUnchanged)
            engine.SetWidth(w);
        float h = engine.MeasureTotalHeight(doc);
        if (h <= cfg.ExtraBottomPadding + LayoutHeightThreshold && hasLayout)
            h = hBefore;
        float contentWidth = engine.MeasureContentWidth(doc);
        float totalWidth = EffectiveConfig.ContentPaddingX * 2 + contentWidth;
        double width = Math.Max(availW, (double)totalWidth);
        double height = Math.Max(100, (double)h);
        // 内容较少时至少占满视口高度，避免预览区下方露出 ScrollViewer 默认黑色
        if (ViewportHeight > 0 && height < (double)ViewportHeight)
            height = (double)ViewportHeight;
        return ClampMeasureSize(width, height);
    }

    /// <summary>确保返回的尺寸为有限正数，避免 Avalonia 报 "Invalid size returned for Measure"。</summary>
    private static Size ClampMeasureSize(double width, double height)
    {
        if (!double.IsFinite(width) || width < 0)
            width = 400;
        if (!double.IsFinite(height) || height < 0)
            height = 100;
        return new Size(width, height);
    }

    private void DrawBackgroundOnly(DrawingContext context, Rect bounds)
    {
        var bg = EffectiveConfig.PageBackground;
        var color = Color.FromArgb(
            (byte)(bg >> 24),
            (byte)(bg >> 16),
            (byte)(bg >> 8),
            (byte)bg
        );
        context.FillRectangle(new SolidColorBrush(color), new Rect(0, 0, bounds.Width, bounds.Height));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var doc = Document;
        if (doc == null)
        {
            // 无文档时只绘制主题背景，避免预览区露出黑色
            DrawBackgroundOnly(context, bounds);
            return;
        }
        if (IsDocumentEmpty(doc))
        {
            // 无实质内容时不创建渲染引擎，仅绘制背景，降低空文档内存（约数十 MB）
            _engine = null;
            DrawBackgroundOnly(context, bounds);
            return;
        }

        var rawScrollY = ScrollOffset;
        var config = EffectiveConfig;
        var w = (float)Math.Max(1, bounds.Width - config.ContentPaddingX * 2);

        if (_engine == null)
            _engine = new RenderEngine(w, config);
        else
        {
            bool widthUnchanged = Math.Abs(w - _engine.GetWidth()) <= WidthTolerance;
            bool hasLayout = _engine.MeasureTotalHeight(doc) > config.ExtraBottomPadding + LayoutHeightThreshold;
            if (!hasLayout || widthUnchanged)
                _engine.SetWidth(w);
        }

        var viewportHeight = (float)bounds.Height;
        var totalHeight = _engine.MeasureTotalHeight(doc);

        // 将滚动偏移裁剪在 [0, max(0, totalHeight - viewportHeight)] 范围内，
        // 避免当 ScrollViewer 允许滚动超过内容高度时，内容整体被平移出视口只剩空白。
        var maxScroll = Math.Max(0, totalHeight - viewportHeight);
        var scrollY = Math.Clamp(rawScrollY, 0, maxScroll);

        var padX = EffectiveConfig.ContentPaddingX;
        var padY = EffectiveConfig.ContentPaddingY;
        context.Custom(
            new EngineDrawOp(
                new Rect(0, 0, bounds.Width, bounds.Height),
                doc,
                _engine,
                scrollY,
                w,
                viewportHeight,
                _selection,
                padX,
                padY
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
        private readonly float _contentPaddingX;
        private readonly float _contentPaddingY;

        public EngineDrawOp(
            Rect rect,
            IDocumentSource doc,
            RenderEngine engine,
            float scrollY,
            float width,
            float height,
            SelectionRange? selection,
            float contentPaddingX,
            float contentPaddingY
        )
        {
            _rect = rect;
            _doc = doc;
            _engine = engine;
            _scrollY = scrollY;
            _width = width;
            _height = height;
            _selection = selection;
            _contentPaddingX = contentPaddingX;
            _contentPaddingY = contentPaddingY;
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

            // 直接绘制到主画布，不占用额外离屏缓冲；由 Avalonia 负责呈现与防撕裂
            RenderToCanvas(canvas);
        }

        private void RenderToCanvas(SKCanvas canvas)
        {
            var skCtx = new SkiaRenderContext
            {
                Canvas = canvas,
                Size = new SKSize(_width, _height),
                Scale = 1f
            };

            // 先用渲染引擎配置中的整页背景色清屏，使预览区域底色来自 Markdown 配置而非界面主题。
            var cfg = _engine.GetConfig();
            var bg = cfg.PageBackground;
            var bgColor = new SKColor((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg, (byte)(bg >> 24));
            canvas.Clear(bgColor);

            canvas.Save();
            canvas.Translate(_contentPaddingX, _contentPaddingY);
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

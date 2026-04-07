using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using MarkdownEditor;
using MarkdownEditor.ViewModels;
using MarkdownEditor.Engine.Highlighting;
using MarkdownEditor.Controls;
using MarkdownEditor.Services;
using MarkdownEditor.Models;
using DiffPlex.DiffBuilder.Model;
using System.Text;
using MarkdownEditor.Engine;
using MarkdownEditor.Export;
using MarkdownEditor.Helpers;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MarkdownEditor.Views;

public partial class MainWindow : Window
{
    private static WindowIcon? _appIcon;

    /// <summary>应用图标（icon.ico），供主窗口及所有对话框使用。</summary>
    public static WindowIcon? GetAppIcon()
    {
        if (_appIcon == null)
        {
            try
            {
                using var s = AssetLoader.Open(new Uri("avares://MarkdownEditor/asserts/icon.ico"));
                _appIcon = new WindowIcon(s);
            }
            catch { }
        }
        return _appIcon;
    }

    private DocumentItem? _documentTabContextTarget;
    private MenuFlyout? _documentTabContextFlyout;

    /// <summary>自本次 Ctrl 按下起是否出现过 Ctrl+其它键；为 true 时本次 Ctrl 松开不算「纯 Ctrl」。</summary>
    private bool _editorCtrlSessionHadCombo;

    /// <summary>已有一次「纯 Ctrl」松开，正在等第二次纯 Ctrl 松开（600ms 内）。</summary>
    private bool _awaitingSecondCleanCtrlUp;

    private long _firstCleanCtrlUpMs;
    private bool _isClosingProgrammatically;
    private readonly DispatcherTimer _searchDebounceTimer;
    /// <summary>标记当前是否需要拦截下一次 Alt 键抬起事件，避免 Alt 组合键操作后激活菜单访问键。</summary>
    private bool _suppressNextAltKeyUp;
    /// <summary>侧栏分隔条是否正在拖动（仅显示蓝色预览线，松手后应用布局）。</summary>
    private bool _sidebarSplitterDragging;
    /// <summary>编辑/预览分隔条是否正在拖动。</summary>
    private bool _contentSplitterDragging;
    /// <summary>当前是否为程序化导航（Alt+Left/Right、搜索结果等），若是则 SelectionChanged 不应 PushBack，避免无限循环。</summary>
    private bool _isProgrammaticNavigation;
    private readonly ExportService _exportService = new(
    [
        new HtmlExporter(),
        new PdfExporter(),
        new LongImageExporter(),
        new DocxExporter(),
    ]);

    private readonly EditorController _editorController;

    private MarkdownColorizer? _diffLeftColorizer;
    private MarkdownColorizer? _diffRightColorizer;
    private DualPaneDiffBackgroundRenderer? _diffLeftBgRenderer;
    private DualPaneDiffBackgroundRenderer? _diffRightBgRenderer;
    private DualPaneDiffSymbolMargin? _diffLeftSymbolMargin;
    private DualPaneDiffSymbolMargin? _diffRightSymbolMargin;
    private IReadOnlyList<ChangeType>? _diffLeftLineTypes;
    private IReadOnlyList<ChangeType>? _diffRightLineTypes;
    private ScrollViewer? _diffLeftScroll;
    private ScrollViewer? _diffRightScroll;
    private bool _diffSyncingScroll;
    private string? _diffTooltipCachedLeft;
    private string? _diffTooltipCachedRight;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        if (EditorTextBox is null)
            throw new InvalidOperationException("EditorTextBox not initialized.");
        _editorController = new EditorController(EditorTextBox, vm);

        if (GetAppIcon() is { } icon)
            Icon = icon;

        Opened += (_, _) =>
        {
            ApplyThemeFromConfig(vm);
            EditorTextBox.TextArea.AddHandler(InputElement.KeyDownEvent, EditorTextAreaOnKeyDown, RoutingStrategies.Tunnel);
            EditorTextBox.TextArea.AddHandler(InputElement.KeyUpEvent, EditorTextAreaOnKeyUp, RoutingStrategies.Tunnel);
            SetupDiffPaneEditors(vm);
            RegisterAutoSaveDialogs(vm);
            if (PreviewEngine != null && DataContext is MainViewModel m)
                PreviewEngine.GotFocus += (_, _) => m.ActivePane = "Preview";
            EditorPaneGrid?.AddHandler(PointerWheelChangedEvent, OnEditorPanePointerWheelZoom, RoutingStrategies.Tunnel);
            // 默认让编辑区获得焦点，使 Ctrl+/- 控制编辑区缩放，直到用户点击预览区
            EditorTextBox?.Focus();
        };

        // 根据窗口状态调整内边距，解决最大化时内容被挤到屏幕外的问题（Reactive 写法）
        this.GetObservable(WindowStateProperty)
            .Subscribe(state =>
            {
                Padding = state == WindowState.Maximized
                    ? new Thickness(8, 8, 8, 8)
                    : new Thickness(0);
            });

        ApplyLayout(vm.LayoutMode);
        SyncLayoutModeButton(vm);
        vm.PropertyChanged += VmOnPropertyChanged;
        vm.ThemeChanged += OnThemeChanged;
        vm.OpenImageInNewWindowRequested += path =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var win = new ImageViewWindow();
                    win.SetImagePath(path);
                    win.ShowWithOwner(this);
                }
                catch { }
            });
        };

        // 标题栏中间区域拖动移动窗口
        TitleDragArea.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(TitleDragArea).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        // 标题栏双击最大化 / 还原窗口
        TitleDragArea.DoubleTapped += (_, _) =>
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        };

        // 底部状态栏布局切换（下拉菜单）
        if (LayoutModeButton != null)
        {
            LayoutBothMenuItem.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.Both);
            LayoutEditorOnlyMenuItem.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.EditorOnly);
            LayoutPreviewOnlyMenuItem.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.PreviewOnly);
        }

        StatusBarPathBorder?.Tapped += (_, _) => OpenCurrentFileFolderInExplorer();

        NewFileMenuItem.Click += (_, _) => vm.NewDocument();

        OpenFileMenuItem.Click += async (_, _) => await DoOpenFileAsync(vm);
        WelcomeOpenFileButton!.Click += async (_, _) => await DoOpenFileAsync(vm);

        OpenFolderMenuItem.Click += async (_, _) => await DoOpenFolderAsync(vm);
        WelcomeOpenFolderButton!.Click += async (_, _) => await DoOpenFolderAsync(vm);
        AddFoldersMenuItem?.Click += async (_, _) => await DoAddFoldersAsync(vm);
        SaveWorkspaceMenuItem?.Click += async (_, _) => await DoSaveWorkspaceAsync(vm);
        OpenWorkspaceMenuItem?.Click += async (_, _) => await DoOpenWorkspaceAsync(vm);

        SaveMenuItem.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(vm.CurrentFilePath))
                _ = await DoSaveAsAsync(vm);
            else
                vm.SaveCurrent();
        };

        SaveAsMenuItem.Click += async (_, _) => _ = await DoSaveAsAsync(vm);

        CloseEditorMenuItem.Click += async (_, _) => await ConfirmCloseCurrentEditorAsync(vm);

        ExitMenuItem.Click += async (_, _) => await ConfirmExitAsync(vm);
        ShortcutsHelpMenuItem.Click += (_, _) =>
        {
            var w = new ShortcutsHelpWindow();
            w.Show(this);
        };
        AboutMenuItem.Click += (_, _) => ShowAboutDialog();

        ExportHtmlMenuItem.Click += async (_, _) => await DoExportAsync(vm, "html");
        ExportPdfMenuItem.Click += async (_, _) => await DoExportAsync(vm, "pdf");
        ExportPngMenuItem.Click += async (_, _) => await DoExportAsync(vm, "png");
        ExportDocxMenuItem.Click += async (_, _) => await DoExportAsync(vm, "docx");

        Closing += (_, e) =>
        {
            if (_isClosingProgrammatically) return;
            if (DataContext is MainViewModel m && m.IsModified)
            {
                e.Cancel = true;
                _ = ConfirmExitAsync(m);
            }
        };

        ActivityExplorerButton.Click += (_, _) => vm.IsExplorerActive = true;
        ActivitySearchButton.Click += (_, _) => vm.IsSearchActive = true;
        ActivityGitButton.Click += (_, _) =>
        {
            vm.IsGitActive = true;
            vm.GitPaneViewModel.RefreshRepositories();
            vm.GitPaneViewModel.TimelineFilePath = vm.CurrentFilePath;
        };
        ActivitySettingsButton.Click += (_, _) => vm.IsSettingsActive = true;
        UpdateActivityBarHighlight(vm);

        SetupEditorContextMenuAndKeys(vm);
        SetupPreviewContextMenu();
        SetupFileTreeContextMenu(vm);
        SetupFileTreeRootButtons(vm);
        SetupDocumentTabBackStack(vm);
        if (NewDocumentTabButton != null)
            NewDocumentTabButton.Click += (_, _) => vm.NewDocument();
        SetupSearchResultsNavigation(vm);
        SetupGitPane(vm);
        Closed += (_, _) =>
        {
            try
            {
                StopAllTimers();
                vm.Config.Save(Core.AppConfig.DefaultConfigPath);
                vm.SaveRecentState();
            }
            catch
            {
                // 关闭时保存或停定时器失败也不影响窗口关闭
            }
        };

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            try
            {
                _searchDebounceTimer.Stop();
                var v = DataContext as MainViewModel;
                if (v != null)
                    Dispatcher.UIThread.Post(() => v.DoSearch(), DispatcherPriority.Background);
            }
            catch
            {
                _searchDebounceTimer.Stop();
            }
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SearchQuery))
            {
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
            }
        };
        if (EditorTextBox != null)
            EditorTextBox.GotFocus += (_, _) => vm.ActivePane = "Editor";

        // 点击编辑区/预览区任意位置即切换激活窗格；用 Tunnel 在事件到达内层控件前处理，确保点击编辑区时能更新 ActivePane（状态栏缩放百分比等）
        if (EditorPaneGrid != null)
        {
            EditorPaneGrid.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(EditorPaneGrid).Properties.IsLeftButtonPressed) return;
                vm.ActivePane = "Editor";
                EditorTextBox?.Focus();
                Dispatcher.UIThread.Post(() => EditorTextBox?.Focus(), DispatcherPriority.Input);
            }, RoutingStrategies.Tunnel);
        }
        if (PreviewPaneGrid != null)
        {
            PreviewPaneGrid.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(PreviewPaneGrid).Properties.IsLeftButtonPressed) return;
                vm.ActivePane = "Preview";
                Dispatcher.UIThread.Post(() =>
                {
                    PreviewEngine?.Focus();
                }, DispatcherPriority.Input);
            }, RoutingStrategies.Tunnel);
        }

        if (FileTreeList != null)
        {
            // 整行点击：左键展开/折叠文件夹并选中，右键仅选中（ContextFlyout 用）
            FileTreeList.AddHandler(PointerPressedEvent, FileTreeListOnPointerPressed, RoutingStrategies.Tunnel);
        }

        // 监听按键抬起事件，用于在执行 Alt 组合键（键盘或鼠标）后拦截随后的“裸 Alt”抬起，避免焦点跳到菜单栏。
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // 监听全局鼠标按下事件：当检测到 Alt+左键（列选择等）时，标记需要拦截下一次 Alt KeyUp。
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        // 点击重命名框外时提交重命名，避免因 AvaloniaEdit 等抛出 VisualLinesInvalidException 导致焦点无法移出而卡住
        AddHandler(PointerPressedEvent, OnClickOutsideRenameCommit, RoutingStrategies.Tunnel);

        // 焦点进入编辑区/预览区时同步 ActivePane，使状态栏缩放百分比等即时更新
        AddHandler(GotFocusEvent, OnWindowGotFocus, RoutingStrategies.Bubble);

        SetupDeferredSplitters();

    }

    /// <summary>VS 风格分隔条：拖动时只显示蓝色预览线，松手后再应用布局。</summary>
    private void SetupDeferredSplitters()
    {
        const double sidebarMin = 180;
        const double sidebarMax = 420;
        const double contentMinWidth = 120;

        // 启动时根据配置恢复侧栏宽度与折叠状态
        if (MainGrid != null && DataContext is MainViewModel vmInit)
        {
            ApplySidebarCollapsedState(vmInit);
            vmInit.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed) && DataContext is MainViewModel vm)
                    ApplySidebarCollapsedState(vm);
            };
        }

        if (SidebarSplitter != null && MainGrid != null && SidebarSplitterPreviewLine != null && SidebarSplitterOverlay != null)
        {
            SidebarSplitter.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(SidebarSplitter).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _sidebarSplitterDragging = true;
                var pt = e.GetPosition(MainGrid);
                SidebarSplitterPreviewLine.IsVisible = true;
                SidebarSplitterPreviewLine.Margin = new Thickness(Math.Clamp(pt.X, sidebarMin, sidebarMax), 0, 0, 0);
                e.Pointer.Capture(SidebarSplitter);
            };
            SidebarSplitter.PointerMoved += (s, e) =>
            {
                if (!_sidebarSplitterDragging || MainGrid == null) return;
                var pt = e.GetPosition(MainGrid);
                var x = Math.Clamp(pt.X, sidebarMin, sidebarMax);
                SidebarSplitterPreviewLine!.Margin = new Thickness(x, 0, 0, 0);
                if (SidebarCollapseStripOverlay != null && DataContext is MainViewModel vmMove && !vmMove.IsSidebarCollapsed)
                {
                    var overlayLeft = x - ScrollbarWidth - SidebarCollapseFloatingWidth;
                    SidebarCollapseStripOverlay.Margin = new Thickness(overlayLeft, 0, 0, 0);
                }
            };
            SidebarSplitter.PointerReleased += (s, e) =>
            {
                if (e.GetCurrentPoint(SidebarSplitter).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
                if (!_sidebarSplitterDragging) return;
                _sidebarSplitterDragging = false;
                e.Pointer.Capture(null);
                SidebarSplitterPreviewLine!.IsVisible = false;
                var pt = e.GetPosition(MainGrid);
                var w = Math.Clamp(pt.X, sidebarMin, sidebarMax);
                var cols = MainGrid!.ColumnDefinitions;
                if (cols.Count >= 1)
                    cols[0].Width = new GridLength(w, GridUnitType.Pixel);
                if (DataContext is MainViewModel vmSidebar)
                {
                    vmSidebar.Config.Ui.DocumentListWidth = w;
                    ApplySidebarCollapsedState(vmSidebar);
                }
            };
            SidebarSplitter.PointerCaptureLost += (s, e) =>
            {
                if (_sidebarSplitterDragging)
                {
                    _sidebarSplitterDragging = false;
                    SidebarSplitterPreviewLine!.IsVisible = false;
                }
            };
        }

        if (ContentSplitter != null && EditorPreviewSplitGrid != null && ContentSplitterPreviewLine != null && ContentSplitterOverlay != null)
        {
            ContentSplitter.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(ContentSplitter).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _contentSplitterDragging = true;
                var pt = e.GetPosition(EditorPreviewSplitGrid);
                var total = EditorPreviewSplitGrid.Bounds.Width;
                var maxX = Math.Max(contentMinWidth, total - 4 - contentMinWidth);
                var x = Math.Clamp(pt.X, contentMinWidth, maxX);
                ContentSplitterPreviewLine.IsVisible = true;
                ContentSplitterPreviewLine.Margin = new Thickness(x, 0, 0, 0);
                e.Pointer.Capture(ContentSplitter);
            };
            ContentSplitter.PointerMoved += (s, e) =>
            {
                if (!_contentSplitterDragging || EditorPreviewSplitGrid == null) return;
                var pt = e.GetPosition(EditorPreviewSplitGrid);
                var total = EditorPreviewSplitGrid.Bounds.Width;
                var maxX = Math.Max(contentMinWidth, total - 4 - contentMinWidth);
                var x = Math.Clamp(pt.X, contentMinWidth, maxX);
                ContentSplitterPreviewLine!.Margin = new Thickness(x, 0, 0, 0);
            };
            ContentSplitter.PointerReleased += (s, e) =>
            {
                if (e.GetCurrentPoint(ContentSplitter).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
                if (!_contentSplitterDragging) return;
                _contentSplitterDragging = false;
                e.Pointer.Capture(null);
                ContentSplitterPreviewLine!.IsVisible = false;
                var pt = e.GetPosition(EditorPreviewSplitGrid);
                var total = EditorPreviewSplitGrid.Bounds.Width;
                var maxX = Math.Max(contentMinWidth, total - 4 - contentMinWidth);
                var w = Math.Clamp(pt.X, contentMinWidth, maxX);
                var cols = EditorPreviewSplitGrid.ColumnDefinitions;
                if (cols.Count >= 3)
                {
                    // Star 比例，窗口变宽/变窄时两栏随总宽变化；列必须写在 EditorPreviewSplitGrid 上（外层 ContentSplitGrid 无三列）。
                    var usable = total > 4 ? total - 4 : total;
                    var ratio = usable > 0 ? Math.Clamp(w / usable, 0.1, 0.9) : 0.5;
                    cols[0].Width = new GridLength(ratio, GridUnitType.Star);
                    cols[1].Width = new GridLength(4, GridUnitType.Pixel);
                    cols[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
                    if (DataContext is MainViewModel vmContent)
                        vmContent.Config.Ui.EditorWidth = ratio;
                }
            };
            ContentSplitter.PointerCaptureLost += (s, e) =>
            {
                if (_contentSplitterDragging)
                {
                    _contentSplitterDragging = false;
                    ContentSplitterPreviewLine!.IsVisible = false;
                }
            };
        }
    }

    private const double SidebarCollapsedWidth = 20;
    /// <summary>展开时折叠箭头悬浮块宽度（仅箭头，不留白）；贴滚动条左侧。</summary>
    private const double SidebarCollapseFloatingWidth = 24;
    private const double ScrollbarWidth = 12;
    private const double SidebarMinWidth = 180;
    private const double SidebarMaxWidth = 420;

    private const double SidebarSplitterWidth = 4;

    private void ApplySidebarCollapsedState(MainViewModel vm)
    {
        if (MainGrid == null || SidebarInnerGrid == null || SidebarCollapseStripOverlay == null)
            return;
        var collapsed = vm.IsSidebarCollapsed;
        // 折叠时隐藏整个内容区（顶部活动栏 + 路径 + 文件树等），只保留悬浮折叠条
        if (SidebarContentGrid != null)
            SidebarContentGrid.IsVisible = !collapsed;
        var mainCols = MainGrid.ColumnDefinitions;
        double sidebarWidthPx = 0;
        if (mainCols.Count >= 1)
        {
            var col0 = mainCols[0];
            if (collapsed)
            {
                col0.MinWidth = 0;
                col0.MaxWidth = 0;
                col0.Width = new GridLength(0, GridUnitType.Pixel);
            }
            else
            {
                sidebarWidthPx = Math.Clamp(vm.Config.Ui.DocumentListWidth, SidebarMinWidth, SidebarMaxWidth);
                col0.MinWidth = SidebarMinWidth;
                col0.MaxWidth = SidebarMaxWidth;
                col0.Width = new GridLength(sidebarWidthPx, GridUnitType.Pixel);
            }
        }
        // 折叠时分隔列宽度设为 0，不留缝隙；展开时恢复 4px
        if (mainCols.Count >= 2)
            mainCols[1].Width = new GridLength(collapsed ? 0 : SidebarSplitterWidth, GridUnitType.Pixel);
        if (SidebarSplitter != null)
            SidebarSplitter.IsVisible = !collapsed;
        // 折叠箭头单独悬浮：展开时置于滚动条左侧居中，不占内容区留白；折叠时贴左
        var overlayWidth = collapsed ? SidebarCollapsedWidth : SidebarCollapseFloatingWidth;
        SidebarCollapseStripOverlay.Width = overlayWidth;
        var overlayLeft = collapsed ? 0 : (sidebarWidthPx - ScrollbarWidth - overlayWidth);
        SidebarCollapseStripOverlay.Margin = new Thickness(overlayLeft, 0, 0, 0);
        // 折叠时去掉侧栏右边线，与主内容区完全贴齐不留缝
        if (SidebarBorder != null)
            SidebarBorder.BorderThickness = new Thickness(collapsed ? 0 : 1, 0, 0, 0);
    }

    private void SidebarCollapseStrip_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var hitTarget = sender as Visual;
        if (hitTarget == null || !e.GetCurrentPoint(hitTarget).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (DataContext is MainViewModel vm)
            vm.IsSidebarCollapsed = !vm.IsSidebarCollapsed;
    }

    private void SidebarCollapseStrip_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsSidebarCollapseStripHovered = true;
    }

    private void SidebarCollapseStrip_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsSidebarCollapseStripHovered = false;
    }

    /// <summary>窗口关闭时停止所有定时器，避免 Tick 在析构后触发导致异常。</summary>
    private void StopAllTimers()
    {
        try
        {
            _searchDebounceTimer.Stop();
        }
        catch { }
    }

    private void SetupEditorHighlighting()
    {
        if (EditorTextBox is TextEditor editor && DataContext is MainViewModel vm)
        {
            editor.FontSize = 14.0 * vm.EditorZoomLevel;
        }
    }

    private void FileTreeListOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control control || DataContext is not MainViewModel vm)
            return;

        var item = control
            .GetVisualAncestors()
            .OfType<ListBoxItem>()
            .FirstOrDefault();
        var node = item?.DataContext as FileTreeNode;
        if (node == null) return;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            vm.SelectedTreeNode = node;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // 左键：选中由 ListBox 绑定处理；若为文件夹且非重命名则整行点击展开/折叠（VSCode 风格）
        if (node.IsFolder && !node.IsRenaming)
            vm.ToggleFolderNode(node);
    }

    private void SetupFileTreeRootButtons(MainViewModel vm)
    {
        if (FileTreeRefreshRootBtn != null)
            FileTreeRefreshRootBtn.Click += (_, _) => vm.RefreshFileTree();
        if (ExplorerOpenWorkspaceBtn != null)
            ExplorerOpenWorkspaceBtn.Click += async (_, _) => await DoOpenWorkspaceAsync(vm);
        if (ExplorerOpenFolderBtn != null)
            ExplorerOpenFolderBtn.Click += async (_, _) => await DoOpenFolderAsync(vm);
    }

    private void SetupFileTreeContextMenu(MainViewModel vm)
    {
        if (FileTreeCopyPathItem == null || FileTreeRenameItem == null || FileTreeNewFileItem == null || FileTreeNewFolderItem == null || FileTreeDeleteItem == null)
            return;

        if (FileTreeOpenFolderItem != null)
        {
            FileTreeOpenFolderItem.Click += (_, _) =>
            {
                var node = vm.SelectedTreeNode;
                if (node == null) return;
                var dir = node.IsFolder ? node.FullPath : System.IO.Path.GetDirectoryName(node.FullPath);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
                catch { }
            };
        }

        FileTreeCopyPathItem.Click += async (_, _) =>
        {
            var node = vm.SelectedTreeNode;
            if (node == null) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(node.FullPath);
        };

        FileTreeNewFileItem.Click += (_, _) =>
        {
            var node = vm.SelectedTreeNode;
            if (node == null) return;
            var dir = node.IsFolder ? node.FullPath : System.IO.Path.GetDirectoryName(node.FullPath);
            if (!string.IsNullOrEmpty(dir))
                vm.NewFileInFolder(dir);
        };

        FileTreeNewFolderItem.Click += (_, _) =>
        {
            var node = vm.SelectedTreeNode;
            if (node == null) return;
            var dir = node.IsFolder ? node.FullPath : System.IO.Path.GetDirectoryName(node.FullPath);
            if (!string.IsNullOrEmpty(dir))
                vm.NewFolderInFolder(dir);
        };

        FileTreeDeleteItem.Click += async (_, _) =>
        {
            var node = vm.SelectedTreeNode;
            if (node == null) return;
            var path = node.FullPath;
            if (node.IsFolder)
            {
                if (vm.IsWorkspaceRoot(path)) return;
                var dialog = new Window
                {
                    Title = "确认删除文件夹",
                    Width = 360,
                    Height = 140,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Icon = GetAppIcon()
                };
                var ok = new Button { Content = "删除" };
                var cancel = new Button { Content = "取消" };
                cancel.Click += (_, _) => dialog.Close();
                ok.Click += (_, _) =>
                {
                    if (vm.DeleteFolderByPath(path))
                        dialog.Close();
                };
                dialog.Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "将删除该文件夹及其中所有内容（文件与子目录），且无法恢复。确定继续？", TextWrapping = TextWrapping.Wrap },
                        new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } }
                    }
                };
                await dialog.ShowDialog(this);
            }
            else
            {
                var dialog = new Window
                {
                    Title = "确认删除",
                    Width = 320,
                    Height = 120,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Icon = GetAppIcon()
                };
                var ok = new Button { Content = "删除" };
                var cancel = new Button { Content = "取消" };
                cancel.Click += (_, _) => dialog.Close();
                ok.Click += (_, _) =>
                {
                    if (vm.DeleteFileByPath(path))
                        dialog.Close();
                };
                dialog.Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "确定要删除此文件吗？", TextWrapping = TextWrapping.Wrap },
                        new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } }
                    }
                };
                await dialog.ShowDialog(this);
            }
        };

        FileTreeRenameItem.Click += (_, _) =>
        {
            var node = vm.SelectedTreeNode;
            if (node == null) return;
            node.EditName = node.DisplayName;
            node.IsRenaming = true;
        };

        void UpdateFileTreeMenuState()
        {
            var node = vm.SelectedTreeNode;
            var hasNode = node != null;
            if (FileTreeOpenFolderItem != null)
                FileTreeOpenFolderItem.IsEnabled = hasNode;
            FileTreeCopyPathItem.IsEnabled = hasNode;
            FileTreeRenameItem.IsEnabled = hasNode;
            FileTreeNewFileItem.IsEnabled = hasNode;
            FileTreeNewFolderItem.IsEnabled = hasNode;
            FileTreeDeleteItem.IsEnabled = hasNode && (node is { IsFolder: false } || (node!.IsFolder && !vm.IsWorkspaceRoot(node.FullPath)));
            if (FileTreeTimelineItem != null)
            {
                var isFile = node is { IsFolder: false };
                FileTreeTimelineItem.IsVisible = isFile;
                if (isFile && node != null)
                {
                    vm.GitPaneViewModel.TimelineFilePath = node.FullPath;
                    FileTreeTimelineItem.Items.Clear();
                    foreach (var c in vm.GitPaneViewModel.FileHistory)
                    {
                        var commit = c;
                        var path = node.FullPath;
                        var title = new TextBlock
                        {
                            Text = c.MessageShort,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 260,
                            FontSize = 12,
                        };
                        var time = new TextBlock
                        {
                            Text = c.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                            FontSize = 10,
                            Opacity = 0.75,
                        };
                        var mi = new MenuItem
                        {
                            Header = new StackPanel
                            {
                                Orientation = Orientation.Vertical,
                                Spacing = 0,
                                Children = { title, time },
                            },
                        };
                        mi.Click += (_, _) =>
                        {
                            vm.EnterCompareWithCommit(path, commit.Sha);
                            Dispatcher.UIThread.Post(RefreshDualDiffDisplay, DispatcherPriority.Loaded);
                        };
                        FileTreeTimelineItem.Items.Add(mi);
                    }
                }
            }
        }

        if (FileTreeList?.ContextFlyout is MenuFlyout flyout)
        {
            flyout.Opening += (_, _) => UpdateFileTreeMenuState();
        }
    }

    private static readonly object FileTreeRenameTextBoxHandlersRegistered = new();

    /// <summary>
    /// 文件树重命名框：首次 Loaded 时注册按键与可见性监听。
    /// IsVisible 随 IsRenaming 切换时不会再次触发 Loaded，故需在 IsVisible 变为 true 时再次聚焦并全选。
    /// </summary>
    private void TreeItemRenameTextBox_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.TextBox box || box.DataContext is not FileTreeNode)
            return;
        if (!ReferenceEquals(box.Tag, FileTreeRenameTextBoxHandlersRegistered))
        {
            box.Tag = FileTreeRenameTextBoxHandlersRegistered;
            void OnPreviewKeyDown(object? s, KeyEventArgs ev)
            {
                if (box.DataContext is not FileTreeNode n || !n.IsRenaming)
                    return;
                if (ev.Key == Key.Enter)
                {
                    CommitTreeItemRename(box);
                    ev.Handled = true;
                }
                else if (ev.Key == Key.Escape)
                {
                    n.IsRenaming = false;
                    n.EditName = n.DisplayName;
                    ev.Handled = true;
                }
            }
            box.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
            box.PropertyChanged += (_, args) =>
            {
                if (!ReferenceEquals(args.Property, Visual.IsVisibleProperty))
                    return;
                if (!box.IsVisible || box.DataContext is not FileTreeNode n || !n.IsRenaming)
                    return;
                ScheduleFocusAndSelectRenameTextBox(box);
            };
        }

        if (box.IsVisible && box.DataContext is FileTreeNode node && node.IsRenaming)
            ScheduleFocusAndSelectRenameTextBox(box);
    }

    /// <summary>延迟两帧聚焦并全选，避免与模板/布局抢焦点（如新建项后立即重命名）。</summary>
    private static void ScheduleFocusAndSelectRenameTextBox(Avalonia.Controls.TextBox box)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        try
                        {
                            if (!box.IsVisible)
                                return;
                            box.Focus();
                            var len = box.Text?.Length ?? 0;
                            if (len > 0)
                            {
                                box.SelectionStart = 0;
                                box.SelectionEnd = len;
                            }
                        }
                        catch
                        {
                            // 控件已脱离视觉树等时忽略
                        }
                    },
                    DispatcherPriority.Loaded
                );
            },
            DispatcherPriority.Loaded
        );
    }

    private void TreeItemRename_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        CommitTreeItemRename(sender);
    }

    private async void CommitTreeItemRename(object? sender)
    {
        if (sender is not Avalonia.Controls.TextBox box || box.DataContext is not FileTreeNode node || DataContext is not MainViewModel vm)
            return;
        if (!node.IsRenaming) return;
        node.IsRenaming = false;
        var newName = box.Text?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == node.DisplayName) return;
        string? error = null;
        if (node.IsFolder)
        {
            var (_, err) = vm.RenameFolderByPath(node.FullPath, newName);
            error = err;
        }
        else
        {
            var (_, err) = vm.RenameFileByPath(node.FullPath, newName);
            error = err;
        }
        if (!string.IsNullOrEmpty(error))
            await ShowRenameErrorDialogAsync(error);
    }

    /// <summary>标签页行为：记录后退栈 + 中键关闭 + 右键菜单 + 关闭按钮。</summary>
    private void SetupDocumentTabBackStack(MainViewModel vm)
    {
        if (DocumentTabControl == null) return;
        DocumentTabControl.SelectionChanged += (_, e) =>
        {
            // 仅在用户手动切换标签时记录历史；程序化导航（Alt+Left/Right、搜索结果等）通过 _isProgrammaticNavigation 保护，避免形成环路。
            if (_isProgrammaticNavigation) return;
            if (e.RemovedItems?.Count > 0 && e.RemovedItems[0] is DocumentItem prev && !string.IsNullOrEmpty(prev.FullPath))
                vm.RecordLocation(prev.FullPath, prev.LastCaretOffset);
        };

        SetupDocumentTabContextFlyout(vm);

        // 中键关闭 / 右键菜单
        DocumentTabControl.AddHandler(
            PointerReleasedEvent,
            async (_, e) =>
            {
                var kind = e.GetCurrentPoint(DocumentTabControl).Properties.PointerUpdateKind;
                if (kind == PointerUpdateKind.RightButtonReleased)
                {
                    if (e.Source is not Control c || DataContext is not MainViewModel m)
                        return;
                    var tab = c.GetVisualAncestors().OfType<TabItem>().FirstOrDefault();
                    if (tab?.DataContext is not DocumentItem item)
                        return;
                    _documentTabContextTarget = item;
                    if (_documentTabContextFlyout != null)
                    {
                        _documentTabContextFlyout.ShowAt(tab, showAtPointer: true);
                        e.Handled = true;
                    }

                    return;
                }

                if (kind != PointerUpdateKind.MiddleButtonReleased)
                    return;
                if (e.Source is not Control c2 || c2.DataContext is not DocumentItem item2 || DataContext is not MainViewModel m2)
                    return;

                bool closed = await ConfirmCloseDocumentAsync(m2, item2);
                if (closed)
                    e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        // 左键点击关闭按钮（tab-close）。Click 事件从按钮向上冒泡，故用 Bubble 才能收到。
        DocumentTabControl.AddHandler(
            Button.ClickEvent,
            async (_, e) =>
            {
                if (e.Source is not Button btn || !btn.Classes.Contains("tab-close") || btn.DataContext is not DocumentItem item || DataContext is not MainViewModel m)
                    return;

                bool closed = await ConfirmCloseDocumentAsync(m, item);
                if (closed)
                    e.Handled = true;
            },
            RoutingStrategies.Bubble);
    }

    /// <summary>统一的“关闭文档前确认”逻辑：保存 / 放弃 / 取消。</summary>
    private async Task<bool> ConfirmCloseDocumentAsync(MainViewModel vm, DocumentItem item)
    {
        // 未修改直接关
        if (!item.IsModified)
        {
            vm.CloseDocument(item);
            return true;
        }

        // 切换到目标文档，确保对话框展示的路径/内容一致
        if (vm.ActiveDocument != item)
            vm.ActiveDocument = item;

        var dialog = new ConfirmCloseWindow();
        dialog.SetDocumentPath(string.IsNullOrEmpty(item.FullPath) ? item.RelativePath : item.FullPath);
        await dialog.ShowDialog(this);

        if (dialog.Result == ConfirmCloseResult.Cancel)
            return false;

        if (dialog.Result == ConfirmCloseResult.Save)
        {
            if (string.IsNullOrEmpty(item.FullPath))
            {
                if (!await DoSaveAsAsync(vm))
                    return false;
                // 新建文档保存后 SaveToPath 已移除未命名文档并打开保存后的文件，item 可能已不在列表中
                return true;
            }

            vm.SaveCurrent();
            vm.CloseDocument(item);
            return true;
        }

        vm.CloseDocument(item);
        return true;
    }

    private void SetupDocumentTabContextFlyout(MainViewModel vm)
    {
        var reload = new MenuItem { Header = "重新加载" };
        reload.Click += async (_, _) =>
        {
            var t = _documentTabContextTarget;
            if (t != null && DataContext is MainViewModel m)
                await ReloadDocumentTabFromDiskAsync(m, t);
        };
        var close = new MenuItem { Header = "关闭" };
        close.Click += async (_, _) =>
        {
            var t = _documentTabContextTarget;
            if (t != null && DataContext is MainViewModel m)
                await ConfirmCloseDocumentAsync(m, t);
        };
        var saveClose = new MenuItem { Header = "保存并关闭" };
        saveClose.Click += async (_, _) =>
        {
            var t = _documentTabContextTarget;
            if (t != null && DataContext is MainViewModel m)
                await SaveAndCloseDocumentAsync(m, t);
        };
        var closeOthers = new MenuItem { Header = "关闭其他" };
        closeOthers.Click += async (_, _) =>
        {
            var t = _documentTabContextTarget;
            if (t != null && DataContext is MainViewModel m)
                await CloseOtherDocumentsAsync(m, t);
        };
        var closeAll = new MenuItem { Header = "关闭全部" };
        closeAll.Click += async (_, _) =>
        {
            if (DataContext is MainViewModel m)
                await CloseAllDocumentsAsync(m);
        };

        _documentTabContextFlyout = new MenuFlyout
        {
            Items =
            {
                reload,
                new MenuItem { Header = "-" },
                close,
                saveClose,
                new MenuItem { Header = "-" },
                closeOthers,
                closeAll,
            },
        };
    }

    private async Task ReloadDocumentTabFromDiskAsync(MainViewModel vm, DocumentItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FullPath))
        {
            await ShowSimpleInfoDialogAsync("重新加载", "未命名文档没有磁盘路径，无法重新加载。");
            return;
        }

        if (!File.Exists(item.FullPath))
        {
            await ShowSimpleInfoDialogAsync("重新加载", "文件在磁盘上不存在，无法重新加载。");
            return;
        }

        if (item.IsModified)
        {
            if (!await ShowReloadDiscardUnsavedDialogAsync(item.DisplayName))
                return;
        }

        vm.ReloadDocumentFromDisk(item);
    }

    private async Task ShowSimpleInfoDialogAsync(string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 400,
            MaxWidth = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        var ok = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 368 },
                ok,
            },
        };
        await dlg.ShowDialog(this);
    }

    private async Task<bool> ShowReloadDiscardUnsavedDialogAsync(string displayName)
    {
        bool confirm = false;
        var dlg = new Window
        {
            Title = "重新加载",
            Width = 440,
            MaxWidth = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        var yes = new Button { Content = "重新加载", Margin = new Thickness(0, 0, 8, 0) };
        yes.Click += (_, _) =>
        {
            confirm = true;
            dlg.Close();
        };
        var no = new Button { Content = "取消" };
        no.Click += (_, _) => dlg.Close();
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { yes, no },
        };
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text =
                        $"「{displayName}」有未保存的修改。\n\n从磁盘重新加载将丢弃这些修改，是否继续？",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                buttons,
            },
        };
        await dlg.ShowDialog(this);
        return confirm;
    }

    private async Task SaveAndCloseDocumentAsync(MainViewModel vm, DocumentItem item)
    {
        if (vm.ActiveDocument != item)
            vm.ActiveDocument = item;
        if (string.IsNullOrEmpty(item.FullPath))
        {
            if (!await DoSaveAsAsync(vm))
                return;
            return;
        }

        vm.SaveCurrent();
        if (vm.OpenDocuments.Contains(item))
            vm.CloseDocument(item);
    }

    private async Task CloseOtherDocumentsAsync(MainViewModel vm, DocumentItem keep)
    {
        foreach (var doc in vm.OpenDocuments.ToList())
        {
            if (ReferenceEquals(doc, keep))
                continue;
            if (!await ConfirmCloseDocumentAsync(vm, doc))
                return;
        }
    }

    private async Task CloseAllDocumentsAsync(MainViewModel vm)
    {
        foreach (var doc in vm.OpenDocuments.ToList())
        {
            if (!await ConfirmCloseDocumentAsync(vm, doc))
                return;
        }
    }

    /// <summary>
    /// 编辑区连续两次「仅 Ctrl、无组合键」：在第二次松开 Ctrl 时让预览滚到光标附近；若当前为「仅编辑」则先切「编辑+预览」。
    /// Ctrl+C 等任意 Ctrl 组合会取消本次序列，且该次松开 Ctrl 不计入双击。
    /// </summary>
    private void EditorTextAreaOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _editorCtrlSessionHadCombo = false;
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            _editorCtrlSessionHadCombo = true;
    }

    private void EditorTextAreaOnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.LeftCtrl and not Key.RightCtrl)
            return;

        if (_editorCtrlSessionHadCombo)
        {
            _awaitingSecondCleanCtrlUp = false;
            _editorCtrlSessionHadCombo = false;
            return;
        }

        long now = Environment.TickCount64;
        if (_awaitingSecondCleanCtrlUp)
        {
            long dt = now - _firstCleanCtrlUpMs;
            if (dt >= 0 && dt <= 600)
            {
                _awaitingSecondCleanCtrlUp = false;
                e.Handled = true;
                TryScrollPreviewToCaret();
                return;
            }
        }

        _firstCleanCtrlUpMs = now;
        _awaitingSecondCleanCtrlUp = true;
        _editorCtrlSessionHadCombo = false;
    }

    private void TryScrollPreviewToCaret()
    {
        if (DataContext is not MainViewModel vm)
            return;
        if (vm.IsDiffCompareActive)
            return;
        if (!vm.ShowEditorPaneForCurrentDoc)
            return;
        if (!vm.ShowMarkdownPreview)
            return;
        if (EditorTextBox is not TextEditor editor || editor.Document == null)
            return;

        int offset = editor.TextArea.Caret.Offset;

        void AttemptScroll(int attempt)
        {
            var rc = PreviewEngine?.RenderControl;
            if (rc == null)
                return;
            if (rc.TryScrollToDocumentOffset(offset))
            {
                vm.ActivePane = "Preview";
                return;
            }

            if (attempt < 4)
                Dispatcher.UIThread.Post(() => AttemptScroll(attempt + 1), DispatcherPriority.Loaded);
        }

        if (vm.LayoutMode == EditorLayoutMode.EditorOnly)
        {
            vm.LayoutMode = EditorLayoutMode.Both;
            ApplyLayout(EditorLayoutMode.Both);
            Dispatcher.UIThread.Post(
                () => Dispatcher.UIThread.Post(() => AttemptScroll(0), DispatcherPriority.Loaded),
                DispatcherPriority.Loaded
            );
            return;
        }

        AttemptScroll(0);
    }

    private void ApplyThemeFromConfig(MainViewModel vm)
    {
        var isLight = string.Equals(vm.Config.Ui.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        var variant = isLight ? ThemeVariant.Light : ThemeVariant.Dark;
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = variant;
            var style = isLight ? vm.Config.Ui.LightStyle : vm.Config.Ui.DarkStyle;
            Helpers.UiStyleApplier.Apply(Application.Current.Resources, style);
        }
        if (this is Window w)
            w.RequestedThemeVariant = variant;
        var theme = isLight
            ? MarkdownHighlightTheme.LightTheme
            : MarkdownHighlightTheme.DarkTheme;
        _editorController.SetHighlightTheme(theme);
        SetDiffPaneHighlightTheme(theme);
    }

    private void SetupDiffPaneEditors(MainViewModel vm)
    {
        if (DiffLeftEditor == null || DiffRightEditor == null) return;
        var theme = string.Equals(vm.Config.Ui.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? MarkdownHighlightTheme.LightTheme
            : MarkdownHighlightTheme.DarkTheme;
        if (_diffLeftColorizer != null)
            DiffLeftEditor.TextArea.TextView.LineTransformers.Remove(_diffLeftColorizer);
        if (_diffRightColorizer != null)
            DiffRightEditor.TextArea.TextView.LineTransformers.Remove(_diffRightColorizer);
        _diffLeftColorizer = new MarkdownColorizer(theme);
        _diffRightColorizer = new MarkdownColorizer(theme);
        DiffLeftEditor.TextArea.TextView.LineTransformers.Add(_diffLeftColorizer);
        DiffRightEditor.TextArea.TextView.LineTransformers.Add(_diffRightColorizer);
        DiffLeftEditor.FontSize = 14.0 * vm.EditorZoomLevel;
        DiffRightEditor.FontSize = 14.0 * vm.EditorZoomLevel;
    }

    private void SetDiffPaneHighlightTheme(MarkdownHighlightTheme theme)
    {
        if (DiffLeftEditor == null || DiffRightEditor == null) return;
        if (_diffLeftColorizer != null)
            DiffLeftEditor.TextArea.TextView.LineTransformers.Remove(_diffLeftColorizer);
        if (_diffRightColorizer != null)
            DiffRightEditor.TextArea.TextView.LineTransformers.Remove(_diffRightColorizer);
        _diffLeftColorizer = new MarkdownColorizer(theme);
        _diffRightColorizer = new MarkdownColorizer(theme);
        DiffLeftEditor.TextArea.TextView.LineTransformers.Add(_diffLeftColorizer);
        DiffRightEditor.TextArea.TextView.LineTransformers.Add(_diffRightColorizer);
        try
        {
            DiffLeftEditor.TextArea.TextView.Redraw();
            DiffRightEditor.TextArea.TextView.Redraw();
        }
        catch { }
    }

    private void RefreshDualDiffDisplay()
    {
        if (DataContext is not MainViewModel vm || !vm.IsDiffCompareActive || DiffLeftEditor == null || DiffRightEditor == null)
            return;
        var path = vm.DiffCompareFilePath;
        var sha = vm.DiffCompareCommitSha;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sha)) return;
        var repo = Services.GitService.FindRepositoryRootForPath(path);
        if (string.IsNullOrEmpty(repo)) return;
        var oldText = Services.GitService.GetFileContentAtCommit(repo, path, sha) ?? "";
        var newText = GetWorkingCopyTextForCompare(vm, path);
        var result = SideBySideDiffService.Build(oldText, newText);
        _diffLeftLineTypes = result.LeftLineTypes;
        _diffRightLineTypes = result.RightLineTypes;
        RemoveDiffPaneBackgroundRenderers();
        RemoveDualPaneSymbolMargins();
        DiffLeftEditor.Text = result.LeftText;
        DiffRightEditor.Text = result.RightText;
        var red = GetDiffBrushFromResources("DiffRemovedBackground");
        var green = GetDiffBrushFromResources("DiffAddedBackground");
        var gray = new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x80, 0x80));
        var minusSym = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60));
        var plusSym = new SolidColorBrush(Color.FromRgb(0x60, 0xD0, 0x60));
        _diffLeftBgRenderer = new DualPaneDiffBackgroundRenderer(() => _diffLeftLineTypes, true, red, gray);
        _diffRightBgRenderer = new DualPaneDiffBackgroundRenderer(() => _diffRightLineTypes, false, green, gray);
        DiffLeftEditor.TextArea.TextView.BackgroundRenderers.Add(_diffLeftBgRenderer);
        DiffRightEditor.TextArea.TextView.BackgroundRenderers.Add(_diffRightBgRenderer);
        _diffLeftSymbolMargin = new DualPaneDiffSymbolMargin(() => _diffLeftLineTypes, true, minusSym, plusSym);
        _diffRightSymbolMargin = new DualPaneDiffSymbolMargin(() => _diffRightLineTypes, false, minusSym, plusSym);
        _diffLeftSymbolMargin.AddToTextView(DiffLeftEditor.TextArea.TextView);
        _diffRightSymbolMargin.AddToTextView(DiffRightEditor.TextArea.TextView);
        DiffLeftEditor.TextArea.LeftMargins.Insert(0, _diffLeftSymbolMargin);
        DiffRightEditor.TextArea.LeftMargins.Insert(0, _diffRightSymbolMargin);
        try
        {
            DiffLeftEditor.TextArea.TextView.Redraw();
            DiffRightEditor.TextArea.TextView.Redraw();
        }
        catch { }
        AttachDiffScrollSync();
        AttachDiffTooltipHandlers();
    }

    private static string GetWorkingCopyTextForCompare(MainViewModel vm, string filePath)
    {
        if (string.Equals(vm.CurrentFilePath, filePath, StringComparison.OrdinalIgnoreCase))
            return vm.CurrentMarkdown ?? "";
        try
        {
            if (File.Exists(filePath))
            {
                var doc = vm.ActiveDocument;
                if (doc != null && string.Equals(doc.FullPath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var enc = Encoding.GetEncoding(doc.EncodingName);
                        return File.ReadAllText(filePath, enc);
                    }
                    catch
                    {
                        return File.ReadAllText(filePath, Encoding.UTF8);
                    }
                }
                return File.ReadAllText(filePath, Encoding.UTF8);
            }
        }
        catch
        {
            // 工作区无此文件（如已删除）则右侧为空
        }
        return "";
    }

    private void RemoveDiffPaneBackgroundRenderers()
    {
        if (DiffLeftEditor != null && _diffLeftBgRenderer != null)
        {
            DiffLeftEditor.TextArea.TextView.BackgroundRenderers.Remove(_diffLeftBgRenderer);
            _diffLeftBgRenderer = null;
        }
        if (DiffRightEditor != null && _diffRightBgRenderer != null)
        {
            DiffRightEditor.TextArea.TextView.BackgroundRenderers.Remove(_diffRightBgRenderer);
            _diffRightBgRenderer = null;
        }
    }

    private void RemoveDualPaneSymbolMargins()
    {
        if (DiffLeftEditor != null && _diffLeftSymbolMargin != null)
        {
            _diffLeftSymbolMargin.RemoveFromTextView(DiffLeftEditor.TextArea.TextView);
            DiffLeftEditor.TextArea.LeftMargins.Remove(_diffLeftSymbolMargin);
            _diffLeftSymbolMargin = null;
        }
        if (DiffRightEditor != null && _diffRightSymbolMargin != null)
        {
            _diffRightSymbolMargin.RemoveFromTextView(DiffRightEditor.TextArea.TextView);
            DiffRightEditor.TextArea.LeftMargins.Remove(_diffRightSymbolMargin);
            _diffRightSymbolMargin = null;
        }
    }

    private void AttachDiffScrollSync()
    {
        DetachDiffScrollSync();
        if (DiffLeftEditor == null || DiffRightEditor == null) return;
        _diffLeftScroll = DiffLeftEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        _diffRightScroll = DiffRightEditor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_diffLeftScroll != null)
            _diffLeftScroll.ScrollChanged += DiffLeftScroll_OnScrollChanged;
        if (_diffRightScroll != null)
            _diffRightScroll.ScrollChanged += DiffRightScroll_OnScrollChanged;
    }

    private void DetachDiffScrollSync()
    {
        DetachDiffTooltipHandlers();
        if (_diffLeftScroll != null)
            _diffLeftScroll.ScrollChanged -= DiffLeftScroll_OnScrollChanged;
        if (_diffRightScroll != null)
            _diffRightScroll.ScrollChanged -= DiffRightScroll_OnScrollChanged;
        _diffLeftScroll = null;
        _diffRightScroll = null;
    }

    private void DiffLeftScroll_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_diffSyncingScroll || _diffLeftScroll == null || _diffRightScroll == null) return;
        _diffSyncingScroll = true;
        try
        {
            _diffRightScroll.Offset = _diffLeftScroll.Offset;
        }
        finally
        {
            _diffSyncingScroll = false;
        }
    }

    private void DiffRightScroll_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_diffSyncingScroll || _diffLeftScroll == null || _diffRightScroll == null) return;
        _diffSyncingScroll = true;
        try
        {
            _diffLeftScroll.Offset = _diffRightScroll.Offset;
        }
        finally
        {
            _diffSyncingScroll = false;
        }
    }

    private void AttachDiffTooltipHandlers()
    {
        DetachDiffTooltipHandlers();
        if (DiffLeftEditor == null || DiffRightEditor == null) return;
        DiffLeftEditor.TextArea.PointerMoved += DiffLeftTextArea_PointerMoved;
        DiffLeftEditor.TextArea.PointerExited += DiffLeftTextArea_PointerExited;
        DiffRightEditor.TextArea.PointerMoved += DiffRightTextArea_PointerMoved;
        DiffRightEditor.TextArea.PointerExited += DiffRightTextArea_PointerExited;
    }

    private void DetachDiffTooltipHandlers()
    {
        if (DiffLeftEditor != null)
        {
            DiffLeftEditor.TextArea.PointerMoved -= DiffLeftTextArea_PointerMoved;
            DiffLeftEditor.TextArea.PointerExited -= DiffLeftTextArea_PointerExited;
            ToolTip.SetTip(DiffLeftEditor, null);
        }
        if (DiffRightEditor != null)
        {
            DiffRightEditor.TextArea.PointerMoved -= DiffRightTextArea_PointerMoved;
            DiffRightEditor.TextArea.PointerExited -= DiffRightTextArea_PointerExited;
            ToolTip.SetTip(DiffRightEditor, null);
        }
        _diffTooltipCachedLeft = null;
        _diffTooltipCachedRight = null;
    }

    private void DiffLeftTextArea_PointerMoved(object? sender, PointerEventArgs e) =>
        UpdateDiffPaneTooltip(DiffLeftEditor, ref _diffTooltipCachedLeft, isOldPane: true, e);

    private void DiffRightTextArea_PointerMoved(object? sender, PointerEventArgs e) =>
        UpdateDiffPaneTooltip(DiffRightEditor, ref _diffTooltipCachedRight, isOldPane: false, e);

    private void DiffLeftTextArea_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DiffLeftEditor != null)
        {
            _diffTooltipCachedLeft = null;
            ToolTip.SetTip(DiffLeftEditor, null);
        }
    }

    private void DiffRightTextArea_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DiffRightEditor != null)
        {
            _diffTooltipCachedRight = null;
            ToolTip.SetTip(DiffRightEditor, null);
        }
    }

    private void UpdateDiffPaneTooltip(TextEditor? editor, ref string? cachedTip, bool isOldPane, PointerEventArgs e)
    {
        if (editor == null) return;
        if (DataContext is not MainViewModel vm || !vm.IsDiffCompareActive)
        {
            if (cachedTip != null)
            {
                cachedTip = null;
                ToolTip.SetTip(editor, null);
            }
            return;
        }
        var kinds = isOldPane ? _diffLeftLineTypes : _diffRightLineTypes;
        if (kinds == null || kinds.Count == 0)
        {
            if (cachedTip != null)
            {
                cachedTip = null;
                ToolTip.SetTip(editor, null);
            }
            return;
        }
        var textView = editor.TextArea.TextView;
        try
        {
            if (!textView.VisualLinesValid) return;
            var pos = e.GetPosition(textView) + textView.ScrollOffset;
            var vl = textView.GetVisualLineFromVisualTop(pos.Y);
            if (vl == null)
            {
                if (cachedTip != null)
                {
                    cachedTip = null;
                    ToolTip.SetTip(editor, null);
                }
                return;
            }
            var lineNum = vl.FirstDocumentLine?.LineNumber ?? 0;
            if (lineNum <= 0 || lineNum > kinds.Count)
            {
                if (cachedTip != null)
                {
                    cachedTip = null;
                    ToolTip.SetTip(editor, null);
                }
                return;
            }
            var t = kinds[lineNum - 1];
            var tip = FormatDiffLineTooltip(t, isOldPane);
            if (tip == cachedTip) return;
            cachedTip = tip;
            ToolTip.SetTip(editor, tip);
        }
        catch (VisualLinesInvalidException)
        {
            if (cachedTip != null)
            {
                cachedTip = null;
                ToolTip.SetTip(editor, null);
            }
        }
    }

    private static string? FormatDiffLineTooltip(ChangeType t, bool isOldPane)
    {
        if (isOldPane)
        {
            return t switch
            {
                ChangeType.Deleted => "删除行",
                ChangeType.Modified => "修改行",
                ChangeType.Imaginary => "对齐占位（本侧无对应行）",
                _ => null
            };
        }
        return t switch
        {
            ChangeType.Inserted => "新增行",
            ChangeType.Modified => "修改行",
            ChangeType.Imaginary => "对齐占位（本侧无对应行）",
            _ => null
        };
    }

    private void ClearDualDiffEditors()
    {
        DetachDiffScrollSync();
        RemoveDiffPaneBackgroundRenderers();
        RemoveDualPaneSymbolMargins();
        _diffLeftLineTypes = null;
        _diffRightLineTypes = null;
        if (DiffLeftEditor != null) DiffLeftEditor.Text = "";
        if (DiffRightEditor != null) DiffRightEditor.Text = "";
    }

    private void OnThemeChanged()
    {
        if (DataContext is MainViewModel vm)
        {
            ApplyThemeFromConfig(vm);
            vm.Config.Save(Core.AppConfig.DefaultConfigPath);
        }
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm)
            return;

        if (e.PropertyName == nameof(MainViewModel.LayoutMode))
        {
            ApplyLayout(vm.LayoutMode);
            SyncLayoutModeButton(vm);
        }
        else if (e.PropertyName is nameof(MainViewModel.IsExplorerActive)
                 or nameof(MainViewModel.IsSearchActive)
                 or nameof(MainViewModel.IsGitActive)
                 or nameof(MainViewModel.IsSettingsActive))
        {
            UpdateActivityBarHighlight(vm);
            if (e.PropertyName == nameof(MainViewModel.IsGitActive) && vm.IsGitActive)
                vm.GitPaneViewModel.RefreshStatus();
            if (e.PropertyName == nameof(MainViewModel.IsExplorerActive) && vm.IsExplorerActive)
                vm.RefreshExplorerFromDisk();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsSearching))
        {
            // 搜索状态由 ViewModel 的 SearchResultStatusText（x 个结果 + 循环小点）与定时器处理
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowEditorPaneForCurrentDoc))
        {
            ApplyLayout(vm.ShowEditorPaneForCurrentDoc ? vm.LayoutMode : EditorLayoutMode.PreviewOnly);
        }
        else if (e.PropertyName == nameof(MainViewModel.EditorZoomLevel))
        {
            if (EditorTextBox is TextEditor ed)
                ed.FontSize = 14.0 * vm.EditorZoomLevel;
            if (DiffLeftEditor != null) DiffLeftEditor.FontSize = 14.0 * vm.EditorZoomLevel;
            if (DiffRightEditor != null) DiffRightEditor.FontSize = 14.0 * vm.EditorZoomLevel;
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedPreset) && PreviewEngine != null)
        {
            // 渲染区主题（Markdown 样式预设）切换后，立即将最新样式应用到预览控件
            PreviewEngine.StyleConfig = vm.Config.Markdown;
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentFilePath))
        {
            vm.GitPaneViewModel.TimelineFilePath = vm.CurrentFilePath;
        }
        else if (e.PropertyName == nameof(MainViewModel.IsDiffCompareActive))
        {
            if (vm.IsDiffCompareActive)
                Dispatcher.UIThread.Post(() => RefreshDualDiffDisplay(), DispatcherPriority.Loaded);
            else
            {
                ClearDualDiffEditors();
                _editorController.SetDiffMode(null);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentMarkdown) && vm.IsDiffCompareActive
                 && !string.IsNullOrEmpty(vm.DiffCompareFilePath)
                 && string.Equals(vm.DiffCompareFilePath, vm.CurrentFilePath, StringComparison.OrdinalIgnoreCase))
            Dispatcher.UIThread.Post(RefreshDualDiffDisplay, DispatcherPriority.Background);
    }

    private void UpdateActivityBarHighlight(MainViewModel vm)
    {
        ActivityExplorerButton.Classes.Set("active", vm.IsExplorerActive);
        ActivitySearchButton.Classes.Set("active", vm.IsSearchActive);
        ActivityGitButton.Classes.Set("active", vm.IsGitActive);
        ActivitySettingsButton.Classes.Set("active", vm.IsSettingsActive);
    }


    private void SetupEditorContextMenuAndKeys(MainViewModel vm)
    {
        if (EditorTextBox is not TextEditor editor)
            return;

        EditorContextUndo.Click += (_, _) => EditorUndo();
        EditorContextRedo.Click += (_, _) => EditorRedo();
        EditorContextCut.Click += (_, _) => EditorCut();
        EditorContextCopy.Click += (_, _) => EditorCopy();
        EditorContextPaste.Click += (_, _) => _ = EditorPasteExAsync();
        EditorContextSelectAll.Click += (_, _) => EditorSelectAll();
        EditorContextInsertLink.Click += async (_, _) => await EditorInsertMarkdownResourceAsync(vm, isImage: false);
        EditorContextInsertImage.Click += async (_, _) => await EditorInsertMarkdownResourceAsync(vm, isImage: true);
        EditorContextExitCompare.Click += (_, _) =>
        {
            if (DataContext is MainViewModel m)
                m.ExitCompareWithCommit();
        };

        EditorContextLayoutBoth.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.Both);
        EditorContextLayoutEditorOnly.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.EditorOnly);
        EditorContextLayoutPreviewOnly.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.PreviewOnly);

        if (EditorTextBox.ContextFlyout is MenuFlyout editorFlyout)
        {
            editorFlyout.Opening += (_, _) =>
            {
                var seg = editor.TextArea.Selection?.SurroundingSegment;
                var hasSelection = seg is { Length: > 0 };
                EditorContextCut.IsEnabled = hasSelection;
                EditorContextCopy.IsEnabled = hasSelection;
                EditorContextUndo.IsEnabled = editor.Document?.UndoStack?.CanUndo ?? false;
                EditorContextRedo.IsEnabled = editor.Document?.UndoStack?.CanRedo ?? false;
                EditorContextExitCompare.IsVisible = vm.IsDiffCompareActive;
                _ = UpdateEditorPasteEnabledAsync();
            };
        }

        async System.Threading.Tasks.Task UpdateEditorPasteEnabledAsync()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var canPaste = false;
            if (clipboard != null)
            {
                try
                {
                    var text = await clipboard.TryGetTextAsync();
                    if (!string.IsNullOrEmpty(text)) { canPaste = true; goto done; }
                    if (await ClipboardPasteHelper.TryGetFileOrImageAsync(clipboard) != null)
                        canPaste = true;
                }
                catch { }
            }
        done:
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                EditorContextPaste.IsEnabled = canPaste;
            });
        }

        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Up, KeyModifiers.Alt),
            Command = new RelayCommand(() =>
            {
                _suppressNextAltKeyUp = true;
                EditorMoveLineUp();
            })
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Down, KeyModifiers.Alt),
            Command = new RelayCommand(() =>
            {
                _suppressNextAltKeyUp = true;
                EditorMoveLineDown();
            })
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Left, KeyModifiers.Alt),
            Command = new RelayCommand(() =>
            {
                _suppressNextAltKeyUp = true;
                DoGoBack(vm);
            })
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Right, KeyModifiers.Alt),
            Command = new RelayCommand(() =>
            {
                _suppressNextAltKeyUp = true;
                DoGoForward(vm);
            })
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.D, KeyModifiers.Control),
            Command = new RelayCommand(EditorDuplicateSelectionOrLine)
        });
        // Ctrl+F / Ctrl+H 打开编辑区查找面板并聚焦搜索框；跨文件搜索通过侧栏“搜索”按钮打开
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new RelayCommand(() => FocusEditorFind(vm)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.H, KeyModifiers.Control), Command = new RelayCommand(() => FocusEditorFind(vm)) });
        // Ctrl+/- 前先按当前键盘焦点同步激活窗格，避免编辑区内层控件获焦时 GotFocus 未冒泡导致始终缩放到预览区
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Add, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomInCommand.Execute(null); }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemPlus, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomInCommand.Execute(null); }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Subtract, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomOutCommand.Execute(null); }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemMinus, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomOutCommand.Execute(null); }) });
    }

    private void SetupPreviewContextMenu()
    {
        PreviewContextLayoutBoth.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.Both);
        PreviewContextLayoutEditorOnly.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.EditorOnly);
        PreviewContextLayoutPreviewOnly.Click += (_, _) => ApplyLayoutModeFromMenu(EditorLayoutMode.PreviewOnly);
        PreviewContextCopy.Click += async (_, _) =>
        {
            if (PreviewEngine != null)
                await PreviewEngine.TryCopySelectionAsync();
        };

        if (PreviewPaneGrid?.ContextFlyout is MenuFlyout previewFlyout)
        {
            previewFlyout.Opening += (_, _) =>
            {
                PreviewContextCopy.IsEnabled = PreviewEngine?.HasPreviewTextSelection ?? false;
            };
        }
    }

    private void SyncLayoutModeButton(MainViewModel vm)
    {
        if (LayoutModeButton == null)
            return;
        LayoutModeButton.Content = vm.LayoutMode switch
        {
            EditorLayoutMode.EditorOnly => "仅编辑",
            EditorLayoutMode.PreviewOnly => "仅预览",
            _ => "编辑+预览"
        };
    }

    private void ApplyLayoutModeFromMenu(EditorLayoutMode mode)
    {
        if (DataContext is not MainViewModel vm)
            return;
        vm.LayoutMode = mode;
        if (mode == EditorLayoutMode.Both)
            PreviewEngine?.RenderControl.ResetEngine();
    }

    private void OnWindowGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Source is not Visual focused) return;
        var ancestors = focused.GetVisualAncestors();
        if (EditorPaneGrid != null && ancestors.Contains(EditorPaneGrid))
        {
            vm.ActivePane = "Editor";
            return;
        }
        if (PreviewPaneGrid != null && ancestors.Contains(PreviewPaneGrid))
            vm.ActivePane = "Preview";
    }

    /// <summary>根据当前键盘焦点所在控件同步 ActivePane，使 Ctrl+/- 缩放到正确的窗格（编辑区内层获焦时 GotFocus 可能不冒泡到 EditorTextBox）。</summary>
    private void OnEditorPanePointerWheelZoom(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        if (DataContext is not MainViewModel vm)
            return;
        vm.ActivePane = "Editor";
        if (e.Delta.Y > 0)
            vm.ZoomInCommand.Execute(null);
        else if (e.Delta.Y < 0)
            vm.ZoomOutCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }

    private void SyncActivePaneFromFocus(MainViewModel vm)
    {
        var focused = FocusManager?.GetFocusedElement() as Visual;
        if (focused == null) return;
        var ancestors = focused.GetVisualAncestors();
        if (EditorPaneGrid != null && ancestors.Contains(EditorPaneGrid))
        {
            vm.ActivePane = "Editor";
            return;
        }
        if (PreviewPaneGrid != null && ancestors.Contains(PreviewPaneGrid))
            vm.ActivePane = "Preview";
    }

    /// <summary>聚焦编辑区并打开内置查找面板（Ctrl+F 文件内查找，与侧栏跨文件搜索分离）。</summary>
    private void FocusEditorFind(MainViewModel vm) => _editorController.FocusFind();

    /// <summary>在 Tunnel 阶段捕获 Ctrl+S、F2 文件树重命名等。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // F2：文件树选中节点时进入重命名，焦点会由 TreeItemRenameTextBox_OnLoaded 移到重命名框
        if (e.Key == Key.F2 && FileTreeList != null && e.Source is Visual sourceVisual)
        {
            var inFileTree = sourceVisual == FileTreeList || sourceVisual.GetVisualAncestors().Contains(FileTreeList);
            if (inFileTree && vm.SelectedTreeNode is FileTreeNode node && !node.IsRenaming)
            {
                node.EditName = node.DisplayName;
                node.IsRenaming = true;
                e.Handled = true;
                return;
            }
        }

        // 重命名框中按 Enter 时提交（兜底：Tunnel 阶段在 ListBox 之前处理，确保提交生效）
        if (e.Key == Key.Enter && !e.Handled && e.Source is TextBox renameBox && renameBox.DataContext is FileTreeNode renamingNode && renamingNode.IsRenaming)
        {
            CommitTreeItemRename(renameBox);
            e.Handled = true;
            return;
        }

        // Ctrl+C：预览区焦点常在 ScrollViewer 上，键盘到不了 EngineRenderControl，在此统一复制选区
        if (e.Key == Key.C && (e.KeyModifiers & KeyModifiers.Control) != 0 && PreviewEngine != null && PreviewPaneGrid != null)
        {
            var focused = FocusManager?.GetFocusedElement() as Visual;
            bool inPreviewPane =
                focused != null
                && (focused == PreviewPaneGrid || focused.GetVisualAncestors().Contains(PreviewPaneGrid));
            if (inPreviewPane || vm.ActivePane == "Preview")
            {
                if (PreviewEngine.TryCopySelectionAsync().ConfigureAwait(false).GetAwaiter().GetResult())
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        // Ctrl+V 在编辑区时走统一粘贴（文件/图片/文本）
        if (e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Control) != 0 && EditorPaneGrid != null && e.Source is Visual v)
        {
            var inEditor = v == EditorPaneGrid || v.GetVisualAncestors().Contains(EditorPaneGrid);
            if (inEditor)
            {
                _ = EditorPasteExAsync();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.S && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 统一拦截 Alt 组合键后的“裸 Alt 抬起”事件，避免菜单栏被激活。
    /// 仅当之前标记了 _suppressNextAltKeyUp 时才生效，普通单独按 Alt 仍保持默认行为。
    /// </summary>
    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (
            !_suppressNextAltKeyUp
            || (e.Key != Key.LeftAlt && e.Key != Key.RightAlt)
        )
            return;

        _suppressNextAltKeyUp = false;
        e.Handled = true;
    }

    /// <summary>
    /// 监听 Alt+鼠标 操作（例如列选择 Alt+拖拽），一旦检测到 Alt+左键按下，
    /// 则标记在本次交互结束时需要拦截随后的 Alt KeyUp，防止菜单获得焦点。
    /// </summary>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Alt) == 0)
            return;

        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed)
        {
            _suppressNextAltKeyUp = true;
        }
    }

    /// <summary>点击不在“重命名 TextBox”上时提交重命名，避免焦点无法移出导致卡住（如新建后立即重命名触发 VisualLinesInvalidException）。</summary>
    private void OnClickOutsideRenameCommit(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedTreeNode?.IsRenaming != true)
            return;
        if (e.Source is not Visual source)
            return;
        // 若点击的是重命名框本身或在其内部，不提交
        foreach (var v in source.GetVisualAncestors())
        {
            if (v is Avalonia.Controls.TextBox tb && tb.DataContext is FileTreeNode node && node.IsRenaming)
                return;
        }
        if (source is Avalonia.Controls.TextBox box && box.DataContext is FileTreeNode n && n.IsRenaming)
            return;
        var (_, error) = vm.TryCommitTreeItemRename();
        if (!string.IsNullOrEmpty(error))
            _ = ShowRenameErrorDialogAsync(error);
    }

    private async System.Threading.Tasks.Task ShowRenameErrorDialogAsync(string message)
    {
        var dialog = new Window
        {
            Title = "重命名失败",
            Width = 360,
            MaxWidth = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon()
        };
        var close = new Button { Content = "确定" };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 328 },
                close
            }
        };
        await dialog.ShowDialog(this);
    }

    private void DoGoBack(MainViewModel vm)
    {
        var offset = EditorTextBox is TextEditor ed ? ed.TextArea.Caret.Offset : 0;
        var (path, newOffset) = vm.GoBack(vm.CurrentFilePath ?? "", offset);
        if (path != null)
            NavigateTo(path, newOffset);
    }

    private void DoGoForward(MainViewModel vm)
    {
        var offset = EditorTextBox is TextEditor ed ? ed.TextArea.Caret.Offset : 0;
        var (path, newOffset) = vm.GoForward(vm.CurrentFilePath ?? "", offset);
        if (path != null)
            NavigateTo(path, newOffset);
    }

    internal void NavigateTo(string path, int offset)
    {
        if (DataContext is not MainViewModel vm) return;
        _isProgrammaticNavigation = true;
        _editorController.SuppressNextHistoryRecord();
        vm.OpenDocument(path);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (EditorTextBox is TextEditor editor && editor.Document != null && DataContext is MainViewModel m)
                {
                    var o = Math.Clamp(offset, 0, editor.Document.TextLength);
                    editor.TextArea.Caret.Offset = o;
                    editor.TextArea.Caret.BringCaretToView();
                    if (m.ActiveDocument != null)
                        m.ActiveDocument.LastCaretOffset = o;
                }
            }
            catch { }
            finally { _isProgrammaticNavigation = false; }
        });
    }

    internal void NavigateToSearchResult(SearchResultItem result)
    {
        if (result == null || DataContext is not MainViewModel vm) return;
        if (result.FilePath == vm.CurrentFilePath && EditorTextBox is TextEditor editor && editor.Document != null)
        {
            // 当前已在同一文件内，直接跳转到对应行，同样视为程序化导航，跳过一次历史记录。
            _editorController.SuppressNextHistoryRecord();
            var lineNum = Math.Clamp(result.LineNumber, 1, editor.Document.LineCount);
            var line = editor.Document.GetLineByNumber(lineNum);
            editor.TextArea.Caret.Offset = line.Offset;
            editor.TextArea.Caret.BringCaretToView();
            return;
        }
        _isProgrammaticNavigation = true;
        _editorController.SuppressNextHistoryRecord();
        _editorController.RequestGoToLine(result.LineNumber);
        vm.OpenDocument(result.FilePath);
        Dispatcher.UIThread.Post(() => _isProgrammaticNavigation = false);
    }

    internal void PushFocusHistory(string path, int offset)
    {
        if (DataContext is MainViewModel vm)
            vm.RecordLocation(path, offset);
    }

    /// <summary>
    /// Ctrl+D：在无选区时复制当前行到下一行；有选区时复制选区并插入到其后方。
    /// 行复制行为与 VS / VS Code 接近：整行复制（含行尾换行），光标移动到新行的相同列。
    /// </summary>
    private void EditorDuplicateSelectionOrLine()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null)
            return;

        editor.Focus();
        var doc = editor.Document;
        var selection = editor.TextArea.Selection;

        if (selection != null && !selection.IsEmpty)
        {
            var seg = selection.SurroundingSegment;
            if (seg == null || seg.Length == 0)
                return;

            string selectedText = doc.GetText(seg.Offset, seg.Length);
            int insertOffset = seg.EndOffset;
            doc.Insert(insertOffset, selectedText);

            // 选中新复制出的区域，便于连续 Ctrl+D。
            editor.SelectionStart = insertOffset;
            editor.SelectionLength = selectedText.Length;
            editor.TextArea.Caret.Offset = insertOffset + selectedText.Length;
            return;
        }

        // 无选区：复制当前整行（含行尾换行）插入到下一行。
        int caretOffset = editor.TextArea.Caret.Offset;
        if (doc.LineCount == 0)
            return;

        var line = doc.GetLineByOffset(caretOffset);
        int lineStart = line.Offset;
        int lineEnd = line.LineNumber == doc.LineCount
            ? doc.TextLength
            : doc.GetLineByNumber(line.LineNumber + 1).Offset;
        int lineLength = lineEnd - lineStart;
        if (lineLength <= 0)
            return;

        string lineText = doc.GetText(lineStart, lineLength);
        doc.Insert(lineEnd, lineText);

        // 将光标移动到新插入行的对应列位置。
        int columnInLine = caretOffset - lineStart;
        int newLineStart = lineEnd;
        int newCaret = Math.Clamp(newLineStart + columnInLine, newLineStart, newLineStart + lineLength);
        editor.TextArea.Caret.Offset = newCaret;
    }

    private void EditorUndo()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document?.UndoStack == null) return;
        editor.Focus();
        if (editor.Document.UndoStack.CanUndo)
            editor.Document.UndoStack.Undo();
    }

    private void EditorRedo()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document?.UndoStack == null) return;
        editor.Focus();
        if (editor.Document.UndoStack.CanRedo)
            editor.Document.UndoStack.Redo();
    }

    private async void EditorCut()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        editor.Focus();
        var seg = editor.TextArea.Selection?.SurroundingSegment;
        if (seg == null || seg.Length == 0) return;
        var text = editor.Document.GetText(seg.Offset, seg.Length);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
        editor.Document.Replace(seg.Offset, seg.Length, "");
    }

    private async void EditorCopy()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        editor.Focus();
        var seg = editor.TextArea.Selection?.SurroundingSegment;
        if (seg == null || seg.Length == 0) return;
        var text = editor.Document.GetText(seg.Offset, seg.Length);
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>统一粘贴入口：文件→插入链接，剪贴板图片→另存为后插入图片，否则文本粘贴。</summary>
    private async System.Threading.Tasks.Task EditorPasteExAsync()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        if (DataContext is not MainViewModel vm) return;
        editor.Focus();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        var docPath = string.IsNullOrWhiteSpace(vm.CurrentFilePath) ? null : vm.CurrentFilePath;
        var seg = editor.TextArea.Selection?.SurroundingSegment;
        var offset = seg?.Offset ?? editor.TextArea.Caret.Offset;
        var length = seg?.Length ?? 0;

        // 1. 剪贴板为文件时插入链接 [文件名](path)；2. 剪贴板为图片时另存为后插入图片。需要 Avalonia 提供 TryGetDataAsync / TryGetFilesAsync / TryGetBitmapAsync（新剪贴板 API）。
        if (await ClipboardPasteHelper.TryGetFileOrImageAsync(clipboard) is { } fileOrImage)
        {
            if (fileOrImage.FirstFilePath is { } localPath)
            {
                var displayPath = InsertMarkdownResourceWindow.ToDisplayPath(docPath, localPath);
                var url = InsertMarkdownResourceWindow.EscapeMarkdownUrl(displayPath);
                var fileName = Path.GetFileName(localPath);
                var md = $"[{fileName}]({url})";
                editor.Document.Replace(offset, length, md);
                editor.TextArea.Caret.Offset = offset + md.Length;
                editor.TextArea.Caret.BringCaretToView();
                return;
            }
            if (fileOrImage.Bitmap != null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "保存图片",
                    DefaultExtension = "png",
                    SuggestedFileName = "image.png",
                    FileTypeChoices = new[] { new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] } }
                });
                if (file != null && file.TryGetLocalPath() is { } savePath)
                {
                    try
                    {
                        fileOrImage.Bitmap.Save(savePath);
                        var displayPath = InsertMarkdownResourceWindow.ToDisplayPath(docPath, savePath);
                        var url = InsertMarkdownResourceWindow.EscapeMarkdownUrl(displayPath);
                        var md = $"![图片]({url})";
                        editor.Document.Replace(offset, length, md);
                        editor.TextArea.Caret.Offset = offset + md.Length;
                        editor.TextArea.Caret.BringCaretToView();
                    }
                    catch
                    {
                        // 保存失败时不插入
                    }
                }
                return;
            }
        }

        // 3. 文本粘贴
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            editor.Document.Replace(offset, length, text);
        }
    }

    private async System.Threading.Tasks.Task EditorInsertMarkdownResourceAsync(MainViewModel vm, bool isImage)
    {
        var docPath = string.IsNullOrWhiteSpace(vm.CurrentFilePath) ? null : vm.CurrentFilePath;
        var md = await InsertMarkdownResourceWindow.ShowInsertAsync(this, isImage, docPath, StorageProvider);
        if (string.IsNullOrEmpty(md)) return;
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        var offset = editor.TextArea.Caret.Offset;
        editor.Document.Insert(offset, md);
        editor.TextArea.Caret.Offset = offset + md.Length;
        editor.TextArea.Caret.BringCaretToView();
        editor.Focus();
    }

    private void EditorSelectAll()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        // 使用异步调度，确保在上下文菜单关闭后再设置焦点和选区，避免被菜单抢焦点导致视觉上无选中效果。
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (EditorTextBox is TextEditor ed)
                {
                    ed.Focus();
                    ed.SelectAll();
                }
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    private void EditorMoveLineUp()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        editor.Focus();
        var doc = editor.Document;
        if (doc.LineCount < 2) return;
        var selection = editor.TextArea.Selection;
        var caretOffset = editor.TextArea.Caret.Offset;

        int selStart, selEnd;
        if (selection.IsEmpty)
        {
            selStart = selEnd = caretOffset;
        }
        else
        {
            var seg = selection.SurroundingSegment;
            selStart = seg.Offset;
            selEnd = seg.EndOffset;
        }

        if (selEnd > selStart)
            selEnd--;

        // 将选择范围映射到完整的行，确保多行选择/列选择时始终按整行移动。
        var firstLine = doc.GetLineByOffset(selStart);
        var lastLine = doc.GetLineByOffset(selEnd);
        if (firstLine.LineNumber <= 1) return;

        var prevLine = doc.GetLineByNumber(firstLine.LineNumber - 1);

        // 选中块：从第一行行首到最后一行的行结束（含行尾换行符），使用“下一行起始/文档长度”推算行尾，避免依赖外部扩展属性。
        int blockStart = firstLine.Offset;
        int blockEnd = lastLine.LineNumber == doc.LineCount
            ? doc.TextLength
            : doc.GetLineByNumber(lastLine.LineNumber + 1).Offset;

        // 被替换的整体范围：上一行开始到选中块结束
        int segmentStart = prevLine.Offset;
        int segmentEnd = blockEnd;
        int segmentLength = segmentEnd - segmentStart;

        int prevStart = prevLine.Offset;
        int prevEnd = firstLine.Offset; // 第一行起始即上一行（含换行）的结束
        var prevText = doc.GetText(prevStart, prevEnd - prevStart);
        var blockText = doc.GetText(blockStart, blockEnd - blockStart);

        var newText = blockText + prevText;

        // 防御性检查：若计算出的范围超出文档长度或长度为 0，则放弃本次移动。
        if (segmentStart < 0 || segmentLength <= 0 || segmentStart + segmentLength > doc.TextLength)
            return;
        if (string.IsNullOrEmpty(newText))
            return;

        doc.Replace(segmentStart, segmentLength, newText);

        int newBlockStart = segmentStart;
        if (selection.IsEmpty)
        {
            int caretRel = caretOffset - blockStart;
            caretRel = Math.Clamp(caretRel, 0, blockText.Length);
            editor.TextArea.Caret.Offset = newBlockStart + caretRel;
        }
        else
        {
            editor.Select(newBlockStart, blockText.Length);
        }
    }

    private void EditorMoveLineDown()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        editor.Focus();
        var doc = editor.Document;
        if (doc.LineCount < 2) return;
        var selection = editor.TextArea.Selection;
        var caretOffset = editor.TextArea.Caret.Offset;

        int selStart, selEnd;
        if (selection.IsEmpty)
        {
            selStart = selEnd = caretOffset;
        }
        else
        {
            var seg = selection.SurroundingSegment;
            selStart = seg.Offset;
            selEnd = seg.EndOffset;
        }

        if (selEnd > selStart)
            selEnd--;

        // 将选择范围映射到完整的行，确保多行选择/列选择时始终按整行移动。
        var firstLine = doc.GetLineByOffset(selStart);
        var lastLine = doc.GetLineByOffset(selEnd);
        if (lastLine.LineNumber >= doc.LineCount) return;

        var nextLine = doc.GetLineByNumber(lastLine.LineNumber + 1);

        int blockStart = firstLine.Offset;
        int blockEnd = lastLine.LineNumber == doc.LineCount
            ? doc.TextLength
            : doc.GetLineByNumber(lastLine.LineNumber + 1).Offset;

        int nextStart = nextLine.Offset;
        int nextEnd = nextLine.LineNumber == doc.LineCount
            ? doc.TextLength
            : doc.GetLineByNumber(nextLine.LineNumber + 1).Offset;

        int segmentStart = blockStart;
        int segmentEnd = nextEnd;
        int segmentLength = segmentEnd - segmentStart;

        var blockText = doc.GetText(blockStart, blockEnd - blockStart);
        var nextText = doc.GetText(nextStart, nextEnd - nextStart);

        var newText = nextText + blockText;

        // 防御性检查：若计算出的范围超出文档长度或长度为 0，则放弃本次移动。
        if (segmentStart < 0 || segmentLength <= 0 || segmentStart + segmentLength > doc.TextLength)
            return;
        if (string.IsNullOrEmpty(newText))
            return;

        doc.Replace(segmentStart, segmentLength, newText);

        int newBlockStart = segmentStart + nextText.Length;
        if (selection.IsEmpty)
        {
            int caretRel = caretOffset - blockStart;
            caretRel = Math.Clamp(caretRel, 0, blockText.Length);
            editor.TextArea.Caret.Offset = newBlockStart + caretRel;
        }
        else
        {
            editor.Select(newBlockStart, blockText.Length);
        }
    }

    private async System.Threading.Tasks.Task DoOpenFileAsync(MainViewModel vm)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown 与文本") { Patterns = ["*.md", "*.txt"] },
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("文本") { Patterns = ["*.txt"] },
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.svg"]
                }
            ]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            vm.OpenDocument(path);
    }

    private async System.Threading.Tasks.Task DoOpenFolderAsync(MainViewModel vm)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 Markdown 文档所在文件夹",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            vm.LoadFolder(path);
    }

    private async System.Threading.Tasks.Task DoAddFoldersAsync(MainViewModel vm)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "添加文件夹到工作区（可多选）",
            AllowMultiple = true
        });
        var paths = folders.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count > 0)
            vm.AddFoldersToWorkspace(paths);
    }

    private async System.Threading.Tasks.Task DoSaveWorkspaceAsync(MainViewModel vm)
    {
        if (!vm.HasWorkspaceOpen) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存工作区",
            DefaultExtension = "mdw",
            FileTypeChoices = new[] { new FilePickerFileType("工作区") { Patterns = ["*.mdw"] } }
        });
        if (file != null && file.TryGetLocalPath() is { } path)
        {
            vm.SaveWorkspaceToFile(path);
        }
    }

    private async System.Threading.Tasks.Task DoOpenWorkspaceAsync(MainViewModel vm)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开工作区",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("工作区") { Patterns = ["*.mdw"] } }
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            var list = vm.LoadWorkspaceFromFile(path);
            if (list != null && list.Count > 0)
                vm.CloseAllAndLoadWorkspace(list);
        }
    }

    private async Task<bool> DoSaveAsAsync(MainViewModel vm)
    {
        var options = new FilePickerSaveOptions
        {
            Title = "另存为",
            DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
        };
        if (string.IsNullOrEmpty(vm.CurrentFilePath))
            options.SuggestedFileName = "未命名.md";
        var file = await StorageProvider.SaveFilePickerAsync(options);
        if (file != null && file.TryGetLocalPath() is { } path)
        {
            vm.SaveToPath(path);
            return true;
        }

        return false;
    }

    private async System.Threading.Tasks.Task DoExportAsync(MainViewModel vm, string formatId)
    {
        if (vm.CurrentMarkdown == null)
        {
            var msg = new Window
            {
                Title = "导出",
                Width = 360,
                MaxWidth = 420,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = GetAppIcon()
            };
            var closeBtn = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            closeBtn.Click += (_, _) => msg.Close();
            msg.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { new TextBlock { Text = "当前无内容可导出。请先打开或编辑文档。", TextWrapping = TextWrapping.Wrap, MaxWidth = 328 }, closeBtn } };
            await msg.ShowDialog(this);
            return;
        }
        int? renderWidth = null;
        if (PreviewEngine?.RenderControl is { Bounds.Width: > 0 } rc)
        {
            var cfg = EngineConfig.FromStyle(vm.Config.Markdown) ?? new EngineConfig();
            renderWidth = (int)Math.Max(1, rc.Bounds.Width - cfg.ContentPaddingX * 2);
        }

        Window? exportProgressWindow = null;
        TextBlock? exportProgressText = null;
        ProgressBar? exportProgressBar = null;
        var exportProgress = new Progress<ExportProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (exportProgressWindow == null)
                {
                    exportProgressText = new TextBlock
                    {
                        Text = p.Message,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 360
                    };
                    exportProgressBar = new ProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Height = 22,
                        Margin = new Thickness(0, 4, 0, 0),
                        IsIndeterminate = !p.Percent.HasValue
                    };
                    if (p.Percent.HasValue)
                        exportProgressBar.Value = p.Percent.Value;

                    exportProgressWindow = new Window
                    {
                        Title = "导出",
                        Width = 400,
                        MinHeight = 120,
                        SizeToContent = SizeToContent.Height,
                        CanResize = false,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Icon = GetAppIcon(),
                        Content = new StackPanel
                        {
                            Margin = new Thickness(16),
                            Spacing = 8,
                            Children = { exportProgressText, exportProgressBar }
                        }
                    };
                    exportProgressWindow.Show(this);
                }
                else
                {
                    if (exportProgressText != null)
                        exportProgressText.Text = p.Message;
                    if (exportProgressBar != null)
                    {
                        if (p.Percent.HasValue)
                        {
                            exportProgressBar.IsIndeterminate = false;
                            exportProgressBar.Value = p.Percent.Value;
                        }
                    }
                }
            });
        });

        try
        {
            var (success, errorMessage) = await _exportService.ExportWithDialogAsync(vm, formatId, StorageProvider, renderWidth, exportProgress);
            if (success == false && !string.IsNullOrEmpty(errorMessage))
            {
                var msg = new Window
                {
                    Title = "导出失败",
                    Width = 380,
                    MaxWidth = 440,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Icon = GetAppIcon()
                };
                var okButton = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                okButton.Click += (_, _) => msg.Close();
                msg.Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 12,
                    Children = { new TextBlock { Text = errorMessage, TextWrapping = TextWrapping.Wrap, MaxWidth = 348 }, okButton }
                };
                await msg.ShowDialog(this);
            }
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                exportProgressWindow?.Close();
                exportProgressWindow = null;
            });
        }
    }

    /// <summary>自动保存：缺失文件确认、失败提示（必须在 UI 线程弹窗）。</summary>
    private void RegisterAutoSaveDialogs(MainViewModel vm)
    {
        vm.AutoSaveAskRecreateMissingFileAsync = async path =>
        {
            bool confirm = false;
            var dlg = new Window
            {
                Title = "自动保存",
                Width = 480,
                MaxWidth = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = GetAppIcon(),
            };
            var yes = new Button { Content = "重新创建并保存", Margin = new Thickness(0, 0, 8, 0) };
            yes.Click += (_, _) => { confirm = true; dlg.Close(); };
            var no = new Button { Content = "暂不保存" };
            no.Click += (_, _) => dlg.Close();
            var buttons = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Children = { yes, no },
            };
            dlg.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text =
                            $"磁盘上已找不到该文件（可能已被删除或移动）：\n\n{path}\n\n是否要在原路径重新创建文件并写入当前编辑内容？",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 448,
                    },
                    buttons,
                },
            };
            await dlg.ShowDialog(this);
            return confirm;
        };

        vm.AutoSaveShowFailureAsync = async message =>
        {
            var msg = new Window
            {
                Title = "自动保存失败",
                Width = 420,
                MaxWidth = 480,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = GetAppIcon(),
            };
            var ok = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            ok.Click += (_, _) => msg.Close();
            msg.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 388 }, ok },
            };
            await msg.ShowDialog(this);
        };
    }

    /// <summary>底部状态栏路径：在系统文件管理器中打开所在文件夹。</summary>
    private void OpenCurrentFileFolderInExplorer()
    {
        if (DataContext is not MainViewModel vm)
            return;
        var path = vm.CurrentFilePath?.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        string? dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "\"",
                        UseShellExecute = true,
                    }
                );
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", dir);
            }
            else
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = "\"" + dir + "\"",
                        UseShellExecute = true,
                    }
                );
            }
        }
        catch
        {
            /* 忽略 */
        }
    }

    private async void ShowAboutDialog()
    {
        const double textMaxW = 420;
        var about = new Window
        {
            Title = "关于",
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
            CanResize = true,
            MinWidth = 300,
            MinHeight = 140,
            Width = 460,
            MaxWidth = 520,
            MaxHeight = 560,
            SizeToContent = SizeToContent.Height,
        };

        static TextBlock Para(string text, FontWeight weight = FontWeight.Normal) =>
            new()
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = textMaxW,
                TextAlignment =TextAlignment.Justify,
                FontWeight = weight,
            };

        static TextBlock ParaLeft(string text, FontWeight weight = FontWeight.Normal) =>
            new()
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = textMaxW,
                TextAlignment = TextAlignment.Left,
                FontWeight = weight,
            };

        var ok = new Button
        {
            Content = "确定",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 80,
            Margin = new Thickness(0, 8, 0, 0),
        };
        ok.Click += (_, _) => about.Close();

        var panel = new StackPanel
        {
            Margin = new Thickness(20, 16, 20, 16),
            Spacing = 10,
            Children =
            {
                Para("Ver 1.0.0.4 @ 2026", FontWeight.SemiBold),
                Para(
                    "We built this to solve our own problems. Now we maintain it to solve yours. Free forever. Works offline. No accounts, no tracking, no expiration date. Just a reliable tool that grows with you."
                ),
                Para("Spc: LdotJdot, Herman Chen"),
                ParaLeft("开源组件与许可证", FontWeight.SemiBold),
                ParaLeft("以下为本软件使用的主要开源组件及其许可证信息："),
                ParaLeft("1) Avalonia UI 11.3.12 - MIT License"),
                ParaLeft("2) AvaloniaEdit 11.4.0 - MIT License"),
                ParaLeft("3) SkiaSharp 2.88.9 - MIT License"),
                ParaLeft("4) Skia (Google Skia, via SkiaSharp 2.88.9) - BSD 3-Clause License"),
                ParaLeft("5) LibGit2Sharp 0.31.0 - MIT License"),
                ParaLeft("6) libgit2 (via LibGit2Sharp.NativeBinaries 2.0.323) - GPLv2 with Linking Exception"),
                ParaLeft("7) DiffPlex 1.7.2 - Apache License 2.0"),
                ParaLeft("完整许可证文本与第三方声明以各组件官方发布内容为准。"),
                ok,
            },
        };

        about.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            MaxHeight = 480,
            Padding = new Thickness(0),
            Content = panel,
        };

        await about.ShowDialog(this);
    }

    private async System.Threading.Tasks.Task ConfirmCloseCurrentEditorAsync(MainViewModel vm)
    {
        if (!vm.IsModified || vm.ActiveDocument == null)
        {
            vm.CloseDocument(vm.ActiveDocument);
            return;
        }
        var dialog = new ConfirmCloseWindow();
        dialog.SetDocumentPath(vm.CurrentFilePath ?? "");
        await dialog.ShowDialog(this);
        if (dialog.Result == ConfirmCloseResult.Save)
        {
            if (string.IsNullOrEmpty(vm.CurrentFilePath))
            {
                if (!await DoSaveAsAsync(vm))
                    return;
            }
            else
            {
                vm.SaveCurrent();
                vm.CloseDocument(vm.ActiveDocument);
            }
        }
        else if (dialog.Result == ConfirmCloseResult.Discard)
            vm.CloseDocument(vm.ActiveDocument);
    }

    private async System.Threading.Tasks.Task ConfirmExitAsync(MainViewModel vm)
    {
        // 逐个检查打开的文档是否有未保存修改，对每个文档依次弹出确认对话框。
        foreach (var doc in vm.OpenDocuments.ToList())
        {
            if (!doc.IsModified)
                continue;

            vm.ActiveDocument = doc;
            vm.OpenDocument(doc.FullPath);

            var dialog = new ConfirmCloseWindow();
            dialog.SetDocumentPath(doc.FullPath);
            await dialog.ShowDialog(this);

            if (dialog.Result == ConfirmCloseResult.Cancel)
                return;

            if (dialog.Result == ConfirmCloseResult.Save)
            {
                if (string.IsNullOrEmpty(doc.FullPath))
                {
                    if (!await DoSaveAsAsync(vm))
                        return;
                }
                else
                    vm.SaveCurrent();
            }
        }

        _isClosingProgrammatically = true;
        Close();
    }

    private void SetupSearchResultsNavigation(MainViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedSearchResult) || vm.SelectedSearchResult == null)
                return;
            if (EditorTextBox is TextEditor ed)
                vm.RecordLocation(vm.CurrentFilePath ?? "", ed.TextArea.Caret.Offset);
            NavigateToSearchResult(vm.SelectedSearchResult);
        };
    }

    private void SetupGitPane(MainViewModel vm)
    {
        var gitVm = vm.GitPaneViewModel;
        gitVm.CredentialsProvider = (url, usernameFromUrl) =>
            Dispatcher.UIThread.Invoke(() => ShowGitCredentialsDialog(this, url, usernameFromUrl));
        gitVm.CreateBranchRequested += async (_, repoPath) =>
        {
            if (DataContext is MainViewModel m)
                await HandleCreateBranchRequestedAsync(m, gitVm, repoPath);
        };
        if (GitChangesListBox != null)
            GitChangesListBox.SelectionChanged += GitChangesListBox_OnSelectionChanged;
        if (GitHistoryCommitsListBox != null)
            GitHistoryCommitsListBox.SelectionChanged += GitHistoryCommitsListBox_OnSelectionChanged;
        if (GitCommitChangedFilesListBox != null)
            GitCommitChangedFilesListBox.SelectionChanged += GitCommitChangedFilesListBox_OnSelectionChanged;
        gitVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitPaneViewModel.GitPaneTabIndex))
                gitVm.RefreshStatus();
        };
        gitVm.CommitMessageEmptyRequested += (_, _) => _ = ShowCommitMessageRequiredDialogAsync();
        gitVm.CompareFileWithWorkingRequested += (_, t) =>
        {
            var (fullPath, sha) = t;
            vm.EnterCompareWithCommit(fullPath, sha);
            Dispatcher.UIThread.Post(RefreshDualDiffDisplay, DispatcherPriority.Loaded);
        };
        gitVm.HardResetToCommitRequested += async (_, sha) => await ConfirmAndHardResetAsync(vm, sha);
        gitVm.RevertCommitRequested += async (_, sha) => await ConfirmAndRevertAsync(vm, sha);
    }

    private enum CreateBranchDirtyChoice { CarryWorkingTree, DiscardWorkingTree }

    private async Task HandleCreateBranchRequestedAsync(MainViewModel vm, GitPaneViewModel gitVm, string repoPath)
    {
        CreateBranchDirtyChoice? dirtyChoice = null;
        if (gitVm.Changes.Count > 0)
        {
            dirtyChoice = await ShowCreateBranchDirtyChoiceDialogAsync();
            if (dirtyChoice == null) return;
        }

        if (dirtyChoice == CreateBranchDirtyChoice.DiscardWorkingTree)
        {
            var (okReset, errReset) = GitService.ResetWorkingTreeToHead(repoPath);
            if (!okReset)
            {
                gitVm.StatusMessage = errReset;
                await ShowSimpleAlertAsync("无法继续", errReset ?? "丢弃本地修改失败。");
                return;
            }

            gitVm.RefreshStatus();
        }

        var win = new GitCreateBranchWindow();
        await win.ShowDialog(this);
        var name = win.BranchName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var (success, error) = GitService.CreateBranchAndCheckout(repoPath, name);
        if (success)
        {
            gitVm.RefreshStatus();
            vm.RefreshExplorerFromDisk();
        }
        else
            gitVm.StatusMessage = error;
    }

    private async Task<CreateBranchDirtyChoice?> ShowCreateBranchDirtyChoiceDialogAsync()
    {
        var dlg = new Window
        {
            Title = "新建分支",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        CreateBranchDirtyChoice? picked = null;
        var carry = new Button { Content = "带入新分支", Margin = new Thickness(0, 0, 6, 0), MinWidth = 108 };
        var discard = new Button { Content = "丢弃后新建", Margin = new Thickness(0, 0, 6, 0), MinWidth = 88 };
        var cancel = new Button { Content = "取消" };
        carry.Click += (_, _) => { picked = CreateBranchDirtyChoice.CarryWorkingTree; dlg.Close(); };
        discard.Click += (_, _) => { picked = CreateBranchDirtyChoice.DiscardWorkingTree; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text =
                        "当前工作区有未提交的修改。请选择：\n\n" +
                        "· 带入新分支：在新分支上保留这些修改（与由当前 HEAD 检出新建分支一致）。\n" +
                        "· 丢弃后新建：先丢弃全部未提交修改，再创建并切换到新分支。\n" +
                        "· 取消：不创建分支。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 388,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { carry, discard, cancel },
                },
            },
        };
        await dlg.ShowDialog(this);
        return picked;
    }

    private async Task ConfirmAndDeleteDotGitAsync(MainViewModel vm)
    {
        var repo = vm.GitPaneViewModel.SelectedRepository?.WorkingDirectory;
        if (string.IsNullOrEmpty(repo) || !vm.GitPaneViewModel.SelectedRepositoryIsInitialized) return;
        var dlg = new Window
        {
            Title = "删除仓库确认",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        var confirm = false;
        var yes = new Button { Content = "确定删除 .git", Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => { confirm = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text =
                        "此操作等价于删除仓库根目录下的“.git”文件夹（或 gitdir 链接文件）：Git 的版本记录、分支、远程等元数据会一并移除。\n\n" +
                        "工作区里的普通文档文件不会被删除；删除后本目录不再视为 Git 仓库。若无其它备份，提交历史通常无法从本应用恢复。\n\n" +
                        "确定要继续吗？",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };
        await dlg.ShowDialog(this);
        if (!confirm) return;
        try
        {
            var (ok, err) = GitService.DeleteDotGit(repo);
            if (ok)
            {
                vm.GitPaneViewModel.RefreshRepositories();
                vm.RefreshExplorerFromDisk();
                vm.GitPaneViewModel.StatusMessage = "已移除 .git；可随时使用「初始化 Git 仓库」重新创建。";
            }
            else
                await ShowSimpleAlertAsync("删除失败", err ?? "操作未成功。");
        }
        catch (Exception ex)
        {
            await ShowSimpleAlertAsync("删除失败", ex.Message);
        }
    }

    private async void GitBranchRowDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: GitBranchListItem item })
            return;
        if (item.IsCurrent) return;
        if (GitPaneBranchCombo != null)
            GitPaneBranchCombo.IsDropDownOpen = false;
        if (DataContext is not MainViewModel vm) return;
        await ConfirmAndDeleteLocalBranchAsync(vm, item.Name);
    }

    private async Task ConfirmAndDeleteLocalBranchAsync(MainViewModel vm, string branchName)
    {
        var dlg = new Window
        {
            Title = "删除分支",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        var confirm = false;
        var yes = new Button { Content = "删除", Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => { confirm = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text =
                        $"确定要删除本地分支「{branchName}」吗？\n\n" +
                        "此操作仅移除该分支的本地指针；若其上的提交未被其它分支引用，可能变为不可达。未合并的分支可能被 Git 拒绝删除。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };
        await dlg.ShowDialog(this);
        if (!confirm) return;
        var repo = vm.GitPaneViewModel.SelectedRepository?.WorkingDirectory;
        if (string.IsNullOrEmpty(repo)) return;
        try
        {
            var (ok, err) = GitService.DeleteLocalBranch(repo, branchName);
            if (ok)
            {
                vm.GitPaneViewModel.RefreshStatus();
                vm.RefreshExplorerFromDisk();
                vm.GitPaneViewModel.StatusMessage = $"已删除本地分支「{branchName}」。";
            }
            else
                await ShowSimpleAlertAsync("删除分支失败", err ?? "操作未成功。");
        }
        catch (Exception ex)
        {
            await ShowSimpleAlertAsync("删除分支失败", ex.Message);
        }
    }

    private async void GitPaneDeleteRepositoryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        await ConfirmAndDeleteDotGitAsync(vm);
    }

    private async System.Threading.Tasks.Task ConfirmAndHardResetAsync(MainViewModel vm, string commitSha)
    {
        var dlg = new Window
        {
            Title = "硬重置确认",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        bool confirm = false;
        var yes = new Button { Content = "确定重置", Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => { confirm = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"将丢弃所有未提交的修改，并把分支指向提交 {commitSha.Substring(0, Math.Min(7, commitSha.Length))}，其后的本地提交记录将不再可见。\n\n若已推送过远程，需要自行 git push --force。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 388,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };
        await dlg.ShowDialog(this);
        if (!confirm) return;
        var repo = vm.GitPaneViewModel.SelectedRepository?.WorkingDirectory;
        if (string.IsNullOrEmpty(repo)) return;
        try
        {
            var (ok, err) = Services.GitService.ResetHard(repo, commitSha);
            if (ok)
            {
                vm.GitPaneViewModel.RefreshStatus();
                vm.RefreshExplorerFromDisk();
                vm.GitPaneViewModel.StatusMessage = "已硬重置，请重新加载打开的文档。";
                if (!string.IsNullOrEmpty(vm.CurrentFilePath))
                    vm.OpenDocument(vm.CurrentFilePath);
            }
            else
                await ShowSimpleAlertAsync("硬重置失败", err ?? "操作未成功。");
        }
        catch (Exception ex)
        {
            await ShowSimpleAlertAsync("硬重置失败", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task ConfirmAndRevertAsync(MainViewModel vm, string commitSha)
    {
        var repo = vm.GitPaneViewModel.SelectedRepository?.WorkingDirectory;
        if (string.IsNullOrEmpty(repo)) return;
        var shortSha = commitSha.Length >= 7 ? commitSha[..7] : commitSha;
        var dlg = new Window
        {
            Title = "Revert 确认",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        var confirm = false;
        var yes = new Button { Content = "确定 Revert", Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "取消" };
        yes.Click += (_, _) => { confirm = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text =
                        $"将针对提交 {shortSha} 创建一次新的「撤销」提交（git revert），不会抹掉已有历史记录。\n\n若工作区存在未提交修改，Revert 在合并时可能失败（例如提示未提交更改将被覆盖）；请先提交、stash 暂存或处理完本地修改后再试。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 388,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };
        await dlg.ShowDialog(this);
        if (!confirm) return;

        try
        {
            var (ok, err) = Services.GitService.RevertCommit(repo, commitSha);
            if (ok)
            {
                vm.GitPaneViewModel.RefreshStatus();
                vm.RefreshExplorerFromDisk();
                vm.GitPaneViewModel.StatusMessage = "已创建撤销提交。";
            }
            else
                await ShowSimpleAlertAsync("Revert 失败", FormatGitRevertFailureMessage(err));
        }
        catch (Exception ex)
        {
            await ShowSimpleAlertAsync("Revert 失败", FormatGitRevertFailureMessage(ex.Message));
        }
    }

    /// <summary>将 LibGit2Sharp / Git 典型英文错误转为可读说明（含未提交被 merge 覆盖）。</summary>
    private static string FormatGitRevertFailureMessage(string? err)
    {
        if (string.IsNullOrWhiteSpace(err))
            return "操作未成功。";
        var e = err.Trim();
        if (e.Contains("uncommitted", StringComparison.OrdinalIgnoreCase) &&
            (e.Contains("overwritten", StringComparison.OrdinalIgnoreCase) ||
             e.Contains("overwrite", StringComparison.OrdinalIgnoreCase) ||
             e.Contains("merge", StringComparison.OrdinalIgnoreCase)))
        {
            return "当前工作区有未提交修改，执行 Revert 时合并会覆盖这些修改，Git 已中止。\n\n请先提交、使用 stash 暂存，或处理完本地修改后再试。\n\n—— 原始信息 ——\n" + e;
        }

        return e;
    }

    private async System.Threading.Tasks.Task ShowSimpleAlertAsync(string title, string message)
    {
        var w = new Window
        {
            Title = title,
            Width = 400,
            MaxWidth = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = GetAppIcon(),
        };
        var okBtn = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        okBtn.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
                okBtn,
            },
        };
        await w.ShowDialog(this);
    }

    private System.Threading.Tasks.Task ShowCommitMessageRequiredDialogAsync() =>
        ShowSimpleAlertAsync("无法提交", "请填写提交说明（Commit message）后再提交。");

    private void GitCommitMessageTextBox_OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.GitPaneViewModel.SelectedRepositoryIsInitialized)
            vm.GitPaneViewModel.RefreshStatus();
    }

    private void GitHistoryCommitsListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.AddedItems?.Count > 0 && e.AddedItems[0] is Models.GitCommitItem added)
        {
            vm.GitPaneViewModel.SelectedBranchCommit = added;
            return;
        }

        if (e.AddedItems is { Count: 0 } && e.RemovedItems is { Count: > 0 })
            vm.GitPaneViewModel.SelectedBranchCommit = null;
    }

    private void GitCommitChangedFilesListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || e.AddedItems?.Count is not 1) return;
        if (vm.GitPaneViewModel.SuppressCommitFileCompareSelection) return;
        if (e.AddedItems[0] is not Models.GitCommitChangedFileItem item) return;
        vm.GitPaneViewModel.RequestCompareFileWithWorking(item);
    }

    private void GitChangesListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || e.AddedItems?.Count is not 1) return;
        if (e.AddedItems[0] is not Models.GitChangeItem item || string.IsNullOrEmpty(item.FullPath)) return;
        var path = item.FullPath;
        vm.EnterCompareWithCommit(path, "HEAD");
        var retryCount = 0;
        const int maxRetries = 3;
        void TryRefresh()
        {
            var text = vm.CurrentMarkdown ?? "";
            if (string.IsNullOrEmpty(text) && retryCount < maxRetries)
            {
                retryCount++;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150 * retryCount) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    TryRefresh();
                };
                timer.Start();
                return;
            }
            RefreshDualDiffDisplay();
        }
        Dispatcher.UIThread.Post(TryRefresh, DispatcherPriority.Loaded);
    }

    private static (string? username, string? password)? ShowGitCredentialsDialog(Window owner, string? url, string? usernameFromUrl)
    {
        return Dispatcher.UIThread.Invoke<(string? username, string? password)?>(() =>
        {
            var win = new GitCredentialsWindow();
            win.SetMessage(string.IsNullOrEmpty(url) ? "请输入用户名和密码（或 Token）。" : $"认证：{url}");
            win.SetUsernameFromUrl(usernameFromUrl);
            win.ShowDialog(owner).GetAwaiter().GetResult();
            return win.DidConfirm ? (win.Username, win.Password) : null;
        });
    }

    private static IBrush? GetDiffBrushFromResources(string key)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return null;
        if (resources.TryGetResource(key, null, out var value) && value is IBrush brush)
            return brush;
        return null;
    }

    private void GitTimelineCommit_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Control c || c.DataContext is not Models.GitCommitItem commit)
            return;
        var filePath = vm.GitPaneViewModel.TimelineFilePath;
        if (string.IsNullOrEmpty(filePath)) return;
        var repoRoot = Services.GitService.FindRepositoryRootForPath(filePath);
        if (string.IsNullOrEmpty(repoRoot)) return;
        var content = Services.GitService.GetFileContentAtCommit(repoRoot, filePath, commit.Sha);
        var win = new TimelineViewWindow();
        win.SetContent(content, commit.Sha, filePath, commit.MessageShort);
        win.SetCompareWithCurrentAction(() =>
        {
            win.Close();
            vm.EnterCompareWithCommit(filePath, commit.Sha);
            Dispatcher.UIThread.Post(RefreshDualDiffDisplay, DispatcherPriority.Loaded);
        });
        _ = win.ShowDialog(this);
    }

    private void SearchResultFileHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c) return;
        if (c.DataContext is SearchResultGroupRowViewModel row)
            row.ToggleExpand();
        else if (c.DataContext is SearchResultGroup group)
            group.IsExpanded = !group.IsExpanded;
    }

    private void SearchResultLine_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || DataContext is not MainViewModel vm)
            return;
        if (c.DataContext is SearchResultLineRowViewModel lineRow)
        {
            vm.SelectedSearchResult = lineRow.Item;
            return;
        }
        if (c.DataContext is SearchResultItem item)
            vm.SelectedSearchResult = item;
    }

    private void FocusSearch(MainViewModel vm)
    {
        vm.IsSearchActive = true;
        UpdateActivityBarHighlight(vm);
        Dispatcher.UIThread.Post(() =>
        {
            try { SearchQueryTextBox?.Focus(); }
            catch { }
        }, DispatcherPriority.Loaded);
    }

    private void ApplyLayout(EditorLayoutMode mode)
    {
        var columns = EditorPreviewSplitGrid?.ColumnDefinitions;
        if (columns == null || columns.Count < 3) return;

        // 从配置中读取编辑区宽度比例，用于 Both 布局时保持上次分栏比例
        double editorRatio = 0.5;
        if (DataContext is MainViewModel vm)
        {
            var r = vm.Config.Ui.EditorWidth;
            if (r > 0 && r < 1)
                editorRatio = Math.Clamp(r, 0.1, 0.9);
        }

        switch (mode)
        {
            case EditorLayoutMode.Both:
                columns[0].Width = new GridLength(editorRatio, GridUnitType.Star);
                columns[1].Width = new GridLength(4);
                columns[2].Width = new GridLength(1 - editorRatio, GridUnitType.Star);
                break;
            case EditorLayoutMode.EditorOnly:
                columns[0].Width = new GridLength(1, GridUnitType.Star);
                columns[1].Width = new GridLength(0);
                columns[2].Width = new GridLength(0);
                break;
            case EditorLayoutMode.PreviewOnly:
                columns[0].Width = new GridLength(0);
                columns[1].Width = new GridLength(0);
                columns[2].Width = new GridLength(1, GridUnitType.Star);
                break;
        }
    }
}

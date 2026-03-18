using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using MarkdownEditor.ViewModels;
using MarkdownEditor.Engine.Highlighting;
using MarkdownEditor.Export;
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

    private ScrollViewer? _editorScroll;
    private ScrollViewer? _previewScroll;
    private bool _isSyncingScroll;
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
    ]);

    private readonly EditorController _editorController;

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
            SetupScrollSync();
            if (PreviewEngine != null && DataContext is MainViewModel m)
                PreviewEngine.GotFocus += (_, _) => m.ActivePane = "Preview";
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
            LayoutBothMenuItem.Click += (_, _) =>
            {
                vm.LayoutMode = EditorLayoutMode.Both;
                PreviewEngine?.RenderControl.ResetEngine();
            };
            LayoutEditorOnlyMenuItem.Click += (_, _) => vm.LayoutMode = EditorLayoutMode.EditorOnly;
            LayoutPreviewOnlyMenuItem.Click += (_, _) => vm.LayoutMode = EditorLayoutMode.PreviewOnly;
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
                await DoSaveAsAsync(vm);
            else
                vm.SaveCurrent();
        };

        SaveAsMenuItem.Click += async (_, _) => await DoSaveAsAsync(vm);

        CloseEditorMenuItem.Click += async (_, _) => await ConfirmCloseCurrentEditorAsync(vm);

        ExitMenuItem.Click += async (_, _) => await ConfirmExitAsync(vm);
        AboutMenuItem.Click += (_, _) => ShowAboutDialog();

        ExportHtmlMenuItem.Click += async (_, _) => await DoExportAsync(vm, "html");
        ExportPdfMenuItem.Click += async (_, _) => await DoExportAsync(vm, "pdf");
        ExportPngMenuItem.Click += async (_, _) => await DoExportAsync(vm, "png");

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
        ActivityGitButton.Click += (_, _) => vm.IsGitActive = true;
        ActivitySettingsButton.Click += (_, _) => vm.IsSettingsActive = true;
        UpdateActivityBarHighlight(vm);

        SetupEditorContextMenuAndKeys(vm);
        SetupFileTreeContextMenu(vm);
        SetupFileTreeRootButtons(vm);
        SetupDocumentTabBackStack(vm);
        SetupSearchResultsNavigation(vm);
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
                    _previewScroll?.Focus();
                    if (_previewScroll == null) PreviewEngine?.Focus();
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

        if (ContentSplitter != null && ContentSplitGrid != null && ContentSplitterPreviewLine != null && ContentSplitterOverlay != null)
        {
            ContentSplitter.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(ContentSplitter).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _contentSplitterDragging = true;
                var pt = e.GetPosition(ContentSplitGrid);
                var total = ContentSplitGrid.Bounds.Width;
                var maxX = Math.Max(contentMinWidth, total - 4 - contentMinWidth);
                var x = Math.Clamp(pt.X, contentMinWidth, maxX);
                ContentSplitterPreviewLine.IsVisible = true;
                ContentSplitterPreviewLine.Margin = new Thickness(x, 0, 0, 0);
                e.Pointer.Capture(ContentSplitter);
            };
            ContentSplitter.PointerMoved += (s, e) =>
            {
                if (!_contentSplitterDragging || ContentSplitGrid == null) return;
                var pt = e.GetPosition(ContentSplitGrid);
                var total = ContentSplitGrid.Bounds.Width;
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
                var pt = e.GetPosition(ContentSplitGrid);
                var total = ContentSplitGrid!.Bounds.Width;
                var maxX = Math.Max(contentMinWidth, total - 4 - contentMinWidth);
                var w = Math.Clamp(pt.X, contentMinWidth, maxX);
                var cols = ContentSplitGrid.ColumnDefinitions;
                if (cols.Count >= 3)
                {
                    cols[0].Width = new GridLength(w, GridUnitType.Pixel);
                    cols[2].Width = new GridLength(1, GridUnitType.Star);
                    if (DataContext is MainViewModel vmContent && total > 4)
                    {
                        var usable = total - 4;
                        var ratio = usable > 0 ? Math.Clamp(w / usable, 0.1, 0.9) : 0.5;
                        vmContent.Config.Ui.EditorWidth = ratio;
                    }
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

    private void SetupScrollSync()
    {
        // 查找左侧编辑控件内部的 ScrollViewer（TextEditor 内部同样包含 ScrollViewer）
        _editorScroll = EditorTextBox
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        // 查找右侧预览控件中的 ScrollViewer
        _previewScroll = PreviewEngine.FindControl<ScrollViewer>("Scroll");

        if (_editorScroll != null)
            _editorScroll.ScrollChanged += EditorScrollOnScrollChanged;

        if (_previewScroll != null)
        {
            _previewScroll.ScrollChanged += PreviewScrollOnScrollChanged;
            _previewScroll.GotFocus += (_, _) =>
            {
                if (DataContext is MainViewModel m)
                    m.ActivePane = "Preview";
            };
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
        }

        if (FileTreeList?.ContextFlyout is MenuFlyout flyout)
        {
            flyout.Opening += (_, _) => UpdateFileTreeMenuState();
        }
    }

    /// <summary>重命名 TextBox 加载后获得焦点并全选；并注册 Tunnel KeyDown，在 TreeView 之前处理 Enter/Escape，避免卡在编辑状态。</summary>
    private void TreeItemRenameTextBox_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.TextBox box || box.DataContext is not FileTreeNode node || !node.IsRenaming)
            return;
        // Tunnel 阶段处理按键，确保在 TreeView 展开/折叠之前提交或取消重命名
        void OnPreviewKeyDown(object? s, KeyEventArgs ev)
        {
            if (ev.Key == Key.Enter)
            {
                CommitTreeItemRename(box);
                ev.Handled = true;
            }
            else if (ev.Key == Key.Escape)
            {
                node.IsRenaming = false;
                node.EditName = node.DisplayName;
                ev.Handled = true;
            }
        }
        box.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // 延迟一帧再聚焦，减少“新建文件夹后立即重命名”时编辑器失焦触发的 VisualLinesInvalidException
        Dispatcher.UIThread.Post(() =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
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
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
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

    /// <summary>标签页行为：记录后退栈 + 处理中键/关闭按钮关闭时的保存确认。</summary>
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

        // 中键点击标签关闭
        DocumentTabControl.AddHandler(
            PointerReleasedEvent,
            async (_, e) =>
            {
                if (e.GetCurrentPoint(DocumentTabControl).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased)
                    return;
                if (e.Source is not Control c || c.DataContext is not DocumentItem item || DataContext is not MainViewModel m)
                    return;

                bool closed = await ConfirmCloseDocumentAsync(m, item);
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
                await DoSaveAsAsync(vm);
                // 新建文档保存后 SaveToPath 已移除未命名文档并打开保存后的文件，item 已不在列表中
            }
            else
            {
                vm.SaveCurrent();
                vm.CloseDocument(item);
                return true;
            }
        }
        else
            vm.CloseDocument(item);
        return true;
    }

    /// <summary>编辑区上下 Padding 总高度（用于按内容高度同步滚动，减少留白导致的偏移）。</summary>
    private const double EditorVerticalPaddingTotal = 16;
    private const double EditorTopPadding = 8;

    private void EditorScrollOnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || _editorScroll == null || _previewScroll == null)
            return;
        if (DataContext is MainViewModel vm && vm.SkipEditorToPreviewScrollSync)
            return;

        _isSyncingScroll = true;
        try
        {
            SyncScrollEditorToPreview();
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void PreviewScrollOnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || _editorScroll == null || _previewScroll == null)
            return;

        _isSyncingScroll = true;
        try
        {
            SyncScrollPreviewToEditor();
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private void SyncScrollEditorToPreview()
    {
        if (_editorScroll == null || _previewScroll == null) return;
        var contentHeight = Math.Max(0, _editorScroll.Extent.Height - EditorVerticalPaddingTotal);
        var editorScrollable = Math.Max(0, contentHeight - _editorScroll.Viewport.Height);
        var contentOffset = Math.Max(0, _editorScroll.Offset.Y - EditorTopPadding);
        var percent = editorScrollable > 0 ? Math.Clamp(contentOffset / editorScrollable, 0, 1) : 0;
        var targetMax = Math.Max(0, _previewScroll.Extent.Height - _previewScroll.Viewport.Height);
        if (targetMax <= 0) return;
        _previewScroll.Offset = new Vector(_previewScroll.Offset.X, targetMax * percent);
    }

    private void SyncScrollPreviewToEditor()
    {
        if (_editorScroll == null || _previewScroll == null) return;
        var sourceMax = Math.Max(0, _previewScroll.Extent.Height - _previewScroll.Viewport.Height);
        if (sourceMax <= 0) return;
        var percent = Math.Clamp(_previewScroll.Offset.Y / sourceMax, 0, 1);
        var contentHeight = Math.Max(0, _editorScroll.Extent.Height - EditorVerticalPaddingTotal);
        var editorScrollable = Math.Max(0, contentHeight - _editorScroll.Viewport.Height);
        var newY = EditorTopPadding + percent * editorScrollable;
        _editorScroll.Offset = new Vector(_editorScroll.Offset.X, newY);
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
            // 同步更新底部布局按钮的文字
            if (LayoutModeButton != null)
            {
                LayoutModeButton.Content = vm.LayoutMode switch
                {
                    EditorLayoutMode.EditorOnly => "仅编辑",
                    EditorLayoutMode.PreviewOnly => "仅预览",
                    _ => "编辑+预览"
                };
            }
        }
        else if (e.PropertyName is nameof(MainViewModel.IsExplorerActive)
                 or nameof(MainViewModel.IsSearchActive)
                 or nameof(MainViewModel.IsGitActive)
                 or nameof(MainViewModel.IsSettingsActive))
            UpdateActivityBarHighlight(vm);
        else if (e.PropertyName == nameof(MainViewModel.IsSearching))
        {
            // 搜索状态由 ViewModel 的 SearchResultStatusText（x 个结果 + 循环小点）与定时器处理
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowEditorPaneForCurrentDoc))
        {
            ApplyLayout(vm.ShowEditorPaneForCurrentDoc ? vm.LayoutMode : EditorLayoutMode.PreviewOnly);
        }
        else if (e.PropertyName == nameof(MainViewModel.EditorZoomLevel) && EditorTextBox is TextEditor ed)
            ed.FontSize = 14.0 * vm.EditorZoomLevel;
        else if (e.PropertyName == nameof(MainViewModel.SelectedPreset) && PreviewEngine != null)
        {
            // 渲染区主题（Markdown 样式预设）切换后，立即将最新样式应用到预览控件
            PreviewEngine.StyleConfig = vm.Config.Markdown;
        }
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
        EditorContextPaste.Click += (_, _) => EditorPaste();
        EditorContextSelectAll.Click += (_, _) => EditorSelectAll();
        EditorContextInsertLink.Click += async (_, _) => await EditorInsertMarkdownResourceAsync(vm, isImage: false);
        EditorContextInsertImage.Click += async (_, _) => await EditorInsertMarkdownResourceAsync(vm, isImage: true);

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
                _ = UpdateEditorPasteEnabledAsync();
            };
        }

        async System.Threading.Tasks.Task UpdateEditorPasteEnabledAsync()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var hasText = false;
            if (clipboard != null)
            {
                try
                {
                    var text = await clipboard.GetTextAsync();
                    hasText = !string.IsNullOrEmpty(text);
                }
                catch { }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                EditorContextPaste.IsEnabled = hasText;
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
        // Ctrl+F 由编辑区 SearchPanel 处理（文件内查找）；跨文件搜索通过侧栏“搜索”按钮打开
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new RelayCommand(() => FocusEditorFind(vm)) });
        // Ctrl+/- 前先按当前键盘焦点同步激活窗格，避免编辑区内层控件获焦时 GotFocus 未冒泡导致始终缩放到预览区
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Add, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomInCommand.Execute(null); }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemPlus, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomInCommand.Execute(null); }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Subtract, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomOutCommand.Execute(null); }) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemMinus, KeyModifiers.Control), Command = new RelayCommand(() => { SyncActivePaneFromFocus(vm); vm.ZoomOutCommand.Execute(null); }) });
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

    /// <summary>在 Tunnel 阶段捕获 Ctrl+S，确保编辑区获焦时也能触发保存（否则会被编辑器吞掉）。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || (e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        if (DataContext is not MainViewModel vm) return;
        vm.SaveCommand.Execute(null);
        e.Handled = true;
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
            MinHeight = 100,
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
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
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

    private async void EditorPaste()
    {
        if (EditorTextBox is not TextEditor editor || editor.Document == null) return;
        editor.Focus();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var text = await clipboard.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        var seg = editor.TextArea.Selection?.SurroundingSegment;
        int offset = seg?.Offset ?? editor.TextArea.Caret.Offset;
        int length = seg?.Length ?? 0;
        editor.Document.Replace(offset, length, text);
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

    private async System.Threading.Tasks.Task DoSaveAsAsync(MainViewModel vm)
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
            vm.SaveToPath(path);
    }

    private async System.Threading.Tasks.Task DoExportAsync(MainViewModel vm, string formatId)
    {
        if (vm.CurrentMarkdown == null)
        {
            var msg = new Window
            {
                Title = "导出",
                Width = 360,
                Height = 100,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = GetAppIcon()
            };
            var closeBtn = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            closeBtn.Click += (_, _) => msg.Close();
            msg.Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { new TextBlock { Text = "当前无内容可导出。请先打开或编辑文档。", TextWrapping = TextWrapping.Wrap }, closeBtn } };
            await msg.ShowDialog(this);
            return;
        }
        var (success, errorMessage) = await _exportService.ExportWithDialogAsync(vm, formatId, StorageProvider);
        if (success == false && !string.IsNullOrEmpty(errorMessage))
        {
            var msg = new Window
            {
                Title = "导出失败",
                Width = 380,
                Height = 140,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = GetAppIcon()
            };
            var okButton = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            okButton.Click += (_, _) => msg.Close();
            msg.Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children = { new TextBlock { Text = errorMessage, TextWrapping = TextWrapping.Wrap }, okButton }
            };
            await msg.ShowDialog(this);
        }
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
                Para("Ver 1.0.0 @ 2026", FontWeight.SemiBold),
                Para(
                    "We built this to solve our own problems. Now we maintain it to solve yours. Free forever. Works offline. No accounts, no tracking, no expiration date. Just a reliable tool that grows with you."
                ),
                Para("Spc: LdotJdot, Herman Chen"),
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
                await DoSaveAsAsync(vm);
                // 新建文档保存后 SaveToPath 已用保存的文件替换当前标签，无需再关闭
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
                    await DoSaveAsAsync(vm);
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
        var columns = ContentSplitGrid.ColumnDefinitions;
        if (columns.Count < 3) return;

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

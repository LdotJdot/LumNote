using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Threading;
using AvaloniaEdit;
using MarkdownEditor.ViewModels;
using MarkdownEditor.Engine.Highlighting;

namespace MarkdownEditor.Views;

public partial class MainWindow : Window
{
    private ScrollViewer? _editorScroll;
    private ScrollViewer? _previewScroll;
    private bool _isSyncingScroll;
    private readonly DispatcherTimer _highlightTimer;
    private readonly MarkdownHighlightingService _highlightService = new();
    private readonly MarkdownColorizer _markdownColorizer = new(MarkdownHighlightTheme.DarkTheme);

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        // 初始化编辑器语法高亮
        SetupEditorHighlighting();

        Opened += (_, _) => SetupScrollSync();

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

        // 底部状态栏布局切换
        LayoutBothButton.Click += (_, _) => vm.LayoutMode = EditorLayoutMode.Both;
        LayoutEditorOnlyButton.Click += (_, _) => vm.LayoutMode = EditorLayoutMode.EditorOnly;
        LayoutPreviewOnlyButton.Click += (_, _) => vm.LayoutMode = EditorLayoutMode.PreviewOnly;

        OpenFolderMenuItem.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择 Markdown 文档所在文件夹",
                AllowMultiple = false
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                vm.LoadFolder(path);
            }
        };

        SaveMenuItem.Click += (_, _) => vm.SaveCurrent();

        ActivityExplorerButton.Click += (_, _) => vm.IsExplorerActive = true;
        ActivitySearchButton.Click += (_, _) => vm.IsSearchActive = true;
        ActivityGitButton.Click += (_, _) => vm.IsGitActive = true;
        UpdateActivityBarHighlight(vm);

        Closed += (_, _) => vm.Config.Save(Core.AppConfig.DefaultConfigPath);

        _highlightTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            UpdateEditorHighlight();
        };
    }

    private void SetupEditorHighlighting()
    {
        if (EditorTextBox is TextEditor editor)
        {
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.TextArea.TextView.LineTransformers.Add(_markdownColorizer);

            if (DataContext is MainViewModel vm)
            {
                editor.Text = vm.CurrentMarkdown ?? string.Empty;

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.CurrentMarkdown))
                    {
                        var target = vm.CurrentMarkdown ?? string.Empty;
                        if (!string.Equals(editor.Text, target, StringComparison.Ordinal))
                        {
                            var caret = editor.TextArea.Caret.Offset;
                            editor.Text = target;
                            editor.TextArea.Caret.Offset = System.Math.Min(caret, editor.Text.Length);
                        }
                    }
                };

                editor.TextChanged += (_, _) =>
                {
                    var text = editor.Text ?? string.Empty;
                    if (!string.Equals(vm.CurrentMarkdown, text, StringComparison.Ordinal))
                    {
                        vm.CurrentMarkdown = text;
                    }

                    _highlightTimer.Stop();
                    _highlightTimer.Start();
                };
            }
            else
            {
                editor.TextChanged += (_, _) =>
                {
                    _highlightTimer.Stop();
                    _highlightTimer.Start();
                };
            }
        }
    }

    private void UpdateEditorHighlight()
    {
        if (EditorTextBox is not TextEditor editor)
            return;
        var text = editor.Text ?? string.Empty;
        var tokens = _highlightService.Analyze(text);
        _markdownColorizer.UpdateTokens(tokens);
        editor.TextArea.TextView.Redraw();
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
            _previewScroll.ScrollChanged += PreviewScrollOnScrollChanged;
    }

    private void EditorScrollOnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || _editorScroll == null || _previewScroll == null)
            return;

        _isSyncingScroll = true;
        try
        {
            SyncScroll(_editorScroll, _previewScroll);
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
            SyncScroll(_previewScroll, _editorScroll);
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }

    private static void SyncScroll(ScrollViewer source, ScrollViewer target)
    {
        var sourceMax = Math.Max(0, source.Extent.Height - source.Viewport.Height);
        var targetMax = Math.Max(0, target.Extent.Height - target.Viewport.Height);
        if (sourceMax <= 0 || targetMax <= 0)
            return;

        var percent = source.Offset.Y / sourceMax;
        var newY = targetMax * percent;
        target.Offset = new Vector(target.Offset.X, newY);
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm)
            return;

        if (e.PropertyName == nameof(MainViewModel.LayoutMode))
            ApplyLayout(vm.LayoutMode);
        else if (e.PropertyName is nameof(MainViewModel.IsExplorerActive)
                 or nameof(MainViewModel.IsSearchActive)
                 or nameof(MainViewModel.IsGitActive))
            UpdateActivityBarHighlight(vm);
    }

    private void UpdateActivityBarHighlight(MainViewModel vm)
    {
        ActivityExplorerButton.Classes.Set("active", vm.IsExplorerActive);
        ActivitySearchButton.Classes.Set("active", vm.IsSearchActive);
        ActivityGitButton.Classes.Set("active", vm.IsGitActive);
    }

    private void ApplyLayout(EditorLayoutMode mode)
    {
        var columns = ContentSplitGrid.ColumnDefinitions;
        if (columns.Count < 3) return;

        switch (mode)
        {
            case EditorLayoutMode.Both:
                columns[0].Width = new GridLength(1, GridUnitType.Star);
                columns[1].Width = new GridLength(4);
                columns[2].Width = new GridLength(1, GridUnitType.Star);
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

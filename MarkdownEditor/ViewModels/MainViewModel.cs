using Avalonia.Threading;
using LumConfig;
using MarkdownEditor.Controls;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MarkdownEditor.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    public Core.AppConfig Config { get; } = Core.AppConfig.Load(Core.AppConfig.DefaultConfigPath);

    /// <summary>界面主题切换后由视图设置 Application.RequestedThemeVariant。</summary>
    public event Action? ThemeChanged;

    /// <summary>用于“界面主题”下拉的显示名称。</summary>
    public static IReadOnlyList<string> ThemeDisplayNames { get; } = new[] { "深色", "浅色" };

    /// <summary>当前界面主题显示名（深色/浅色），双向绑定；设置时会写回 Config 并触发 ThemeChanged。</summary>
    public string CurrentThemeDisplayName
    {
        get => Config.Ui.Theme == "Light" ? "浅色" : "深色";
        set
        {
            var isLight = string.Equals(value, "浅色", StringComparison.OrdinalIgnoreCase);
            Config.Ui.Theme = isLight ? "Light" : "Dark";
            OnPropertyChanged(nameof(CurrentThemeDisplayName));
            OnPropertyChanged(nameof(Config));
            NotifyThemeToggleUi();
            ThemeChanged?.Invoke();
        }
    }

    /// <summary>状态栏单键主题切换：浅色显示 ☀，深色显示 🌙。</summary>
    public string ThemeToggleGlyph =>
        string.Equals(Config.Ui.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "☀" : "🌙";

    private void NotifyThemeToggleUi()
    {
        OnPropertyChanged(nameof(ThemeToggleGlyph));
    }

    /// <summary>在浅色 / 深色 bundled 主题间切换（界面 + Markdown 预设）。</summary>
    public ICommand ToggleThemeCommand =>
        new RelayCommand(() =>
        {
            var light = string.Equals(Config.Ui.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            ApplyBundledTheme(lightUi: !light);
        });

    private void ApplyBundledTheme(bool lightUi)
    {
        var keepPreviewZoom = PreviewZoomLevel;
        Config.Ui.Theme = lightUi ? "Light" : "Dark";
        Config.ApplyPreset(lightUi ? "浅色" : "深色");
        Config.Markdown.ZoomLevel = keepPreviewZoom;
        OnPropertyChanged(nameof(CurrentThemeDisplayName));
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged("Config.Markdown");
        OnPropertyChanged(nameof(PreviewZoomLevel));
        NotifyThemeToggleUi();
        ThemeChanged?.Invoke();
    }

    /// <summary>当前选中的样式预设；设置时应用该预设并刷新 Config。</summary>
    public Core.MarkdownStylePreset? SelectedPreset
    {
        get => Config.StylePresets.FirstOrDefault(p => string.Equals(p.Name, Config.ActivePresetName, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value == null || !Config.ApplyPreset(value.Name))
                return;
            OnPropertyChanged(nameof(SelectedPreset));
            // 通知绑定到 Config 以及 Config.Markdown 的视图刷新（渲染区主题即时生效）
            OnPropertyChanged(nameof(Config));
            OnPropertyChanged("Config.Markdown");
        }
    }

    /// <summary>最近打开的文件列表（按打开时间排序，含路径、时间、大小等，欢迎页与持久化）。</summary>
    public ObservableCollection<RecentFileItem> RecentFileItems => _recentFileItems;

    /// <summary>欢迎页最近文件显示数量上限，超出部分通过“更多...”下拉显示。</summary>
    public const int RecentDisplayLimit = 8;

    /// <summary>欢迎页显示的前 N 个最近文件。</summary>
    public IEnumerable<RecentFileItem> RecentFileItemsVisible => _recentFileItems.Take(RecentDisplayLimit);

    /// <summary>超出显示上限的最近文件，用于“更多...”下拉菜单。</summary>
    public IEnumerable<RecentFileItem> RecentFileItemsMore => _recentFileItems.Skip(RecentDisplayLimit);

    /// <summary>是否有超出显示上限的最近文件。</summary>
    public bool HasMoreRecentFiles => _recentFileItems.Count > RecentDisplayLimit;

    /// <summary>最近打开的文件夹路径列表（欢迎页与持久化）。</summary>
    public ObservableCollection<string> RecentFolderPaths => _recentFolderPaths;

    /// <summary>欢迎页显示的前 N 个最近文件夹。</summary>
    public IEnumerable<string> RecentFolderPathsVisible => _recentFolderPaths.Take(RecentDisplayLimit);

    /// <summary>超出显示上限的最近文件夹，用于“更多...”下拉菜单。</summary>
    public IEnumerable<string> RecentFolderPathsMore => _recentFolderPaths.Skip(RecentDisplayLimit);

    /// <summary>是否有超出显示上限的最近文件夹。</summary>
    public bool HasMoreRecentFolders => _recentFolderPaths.Count > RecentDisplayLimit;

    /// <summary>无打开文档时显示欢迎页（最近打开列表）。</summary>
    public bool ShowWelcomeView => _openDocuments.Count == 0;

    /// <summary>有打开文档时显示编辑/预览区（与 ShowWelcomeView 互斥）。</summary>
    public bool ShowEditorView => _openDocuments.Count > 0;

    public MainViewModel()
    {
        // 根据配置恢复布局模式（默认 Both）
        if (!string.IsNullOrEmpty(Config.Ui.LayoutMode))
        {
            try
            {
                if (Enum.TryParse<EditorLayoutMode>(Config.Ui.LayoutMode, out var mode))
                    _layoutMode = mode;
            }
            catch
            {
                _layoutMode = EditorLayoutMode.Both;
            }
        }
        LoadRecentFoldersFromDisk(_recentFolderPaths);
        _ = InitializeRecentFilesAsync();
    }

    /// <summary>退出时由视图调用，确保最近列表已持久化。</summary>
    public void SaveRecentState()
    {
        _saveRecentFilesTimer?.Stop();
        SaveRecentFilesToDisk();
    }

    private string _documentFolder = "";
    private string _searchQuery = "";
    private string _currentMarkdown = "";
    private string _currentFilePath = "";
    private string _currentFileName = "";
    private DocumentItem? _selectedDocument;
    private FileTreeNode? _selectedTreeNode;
    /// <summary>刷新文件树时恢复选中用，为 true 时 SelectedTreeNode  setter 不触发打开文档，避免重复开标签。</summary>
    private bool _isRestoringFileTreeSelection;
    private ObservableCollection<DocumentItem> _documents = [];
    private ObservableCollection<DocumentItem> _filteredDocuments = [];
    private ObservableCollection<DocumentItem> _openDocuments = [];
    private ObservableCollection<FileTreeNode> _fileTreeRoot = [];
    /// <summary>扁平可见节点列表，用于虚拟化 ListBox 展示（VSCode 风格，整行点击展开/折叠）。</summary>
    private readonly ObservableCollection<FileTreeNode> _visibleFileTree = [];
    private readonly ObservableCollection<SearchResultItem> _searchResults = [];
    private readonly ObservableCollection<SearchResultGroup> _searchResultGroups = [];
    /// <summary>扁平的搜索结果行（组头+匹配行），用于虚拟化列表，减少内存与 UI 阻塞。</summary>
    private readonly ObservableCollection<SearchResultRowViewModel> _flatSearchResultRows = [];
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private bool _isModified;
    private EditorLayoutMode _layoutMode = EditorLayoutMode.Both;
    private bool _isExplorerActive = true;
    private bool _isSearchActive;
    private bool _isSettingsActive;
    private bool _isGitActive;
    private string? _previewImagePath;

    private DocumentItem? _activeDocument;
    private readonly Dictionary<string, double> _previewScrollRatiosByPath = new(StringComparer.OrdinalIgnoreCase);
    private double _currentPreviewScrollRatio;
    private double? _pendingPreviewScrollRatio;
    private readonly Stack<(string path, int offset)> _focusBackStack = new();
    private readonly Stack<(string path, int offset)> _focusForwardStack = new();
    private const int MaxRecentFiles = 100;
    private const int MaxRecentFolders = 10;
    /// <summary>工作区根目录列表；多根时左侧文件树每个根默认折叠。</summary>
    private readonly List<string> _workspaceFolderPaths = [];
    /// <summary>关闭后的文档内容缓存（路径→内容），最多 10 个，FIFO 淘汰，避免越用越卡。</summary>
    private readonly List<(string path, string content)> _closedDocumentCache = [];
    private const int MaxClosedDocumentCache = 10;
    private readonly ObservableCollection<RecentFileItem> _recentFileItems = [];
    private readonly ObservableCollection<string> _recentFolderPaths = [];
    private CancellationTokenSource? _searchCts;
    private Task? _searchTask;
    private DispatcherTimer? _saveRecentFilesTimer;
    private DispatcherTimer? _searchLoadingDotsTimer;
    private string _searchLoadingDots = ".";

    /// <summary>无文档时编辑区默认缩放。</summary>
    private double _editorZoomLevel = 1.0;
    /// <summary>无文档时预览区默认缩放（与 Config 同步）。</summary>
    private double _previewZoomLevelDefault = 1.0;
    /// <summary>当前激活窗格：Editor / Preview，用于 Ctrl+/- 和状态栏显示。</summary>
    private string _activePane = "Editor";
    /// <summary>当前选择的编码显示名（用于状态栏与保存时）。</summary>
    private string _currentEncodingName = "UTF-8";

    /// <summary>当前工作区第一个根目录（兼容单文件夹显示）；多根时仅作显示用。</summary>
    public string DocumentFolder
    {
        get => _workspaceFolderPaths.Count > 0 ? _workspaceFolderPaths[0] : _documentFolder;
        set
        {
            if (SetProperty(ref _documentFolder, value) && _workspaceFolderPaths.Count == 0)
                OnPropertyChanged(nameof(DocumentFolder));
        }
    }

    /// <summary>是否有工作区打开（至少一个根目录）。</summary>
    public bool HasWorkspaceOpen => _workspaceFolderPaths.Count > 0;

    /// <summary>未打开工作区时在文件夹浏览区显示“打开工作区/打开文件夹”占位。</summary>
    public bool ShowWorkspacePlaceholder => !HasWorkspaceOpen;

    /// <summary>搜索关键词。过滤文档列表立即生效；实际全文搜索由视图防抖后调用 <see cref="DoSearch"/>。</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
                FilterDocuments();
        }
    }

    public ObservableCollection<SearchResultItem> SearchResults => _searchResults;

    /// <summary>按文件分组的搜索结果，用于侧栏按文件列出、文件内可折叠。</summary>
    public ObservableCollection<SearchResultGroup> SearchResultGroups => _searchResultGroups;

    /// <summary>扁平的搜索结果行（虚拟化列表用），组头与匹配行交替。</summary>
    public ObservableCollection<SearchResultRowViewModel> FlatSearchResultRows => _flatSearchResultRows;

    private bool _isSearching;
    /// <summary>是否正在执行搜索（用于显示“搜索中”提示）。</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) return;
            OnPropertyChanged(nameof(IsSearchCompleted));
            OnPropertyChanged(nameof(SearchResultStatusText));
            if (value) StartSearchLoadingDotsTimer(); else StopSearchLoadingDotsTimer();
        }
    }
    /// <summary>搜索未在进行中，用于显示结果数量。</summary>
    public bool IsSearchCompleted => !_isSearching;

    private int _searchResultCount;
    /// <summary>当前搜索结果总条数（匹配行数）；搜索完成后更新，无结果时为 0。</summary>
    public int SearchResultCount { get => _searchResultCount; private set => SetProperty(ref _searchResultCount, value); }

    /// <summary>结果数量文案；搜索中时在「x 个结果」后追加循环小点 . / .. / ... / .... 表示进行中。</summary>
    public string SearchResultStatusText => $"{SearchResultCount} 个结果" + (IsSearching ? _searchLoadingDots : "");

    /// <summary>搜索结果数量文案（仅数量，无状态点）。</summary>
    public string SearchResultCountText => $"{SearchResultCount} 个结果";

    private SearchResultItem? _selectedSearchResult;
    /// <summary>用户点击某条搜索结果时设置，由视图订阅并调用 NavigateToSearchResult。</summary>
    public SearchResultItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (_selectedSearchResult == value) return;
            _selectedSearchResult = value;
            OnPropertyChanged(nameof(SelectedSearchResult));
        }
    }

    public int CaretLine { get => _caretLine; set => SetProperty(ref _caretLine, value); }
    public int CaretColumn { get => _caretColumn; set => SetProperty(ref _caretColumn, value); }

    /// <summary>为 true 时，编辑区→预览的滚动同步应跳过，避免预览发起更新（如 todo 勾选）后编辑器写回触发同步覆盖预览滚动恢复。</summary>
    public bool SkipEditorToPreviewScrollSync { get; set; }

    public string CurrentMarkdown
    {
        get => _currentMarkdown;
        set
        {
            var text = value ?? string.Empty;
            if (SetProperty(ref _currentMarkdown, text))
            {
                // 无论是否有路径（新建文档或已打开文件），内容变更均标记为未保存，以便关闭时提示
                IsModified = true;
                if (_activeDocument is not null)
                {
                    _activeDocument.CachedMarkdown = text;
                    _activeDocument.IsModified = true;
                }
                ScheduleAutoSaveDebounced();
            }
        }
    }

    /// <summary>
    /// 将 Markdown 文本中的有序列表编号规范化为连续编号（从每段列表的首行编号开始递增），
    /// 避免 1. 1. 1. 这类硬编码编号在渲染时与预期不符。
    /// </summary>
    private static string NormalizeOrderedLists(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown ?? string.Empty;

        var lines = markdown.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            // 匹配形如 "1. xxx" 的有序列表项（允许前置缩进）
            int j = 0;
            while (j < trimmed.Length && char.IsDigit(trimmed[j]))
                j++;
            if (j == 0 || j + 1 >= trimmed.Length || trimmed[j] != '.' || trimmed[j + 1] != ' ')
            {
                i++;
                continue;
            }

            // 进入一个有序列表块
            var indentLen = line.Length - trimmed.Length;
            var indent = indentLen > 0 ? line[..indentLen] : string.Empty;
            int startNum = int.TryParse(trimmed[..j], out var n) ? n : 1;
            int currentNum = startNum;
            int k = i;
            while (k < lines.Length)
            {
                var l = lines[k];
                var t = l.TrimStart();
                if (t.Length == 0)
                {
                    // 空行视为列表中断
                    break;
                }
                int jj = 0;
                while (jj < t.Length && char.IsDigit(t[jj]))
                    jj++;
                if (jj == 0 || jj + 1 >= t.Length || t[jj] != '.' || t[jj + 1] != ' ')
                {
                    break;
                }

                // 用规范化编号替换前缀，保持原有缩进和正文内容
                var content = t[(jj + 2)..];
                lines[k] = indent + currentNum.ToString() + ". " + content;
                currentNum++;
                k++;
            }

            i = k;
        }

        return string.Join("\n", lines);
    }

    private string _documentBasePath = "";

    public string CurrentFilePath
    {
        get => _currentFilePath;
        set
        {
            if (SetProperty(ref _currentFilePath, value))
            {
                _documentBasePath = string.IsNullOrEmpty(value) ? "" : Path.GetDirectoryName(value) ?? "";
                OnPropertyChanged(nameof(DocumentBasePath));
            }
        }
    }

    /// <summary>
    /// 当前文档所在目录，用于预览中解析图片相对路径
    /// </summary>
    public string DocumentBasePath => _documentBasePath;

    /// <summary>
    /// LoadFromDocumentItem / ClearEditor 直接改 _currentFilePath 时须同步，否则预览相对路径图片一直用错基目录。
    /// </summary>
    private void SyncDocumentBasePathWithCurrentFile()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_currentFilePath))
                _documentBasePath = "";
            else
                _documentBasePath = Path.GetDirectoryName(Path.GetFullPath(_currentFilePath)) ?? "";
        }
        catch
        {
            _documentBasePath = "";
        }
        OnPropertyChanged(nameof(DocumentBasePath));
    }

    public string CurrentFileName
    {
        get => _currentFileName;
        set
        {
            if (SetProperty(ref _currentFileName, value))
            {
                OnPropertyChanged(nameof(CenterTitle));
            }
        }
    }

    /// <summary>左上角应用标题文本。</summary>
    public string TitleStatus
    {
        get
        {
            return "LumNote";
        }
    }

    /// <summary>
    /// 标题栏中间区域显示的文本：
    /// - 打开文件且已保存："[已保存] 文件名"
    /// - 打开文件但未保存：仅文件名
    /// - 未打开文件：空
    /// </summary>
    public string CenterTitle
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentFileName))
                return string.Empty;
            // 未修改或已保存：只显示文件名；有未保存修改：显示 "[待保存] 文件名"
            return IsModified ? "[待保存] " + CurrentFileName : CurrentFileName;
        }
    }

    public DocumentItem? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (_selectedDocument == value) return;
            _selectedDocument = value;
            OnPropertyChanged();
            if (value != null)
                LoadDocument(value.FullPath);
            else
                ClearEditor();
        }
    }

    public ObservableCollection<DocumentItem> Documents => _documents;
    public ObservableCollection<DocumentItem> FilteredDocuments => _filteredDocuments;

    /// <summary>当前已打开的文档选项卡集合。</summary>
    public ObservableCollection<DocumentItem> OpenDocuments => _openDocuments;

    /// <summary>预览区当前滚动比例 [0,1]，由 MarkdownEngineView 在滚动时更新。</summary>
    public double CurrentPreviewScrollRatio
    {
        get => _currentPreviewScrollRatio;
        set => SetProperty(ref _currentPreviewScrollRatio, value);
    }

    /// <summary>切换回某文档时待恢复的滚动比例，由 MarkdownEngineView 读取后清除。</summary>
    public double? PendingPreviewScrollRatio
    {
        get => _pendingPreviewScrollRatio;
        set => SetProperty(ref _pendingPreviewScrollRatio, value);
    }

    /// <summary>当前活动的文档（与选项卡选中项保持一致）。</summary>
    public DocumentItem? ActiveDocument
    {
        get => _activeDocument;
        set
        {
            if (_activeDocument == value) return;

            var pathToSave = string.IsNullOrEmpty(_currentFilePath)
                ? (_activeDocument != null ? "untitled:" + _activeDocument.DisplayName : null)
                : _currentFilePath;
            if (!string.IsNullOrEmpty(pathToSave))
                _previewScrollRatiosByPath[pathToSave] = _currentPreviewScrollRatio;

            _activeDocument = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowEditorPaneForCurrentDoc));

            if (value is null)
            {
                ClearEditor();
            }
            else
            {
                LoadFromDocumentItem(value);
                Config.Markdown.ZoomLevel = value.PreviewZoomLevel;
                _currentEncodingName = value.EncodingName;
                OnPropertyChanged(nameof(EditorZoomLevel));
                OnPropertyChanged(nameof(PreviewZoomLevel));
                OnPropertyChanged(nameof(ActivePaneZoomLevel));
                OnPropertyChanged(nameof(CurrentEncodingName));
            }
        }
    }

    /// <summary>当前是否为图片标签（图片时隐藏编辑区，仅显示预览）。</summary>
    public bool ShowEditorPaneForCurrentDoc =>
        _activeDocument == null || !IsPreviewableImagePath(_activeDocument.FullPath);

    /// <summary>文件树根节点（VSCode 风格侧边栏用）。</summary>
    public ObservableCollection<FileTreeNode> FileTreeRoot => _fileTreeRoot;

    /// <summary>扁平可见节点列表，用于侧栏 ListBox 虚拟化展示；展开/折叠后需调用 RebuildVisibleFileTree。</summary>
    public ObservableCollection<FileTreeNode> VisibleFileTree => _visibleFileTree;

    /// <summary>请求在独立窗口中打开图片（仅显示图片，无编辑区）。由视图订阅并弹出 ImageViewWindow；预览区点击图片时使用。</summary>
    public event Action<string>? OpenImageInNewWindowRequested;

    /// <summary>在独立窗口中打开图片（供预览区点击图片等调用）。</summary>
    public void OpenImageInNewWindow(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && IsPreviewableImagePath(path))
            OpenImageInNewWindowRequested?.Invoke(path);
    }

    /// <summary>将图片以标签页形式在右侧打开（与文档标签一起管理），不读文件内容。</summary>
    public void OpenImageInTab(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsPreviewableImagePath(path)) return;

        var doc = _documents.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            var root = GetWorkspaceRootForPath(path);
            var rel = root != null ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
            doc = new DocumentItem(path, rel) { WorkspaceRoot = root };
            _documents.Add(doc);
        }
        if (!_openDocuments.Contains(doc))
        {
            doc.IsOpen = true;
            _openDocuments.Add(doc);
            OnPropertyChanged(nameof(OpenDocuments));
            NotifyWelcomeViewChanged();
        }
        ActiveDocument = doc;
    }

    public FileTreeNode? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (_selectedTreeNode == value) return;
            _selectedTreeNode = value;
            OnPropertyChanged();

            // 刷新后仅恢复树选中状态，不再次打开文档，避免同一文档被多次加入标签
            if (_isRestoringFileTreeSelection)
            {
                if (value is { IsFolder: false })
                    PreviewImagePath = null;
                return;
            }

            // 统一行为：
            // - 文档(.md/.txt)：打开并预览
            // - 图片：作为标签页在右侧打开，与文档标签一起管理
            // - 文件夹：仅改变选中项
            if (value is { IsFolder: false } node)
            {
                var path = node.FullPath;
                if (IsPreviewableImagePath(path))
                {
                    OpenImageInTab(path);
                }
                else
                {
                    PreviewImagePath = null;
                    LoadDocument(path);
                }
            }
            else
            {
                PreviewImagePath = null;
            }
        }
    }

    /// <summary>当前仅在右侧预览的图片路径（树中选中图片文件时设置，选中文档或文件夹时清空）。</summary>
    public string? PreviewImagePath
    {
        get => _previewImagePath;
        set
        {
            if (SetProperty(ref _previewImagePath, value))
            {
                OnPropertyChanged(nameof(ShowPreviewImage));
                OnPropertyChanged(nameof(ShowMarkdownPreview));
            }
        }
    }

    /// <summary>是否正在预览图片（右侧显示图片面板而非 Markdown）。</summary>
    public bool ShowPreviewImage => !string.IsNullOrEmpty(_previewImagePath);

    /// <summary>是否显示 Markdown 预览（即未在预览图片）。</summary>
    public bool ShowMarkdownPreview => string.IsNullOrEmpty(_previewImagePath);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"
    };

    private static bool IsPreviewableImagePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext);
    }

    public bool IsModified
    {
        get => _isModified;
        set
        {
            if (SetProperty(ref _isModified, value) && _activeDocument is not null)
            {
                _activeDocument.IsModified = value;
            }
            OnPropertyChanged(nameof(CenterTitle));
        }
    }

    /// <summary>预览区缩放（1.0=100%）。有当前文档时使用该文档的 PreviewZoomLevel，否则用全局。</summary>
    public double PreviewZoomLevel
    {
        get => _activeDocument != null ? _activeDocument.PreviewZoomLevel : _previewZoomLevelDefault;
        set
        {
            var v = Math.Clamp(value, 0.5, 2.5);
            if (_activeDocument != null)
            {
                if (Math.Abs(_activeDocument.PreviewZoomLevel - v) < 0.001) return;
                _activeDocument.PreviewZoomLevel = v;
            }
            else
            {
                if (Math.Abs(_previewZoomLevelDefault - v) < 0.001) return;
                _previewZoomLevelDefault = v;
            }
            Config.Markdown.ZoomLevel = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivePaneZoomLevel));
        }
    }

    /// <summary>编辑区缩放（1.0=100%）。有当前文档时使用该文档的 EditorZoomLevel，否则用全局。</summary>
    public double EditorZoomLevel
    {
        get => _activeDocument != null ? _activeDocument.EditorZoomLevel : _editorZoomLevel;
        set
        {
            var v = Math.Clamp(value, 0.5, 2.5);
            if (_activeDocument != null)
            {
                if (Math.Abs(_activeDocument.EditorZoomLevel - v) < 0.001) return;
                _activeDocument.EditorZoomLevel = v;
            }
            else
            {
                if (Math.Abs(_editorZoomLevel - v) < 0.001) return;
                _editorZoomLevel = v;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActivePaneZoomLevel));
        }
    }

    /// <summary>当前激活窗格：Editor / Preview。</summary>
    public string ActivePane
    {
        get => _activePane;
        set
        {
            if (SetProperty(ref _activePane, value))
            {
                OnPropertyChanged(nameof(ActivePaneZoomLevel));
                OnPropertyChanged(nameof(ActivePaneName));
                OnPropertyChanged(nameof(ZoomLevelToolTip));
            }
        }
    }

    /// <summary>当前激活窗格的缩放（用于状态栏显示与滑动调整）。</summary>
    public double ActivePaneZoomLevel
    {
        get => _activePane == "Preview" ? PreviewZoomLevel : EditorZoomLevel;
        set
        {
            if (_activePane == "Preview")
                PreviewZoomLevel = value;
            else
                EditorZoomLevel = value;
        }
    }

    /// <summary>当前激活窗格名称（用于状态栏）。</summary>
    public string ActivePaneName => _activePane == "Preview" ? "预览" : "编辑";

    /// <summary>状态栏缩放按钮的提示，标明当前操作的是编辑区还是预览区。</summary>
    public string ZoomLevelToolTip =>
        $"缩放（当前：{ActivePaneName}区）。Ctrl+/- 或 Ctrl+滚轮；鼠标在编辑区/预览区上滚轮即可，无需先点击获焦。";

    /// <summary>当前编码显示名（状态栏点击可选）。有当前文档时与文档一致。</summary>
    public string CurrentEncodingName
    {
        get => _activeDocument != null ? _activeDocument.EncodingName : _currentEncodingName;
        set
        {
            var name = value ?? "UTF-8";
            if (_activeDocument != null)
                _activeDocument.EncodingName = name;
            if (SetProperty(ref _currentEncodingName, name) && _activeDocument != null && !string.IsNullOrEmpty(CurrentFilePath) && File.Exists(CurrentFilePath))
                ReloadWithCurrentEncoding();
        }
    }

    /// <summary>状态栏点击编码时设置编码（CommandParameter 为编码名字符串），并实时按新编码重读当前文件。</summary>
    public ICommand SetEncodingCommand => new RelayCommand<object>(param =>
    {
        if (param is string name && !string.IsNullOrWhiteSpace(name))
            CurrentEncodingName = name;
    });

    /// <summary>从磁盘重新加载当前文件内容（使用当前文档编码）。</summary>
    public void ReloadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(CurrentFilePath) || _activeDocument == null) return;
        ReloadWithCurrentEncoding();
    }

    /// <summary>按当前文档所选编码重新读取当前文件并更新编辑区（编码切换时调用）。</summary>
    private void ReloadWithCurrentEncoding()
    {
        if (string.IsNullOrWhiteSpace(CurrentFilePath) || _activeDocument == null || !File.Exists(CurrentFilePath)) return;
        try
        {
            var path = CurrentFilePath;
            var enc = GetEncodingByName(_activeDocument.EncodingName);
            var text = File.ReadAllText(path, enc);
            _activeDocument.CachedMarkdown = text;
            _activeDocument.IsModified = false;
            _activeDocument.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            _currentMarkdown = text;
            _isModified = false;
            OnPropertyChanged(nameof(CurrentMarkdown));
            OnPropertyChanged(nameof(IsModified));
        }
        catch { }
    }

    private static Encoding GetEncodingByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;
        try
        {
            return name.ToUpperInvariant() switch
            {
                "UTF-16 BE" => Encoding.BigEndianUnicode,
                "UTF-16" => Encoding.Unicode,
                _ => Encoding.GetEncoding(name)
            };
        }
        catch { return Encoding.UTF8; }
    }

    public ICommand ZoomInCommand => new RelayCommand(() =>
    {
        if (_activePane == "Preview")
            PreviewZoomLevel = Math.Min(2.5, PreviewZoomLevel + 0.1);
        else
            EditorZoomLevel = Math.Min(2.5, EditorZoomLevel + 0.1);
    });
    public ICommand ZoomOutCommand => new RelayCommand(() =>
    {
        if (_activePane == "Preview")
            PreviewZoomLevel = Math.Max(0.5, PreviewZoomLevel - 0.1);
        else
            EditorZoomLevel = Math.Max(0.5, EditorZoomLevel - 0.1);
    });

    /// <summary>底部状态栏点击缩放标签时，从下拉选择预设缩放比例（CommandParameter 为字符串如 "1.0" 或数值）。</summary>
    public ICommand SetActivePaneZoomCommand => new RelayCommand<object>(param =>
    {
        var v = param switch
        {
            double d => d,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (double?)null
        };
        if (v.HasValue)
            ActivePaneZoomLevel = Math.Clamp(v.Value, 0.5, 2.5);
    });
    /// <summary>仅缩放预览区（预览标题栏按钮用）。</summary>
    public ICommand ZoomPreviewInCommand => new RelayCommand(() => PreviewZoomLevel = Math.Min(2.5, PreviewZoomLevel + 0.1));
    public ICommand ZoomPreviewOutCommand => new RelayCommand(() => PreviewZoomLevel = Math.Max(0.5, PreviewZoomLevel - 0.1));
    public ICommand SaveCommand => new RelayCommand(SaveCurrent);
    public ICommand CloseDocumentCommand => new RelayCommand<DocumentItem>(CloseDocument);
    public ICommand OpenRecentDocumentCommand => new RelayCommand<string>(OpenDocument);
    /// <summary>侧栏搜索结果点击某条时由视图绑定，设置 SelectedSearchResult 以便视图跳转。</summary>
    public ICommand NavigateToSearchResultCommand => new RelayCommand<SearchResultItem>(item =>
    {
        if (item != null) SelectedSearchResult = item;
    });

    /// <summary>停止当前搜索（搜索中时由界面停止按钮调用）。</summary>
    public ICommand CancelSearchCommand => new RelayCommand(() => CancelSearch());

    /// <summary>欢迎页点击最近文件夹时打开该文件夹。</summary>
    public ICommand OpenRecentFolderCommand => new RelayCommand<string>(path =>
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            LoadFolder(path);
    });

    private async Task InitializeRecentFilesAsync()
    {
        var items = await Task.Run(() => LoadRecentFilesFromDiskCore());
        Dispatcher.UIThread.Post(() =>
        {
            _recentFileItems.Clear();
            foreach (var item in items)
                _recentFileItems.Add(item);
            OnPropertyChanged(nameof(RecentFileItems));
            NotifyRecentDisplayProperties();
        });
    }

    /// <summary>在后台线程读取并解析最近文件列表（LumConfig 持久化，AOT 友好）。</summary>
    private static List<RecentFileItem> LoadRecentFilesFromDiskCore()
    {
        var result = new List<RecentFileItem>();
        try
        {
            var path = Core.AppConfig.RecentFilesPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(path)) return result;
            var cfg = new LumConfigManager(path);
            var pathsObj = cfg.Get("paths");
            var timesObj = cfg.Get("times");
            if (pathsObj is IList pathList && pathList.Count > 0)
            {
                IList? timeList = timesObj as IList;
                for (int i = 0; i < pathList.Count; i++)
                {
                    var p = pathList[i]?.ToString();
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var fullPath = Path.GetFullPath(p);
                    var time = DateTime.UtcNow;
                    var timeStr = timeList != null && i < timeList.Count ? timeList[i]?.ToString() : null;
                    if (!string.IsNullOrEmpty(timeStr) &&
                        DateTime.TryParse(timeStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        time = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
                    long? size = null;
                    try { if (File.Exists(fullPath)) size = new FileInfo(fullPath).Length; } catch { }
                    result.Add(new RecentFileItem(fullPath, time, size));
                }
                result = result.OrderByDescending(x => x.LastOpenTimeUtc).Take(MaxRecentFiles).ToList();
                return result;
            }
            var legacyObj = cfg.Get("list");
            if (legacyObj is IList legacyList)
            {
                foreach (var item in legacyList)
                {
                    var p = item?.ToString();
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var fullPath = Path.GetFullPath(p);
                    long? size = null;
                    try { if (File.Exists(fullPath)) size = new FileInfo(fullPath).Length; } catch { }
                    result.Add(new RecentFileItem(fullPath, DateTime.UtcNow, size));
                }
                result = result.Take(MaxRecentFiles).ToList();
            }
        }
        catch { }
        return result;
    }

    private void NotifyRecentDisplayProperties()
    {
        OnPropertyChanged(nameof(RecentFileItemsVisible));
        OnPropertyChanged(nameof(RecentFileItemsMore));
        OnPropertyChanged(nameof(HasMoreRecentFiles));
        OnPropertyChanged(nameof(RecentFolderPathsVisible));
        OnPropertyChanged(nameof(RecentFolderPathsMore));
        OnPropertyChanged(nameof(HasMoreRecentFolders));
    }

    private void SaveRecentFilesToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(Core.AppConfig.RecentFilesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var items = _recentFileItems.Take(MaxRecentFiles).ToList();
            var cfg = new LumConfigManager();
            cfg.Set("paths", items.Select(x => x.FullPath).ToArray());
            cfg.Set("times", items.Select(x => x.LastOpenTimeUtc.ToString("O")).ToArray());
            cfg.Save(Core.AppConfig.RecentFilesPath);
        }
        catch { }
    }

    /// <summary>节流保存：延迟执行，短时间内多次调用只触发一次写入。</summary>
    private void ScheduleSaveRecentFiles()
    {
        _saveRecentFilesTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveRecentFilesTimer.Stop();
        _saveRecentFilesTimer.Tick -= OnSaveRecentFilesTimerTick;
        _saveRecentFilesTimer.Tick += OnSaveRecentFilesTimerTick;
        _saveRecentFilesTimer.Start();
    }

    private void OnSaveRecentFilesTimerTick(object? sender, EventArgs e)
    {
        _saveRecentFilesTimer?.Stop();
        SaveRecentFilesToDisk();
    }

    private static void LoadRecentFoldersFromDisk(ObservableCollection<string> target)
    {
        try
        {
            var path = Core.AppConfig.RecentFoldersPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(path)) return;
            var cfg = new LumConfigManager(path);
            var listObj = cfg.Get("list");
            if (listObj is not IList list) return;
            target.Clear();
            foreach (var item in list)
            {
                var p = item?.ToString();
                if (string.IsNullOrWhiteSpace(p)) continue;
                target.Add(Path.GetFullPath(p));
            }
        }
        catch { }
    }

    private static void SaveRecentFoldersToDisk(IList<string> paths)
    {
        try
        {
            var dir = Path.GetDirectoryName(Core.AppConfig.RecentFoldersPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var cfg = new LumConfigManager();
            cfg.Set("list", paths.Take(MaxRecentFolders).ToArray());
            cfg.Save(Core.AppConfig.RecentFoldersPath);
        }
        catch { }
    }

    private void NotifyWelcomeViewChanged()
    {
        OnPropertyChanged(nameof(ShowWelcomeView));
        OnPropertyChanged(nameof(ShowEditorView));
    }

    private void PushRecentFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var normalized = Path.GetFullPath(path);
        for (int i = 0; i < _recentFolderPaths.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(_recentFolderPaths[i]), normalized, StringComparison.OrdinalIgnoreCase))
            {
                _recentFolderPaths.RemoveAt(i);
                _recentFolderPaths.Insert(0, normalized);
                SaveRecentFoldersToDisk(_recentFolderPaths);
                OnPropertyChanged(nameof(RecentFolderPaths));
                NotifyRecentDisplayProperties();
                return;
            }
        }
        _recentFolderPaths.Insert(0, normalized);
        while (_recentFolderPaths.Count > MaxRecentFolders)
            _recentFolderPaths.RemoveAt(_recentFolderPaths.Count - 1);
        SaveRecentFoldersToDisk(_recentFolderPaths);
        OnPropertyChanged(nameof(RecentFolderPaths));
        NotifyRecentDisplayProperties();
    }

    private void PushRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = Path.GetFullPath(path);
        long? size = null;
        try { if (File.Exists(normalized)) size = new FileInfo(normalized).Length; } catch { }
        var now = DateTime.UtcNow;
        for (int i = 0; i < _recentFileItems.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(_recentFileItems[i].FullPath), normalized, StringComparison.OrdinalIgnoreCase))
            {
                _recentFileItems.RemoveAt(i);
                _recentFileItems.Insert(0, new RecentFileItem(normalized, now, size));
                ScheduleSaveRecentFiles();
                OnPropertyChanged(nameof(RecentFileItems));
                NotifyRecentDisplayProperties();
                return;
            }
        }
        _recentFileItems.Insert(0, new RecentFileItem(normalized, now, size));
        while (_recentFileItems.Count > MaxRecentFiles)
            _recentFileItems.RemoveAt(_recentFileItems.Count - 1);
        ScheduleSaveRecentFiles();
        OnPropertyChanged(nameof(RecentFileItems));
        NotifyRecentDisplayProperties();
    }

    /// <summary>
    /// 统一入口：记录一次“手动焦点跳转”，将当前位置压入后退栈，并清空前进栈。
    /// 所有非 Alt+Left/Right 的导航（点击标签、搜索结果、编辑器内大跳转等）都应通过此方法写入历史。
    /// </summary>
    public void RecordLocation(string path, int offset)
    {
        if (string.IsNullOrEmpty(path)) return;
        _focusBackStack.Push((path, offset));
        _focusForwardStack.Clear();
    }

    /// <summary>Alt+Left：后退到上一焦点。返回 (目标 path, 目标 offset)，若无法后退则 path 为 null。</summary>
    public (string? path, int offset) GoBack(string currentPath, int currentOffset)
    {
        if (_focusBackStack.Count == 0) return (null, 0);
        _focusForwardStack.Push((currentPath, currentOffset));
        var (path, offset) = _focusBackStack.Pop();
        return (path, offset);
    }

    /// <summary>Alt+Right：前进到下一焦点。</summary>
    public (string? path, int offset) GoForward(string currentPath, int currentOffset)
    {
        if (_focusForwardStack.Count == 0) return (null, 0);
        _focusBackStack.Push((currentPath, currentOffset));
        var (path, offset) = _focusForwardStack.Pop();
        return (path, offset);
    }

    public EditorLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (SetProperty(ref _layoutMode, value))
            {
                // 将当前布局模式写回配置，供下次启动时恢复
                Config.Ui.LayoutMode = value.ToString();
                OnPropertyChanged(nameof(ShowEditor));
                OnPropertyChanged(nameof(ShowPreview));                
            }
        }
    }

    /// <summary>启动时尝试恢复上一次会话：优先恢复最近文件，其次恢复最近文件夹。</summary>
    private void TryRestoreLastSession()
    {
        try
        {
            // 1) 优先：最近打开的文件（按时间倒序）
            var lastFile = _recentFileItems
                .OrderByDescending(x => x.LastOpenTimeUtc)
                .FirstOrDefault();
            if (lastFile != null && File.Exists(lastFile.FullPath))
            {
                var dir = Path.GetDirectoryName(lastFile.FullPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    LoadFolder(dir);
                }
                OpenDocument(lastFile.FullPath);
                return;
            }

            // 2) 其次：最近打开的文件夹
            var lastFolder = _recentFolderPaths.FirstOrDefault();
            if (!string.IsNullOrEmpty(lastFolder) && Directory.Exists(lastFolder))
            {
                LoadFolder(lastFolder);
            }
        }
        catch
        {
            // 恢复失败不影响启动，静默忽略
        }
    }

    public bool ShowEditor => _layoutMode is EditorLayoutMode.Both or EditorLayoutMode.EditorOnly;
    public bool ShowPreview => _layoutMode is EditorLayoutMode.Both or EditorLayoutMode.PreviewOnly;

    public bool IsExplorerActive
    {
        get => _isExplorerActive;
        set
        {
            if (SetProperty(ref _isExplorerActive, value))
            {
                OnPropertyChanged(nameof(ShowExplorerPane));
                if (value)
                {
                    IsSearchActive = false;
                    IsSettingsActive = false;
                    IsGitActive = false;
                }
            }
        }
    }

    public bool IsSearchActive
    {
        get => _isSearchActive;
        set
        {
            if (SetProperty(ref _isSearchActive, value))
            {
                OnPropertyChanged(nameof(ShowSearchPane));
                if (value)
                {
                    IsExplorerActive = false;
                    IsSettingsActive = false;
                    IsGitActive = false;
                }
            }
        }
    }

    public bool IsSettingsActive
    {
        get => _isSettingsActive;
        set
        {
            if (SetProperty(ref _isSettingsActive, value))
            {
                OnPropertyChanged(nameof(ShowSettingsPane));
                if (value)
                {
                    IsExplorerActive = false;
                    IsSearchActive = false;
                    IsGitActive = false;
                }
            }
        }
    }

    public bool IsGitActive
    {
        get => _isGitActive;
        set
        {
            if (SetProperty(ref _isGitActive, value))
            {
                OnPropertyChanged(nameof(ShowGitPane));
                if (value)
                {
                    IsExplorerActive = false;
                    IsSearchActive = false;
                    IsSettingsActive = false;
                }
            }
        }
    }

    public bool ShowExplorerPane => IsExplorerActive;
    public bool ShowSearchPane => IsSearchActive;
    public bool ShowSettingsPane => IsSettingsActive;
    public bool ShowGitPane => IsGitActive;

    /// <summary>左侧文件栏是否已折叠为侧边窄条；设置时持久化。</summary>
    public bool IsSidebarCollapsed
    {
        get => Config.Ui.SidebarCollapsed;
        set
        {
            if (Config.Ui.SidebarCollapsed == value) return;
            Config.Ui.SidebarCollapsed = value;
            Config.Save(Core.AppConfig.DefaultConfigPath);
            OnPropertyChanged(nameof(IsSidebarCollapsed));
            OnPropertyChanged(nameof(ShowSidebarCollapseIcon));
        }
    }

    /// <summary>折叠条在展开状态下是否处于 hover（用于显示“收起”图标）。由视图在 PointerEntered/Exited 时设置。</summary>
    public bool IsSidebarCollapseStripHovered
    {
        get => _isSidebarCollapseStripHovered;
        set
        {
            if (SetProperty(ref _isSidebarCollapseStripHovered, value))
                OnPropertyChanged(nameof(ShowSidebarCollapseIcon));
        }
    }
    private bool _isSidebarCollapseStripHovered;

    /// <summary>展开时且折叠条 hover 时显示收起三角。</summary>
    public bool ShowSidebarCollapseIcon => !IsSidebarCollapsed && IsSidebarCollapseStripHovered;

    /// <summary>工作区展示与搜索共用的文件类型（当前仅 .md、.txt）。</summary>
    private static readonly string[] FolderFilePatterns = ["*.md", "*.txt"];

    public void LoadFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        CancelSearch();
        ClearSearchResults();
        _workspaceFolderPaths.Clear();
        _workspaceFolderPaths.Add(Path.GetFullPath(path));
        _documentFolder = path;
        OnPropertyChanged(nameof(DocumentFolder));
        OnPropertyChanged(nameof(HasWorkspaceOpen));
        OnPropertyChanged(nameof(ShowWorkspacePlaceholder));
        CloseAllDocumentsAndClearCache();
        RescanWorkspaceDocuments();
        BuildFileTreeFromWorkspace();
        FilterDocuments();
        PushRecentFolder(path);
        NotifyWelcomeViewChanged();
    }

    /// <summary>添加多个文件夹到工作区；新根默认折叠。</summary>
    public void AddFoldersToWorkspace(IEnumerable<string> paths)
    {
        CancelSearch();
        ClearSearchResults();
        var added = false;
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            if (!Directory.Exists(full)) continue;
            if (_workspaceFolderPaths.Contains(full)) continue;
            _workspaceFolderPaths.Add(full);
            added = true;
        }
        if (!added) return;
        if (_documentFolder == "")
            _documentFolder = _workspaceFolderPaths[0];
        OnPropertyChanged(nameof(DocumentFolder));
        OnPropertyChanged(nameof(HasWorkspaceOpen));
        OnPropertyChanged(nameof(ShowWorkspacePlaceholder));
        RescanWorkspaceDocuments();
        BuildFileTreeFromWorkspace();
        FilterDocuments();
        NotifyWelcomeViewChanged();
    }

    /// <summary>关闭所有文档并清空缓存，然后加载指定工作区根目录列表并重建文件树（每个根默认折叠）。</summary>
    public void CloseAllAndLoadWorkspace(IEnumerable<string> rootPaths)
    {
        var list = rootPaths.Select(Path.GetFullPath).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        CancelSearch();
        ClearSearchResults();
        _workspaceFolderPaths.Clear();
        _workspaceFolderPaths.AddRange(list);
        _documentFolder = list[0];
        OnPropertyChanged(nameof(DocumentFolder));
        OnPropertyChanged(nameof(HasWorkspaceOpen));
        OnPropertyChanged(nameof(ShowWorkspacePlaceholder));
        CloseAllDocumentsAndClearCache();
        RescanWorkspaceDocuments();
        BuildFileTreeFromWorkspace();
        FilterDocuments();
        NotifyWelcomeViewChanged();
    }

    /// <summary>关闭所有已打开文档并清空内容缓存（工作区切换时调用）。</summary>
    private void CloseAllDocumentsAndClearCache()
    {
        _openDocuments.Clear();
        _activeDocument = null;
        ClearEditor();
        PreviewImagePath = null;
        foreach (var d in _documents)
        {
            d.CachedMarkdown = null;
            d.IsOpen = false;
        }
        _closedDocumentCache.Clear();
        _documents.Clear();
        _filteredDocuments.Clear();
        _fileTreeRoot.Clear();
        OnPropertyChanged(nameof(OpenDocuments));
        OnPropertyChanged(nameof(Documents));
        OnPropertyChanged(nameof(FilteredDocuments));
    }

    /// <summary>保存当前工作区根目录列表到文件（如 .mdw 或 .json）。</summary>
    /// <summary>保存工作区根目录列表到文件（每行一个路径，UTF-8），避免反射序列化在 AOT/Trim 下报错。</summary>
    public void SaveWorkspaceToFile(string filePath)
    {
        try
        {
            File.WriteAllLines(filePath, _workspaceFolderPaths, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>从文件读取工作区根目录列表（每行一个路径）；失败返回 null。</summary>
    public IReadOnlyList<string>? LoadWorkspaceFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            var list = lines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && Directory.Exists(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>刷新侧栏文件树（由根目录“刷新”按钮等调用）。刷新前保存展开状态与选中项，刷新后恢复；若选中项已不存在则清空选中。</summary>
    public void RefreshFileTree()
    {
        if (_workspaceFolderPaths.Count == 0) return;
        var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpandedPaths(_fileTreeRoot, expandedPaths);
        var selectedPath = _selectedTreeNode?.FullPath;

        RescanWorkspaceDocuments();
        BuildFileTreeFromWorkspace();

        ApplyExpandedPaths(_fileTreeRoot, expandedPaths);
        EnsureExpandedFoldersLoaded(_fileTreeRoot);
        RebuildVisibleFileTree();
        _isRestoringFileTreeSelection = true;
        try
        {
            SelectedTreeNode = FindNodeByPath(_fileTreeRoot, selectedPath);
        }
        finally
        {
            _isRestoringFileTreeSelection = false;
        }

        FilterDocuments();
        // 刷新后 _documents 已重建为新实例，需把已打开文档的内容与状态迁移到新实例，避免右侧编辑区变空白
        MigrateOpenDocumentsAfterRefresh();
        // 同步当前文档引用与选中节点，避免新建文件后点击无反应（内部路径/引用不一致）
        var currentPath = _activeDocument?.FullPath;
        if (!string.IsNullOrEmpty(currentPath))
        {
            var docInList = _documents.FirstOrDefault(d => string.Equals(d.FullPath, currentPath, StringComparison.OrdinalIgnoreCase));
            if (docInList != null && _activeDocument != docInList)
                ActiveDocument = docInList;
        }
    }

    /// <summary>刷新后把 _openDocuments 中的旧 DocumentItem 状态迁移到 _documents 中的新实例，并替换引用，避免编辑区空白。</summary>
    private void MigrateOpenDocumentsAfterRefresh()
    {
        var openList = _openDocuments.ToList();
        var activePath = _activeDocument?.FullPath;
        for (var i = 0; i < openList.Count; i++)
        {
            var oldDoc = openList[i];
            var newDoc = _documents.FirstOrDefault(d => string.Equals(d.FullPath, oldDoc.FullPath, StringComparison.OrdinalIgnoreCase));
            if (newDoc == null) continue;
            newDoc.CachedMarkdown = oldDoc.CachedMarkdown;
            newDoc.IsModified = oldDoc.IsModified;
            newDoc.LastCaretOffset = oldDoc.LastCaretOffset;
            newDoc.LastKnownWriteTimeUtc = oldDoc.LastKnownWriteTimeUtc;
            newDoc.EncodingName = oldDoc.EncodingName;
            newDoc.EditorZoomLevel = oldDoc.EditorZoomLevel;
            newDoc.PreviewZoomLevel = oldDoc.PreviewZoomLevel;
            newDoc.IsOpen = true;
            _openDocuments[i] = newDoc;
        }
        OnPropertyChanged(nameof(OpenDocuments));
        // 若当前活动文档已迁移，指向新实例并通知视图，避免标签选中错乱；不调用 setter 以免 LoadFromDocumentItem 覆盖 _currentMarkdown
        if (!string.IsNullOrEmpty(activePath))
        {
            var newActive = _documents.FirstOrDefault(d => string.Equals(d.FullPath, activePath, StringComparison.OrdinalIgnoreCase));
            if (newActive != null && _activeDocument != newActive)
            {
                _activeDocument = newActive;
                OnPropertyChanged(nameof(ActiveDocument));
            }
        }
    }

    /// <summary>仅扫描各工作区根目录下第一层文件，不递归；子目录内文件在展开时由 LoadChildrenForFolder 按需加入 _documents。</summary>
    private void RescanWorkspaceDocuments()
    {
        _documents.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootPath in _workspaceFolderPaths)
        {
            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root)) continue;
            foreach (var pattern in FolderFilePatterns)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
                    {
                        if (seen.Add(file))
                        {
                            var rel = Path.GetRelativePath(root, file);
                            var doc = new DocumentItem(file, rel) { WorkspaceRoot = root };
                            _documents.Add(doc);
                        }
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>仅构建根节点，并只加载根下第一层；子文件夹在用户展开时再动态加载。</summary>
    private void BuildFileTreeFromWorkspace()
    {
        _fileTreeRoot.Clear();
        foreach (var rootPath in _workspaceFolderPaths)
        {
            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root)) continue;
            var rootName = Path.GetFileName(root);
            if (string.IsNullOrEmpty(rootName)) rootName = root;
            var rootNode = new FileTreeNode(rootName, root, true);
            rootNode.Level = 0;
            rootNode.IsExpanded = true;
            LoadChildrenForFolder(rootNode);
            _fileTreeRoot.Add(rootNode);
        }
        RebuildVisibleFileTree();
    }

    /// <summary>动态加载该文件夹下的直接子目录和文件（仅扫描当前层），并标记已加载；子目录内文件按需加入 _documents。</summary>
    private void LoadChildrenForFolder(FileTreeNode folderNode)
    {
        if (!folderNode.IsFolder || folderNode.ChildrenLoaded) return;
        var path = Path.GetFullPath(folderNode.FullPath);
        if (!Directory.Exists(path)) return;
        try
        {
            var dirs = Directory.GetDirectories(path)
                .Select(Path.GetFullPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var fullDir in dirs)
            {
                var displayName = Path.GetFileName(fullDir);
                if (string.IsNullOrEmpty(displayName)) continue;
                var folderChild = new FileTreeNode(displayName, fullDir, true);
                folderChild.Level = folderNode.Level + 1;
                folderChild.IsExpanded = false;
                folderNode.Children.Add(folderChild);
            }
            var root = GetWorkspaceRootForPath(path);
            foreach (var pattern in FolderFilePatterns)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly))
                    {
                        var fullPath = Path.GetFullPath(file);
                        if (_documents.Any(d => string.Equals(d.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var rel = root != null ? Path.GetRelativePath(root, fullPath) : Path.GetFileName(fullPath);
                        var doc = new DocumentItem(fullPath, rel) { WorkspaceRoot = root };
                        _documents.Add(doc);
                        var fileName = Path.GetFileName(fullPath);
                        if (string.IsNullOrEmpty(fileName)) continue;
                        var fileNode = new FileTreeNode(fileName, fullPath, false);
                        fileNode.Level = folderNode.Level + 1;
                        folderNode.Children.Add(fileNode);
                    }
                }
                catch { }
            }
            SortTreeNodes(folderNode.Children);
        }
        catch { }
        folderNode.ChildrenLoaded = true;
    }

    /// <summary>对树中所有已展开的文件夹执行一次子节点加载（刷新后恢复展开状态时用）。</summary>
    private void EnsureExpandedFoldersLoaded(IEnumerable<FileTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.IsFolder && n.IsExpanded)
            {
                LoadChildrenForFolder(n);
                EnsureExpandedFoldersLoaded(n.Children);
            }
        }
    }

    private static void SortTreeNodes(ObservableCollection<FileTreeNode> nodes)
    {
        var list = nodes.OrderBy(n => n.IsFolder ? 0 : 1).ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        nodes.Clear();
        foreach (var n in list)
        {
            nodes.Add(n);
            SortTreeNodes(n.Children);
        }
    }

    /// <summary>收集当前树中所有已展开文件夹的 FullPath，用于刷新后恢复。</summary>
    private static void CollectExpandedPaths(IEnumerable<FileTreeNode> nodes, HashSet<string> result)
    {
        foreach (var n in nodes)
        {
            if (n.IsFolder && n.IsExpanded)
                result.Add(n.FullPath);
            CollectExpandedPaths(n.Children, result);
        }
    }

    /// <summary>将保存的展开路径应用到新构建的树上。</summary>
    private static void ApplyExpandedPaths(IEnumerable<FileTreeNode> nodes, HashSet<string> expandedPaths)
    {
        foreach (var n in nodes)
        {
            if (n.IsFolder && expandedPaths.Contains(n.FullPath))
                n.IsExpanded = true;
            ApplyExpandedPaths(n.Children, expandedPaths);
        }
    }

    /// <summary>根据当前展开状态重建扁平可见列表；差异同步以减少展开/折叠时的闪烁。</summary>
    private void RebuildVisibleFileTree()
    {
        var newVisible = new List<FileTreeNode>();
        foreach (var root in _fileTreeRoot)
            CollectVisibleNodes(root, newVisible);
        SyncVisibleFileTree(newVisible);
    }

    private static void CollectVisibleNodes(FileTreeNode node, List<FileTreeNode> list)
    {
        list.Add(node);
        if (!node.IsFolder || !node.IsExpanded) return;
        foreach (var child in node.Children)
            CollectVisibleNodes(child, list);
    }

    /// <summary>将 _visibleFileTree 同步为 newVisible，只增删差异部分，减少 UI 闪烁。</summary>
    private void SyncVisibleFileTree(List<FileTreeNode> newVisible)
    {
        var current = _visibleFileTree;
        int i = 0;
        while (i < current.Count && i < newVisible.Count && current[i] == newVisible[i])
            i++;
        if (i == current.Count && i == newVisible.Count)
            return;
        while (current.Count > i)
            current.RemoveAt(current.Count - 1);
        for (int j = i; j < newVisible.Count; j++)
            current.Add(newVisible[j]);
    }

    /// <summary>切换文件夹展开/折叠（整行点击时由视图调用）；展开时若未加载则先动态加载该层子节点。</summary>
    public void ToggleFolderNode(FileTreeNode? node)
    {
        if (node == null || !node.IsFolder) return;
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
            LoadChildrenForFolder(node);
        RebuildVisibleFileTree();
    }

    /// <summary>由视图在“点击重命名框外”时调用：若当前选中节点处于重命名状态则提交并退出重命名，避免因焦点无法移出而卡住。返回 (是否已处理, 错误信息，有错误时由视图弹框)。</summary>
    public (bool Handled, string? ErrorMessage) TryCommitTreeItemRename()
    {
        var node = _selectedTreeNode;
        if (node == null || !node.IsRenaming) return (false, null);
        node.IsRenaming = false;
        var newName = node.EditName?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == node.DisplayName) return (true, null);
        if (node.IsFolder)
        {
            var (_, err) = RenameFolderByPath(node.FullPath, newName);
            return (true, err);
        }
        var (_, errF) = RenameFileByPath(node.FullPath, newName);
        return (true, errF);
    }

    /// <summary>在树中按 FullPath 查找节点；不存在则返回 null。</summary>
    private static FileTreeNode? FindNodeByPath(IEnumerable<FileTreeNode> nodes, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var n in nodes)
        {
            if (string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase))
                return n;
            var found = FindNodeByPath(n.Children, path);
            if (found != null) return found;
        }
        return null;
    }

    public void OnMarkdownChanged()
    {
        IsModified = true;
    }

    public void CloseDocument(DocumentItem? doc)
    {
        if (doc is null) return;
        StopAutoSaveTimer(doc);
        if (ReferenceEquals(_autoSaveDebounceDoc, doc))
        {
            _autoSaveDebounceTimer?.Stop();
            _autoSaveDebounceDoc = null;
        }
        var list = _openDocuments;
        var idx = list.IndexOf(doc);
        if (idx < 0) return;

        // 若为未保存关闭（用户选“不保存”），清除内容缓存，下次打开从磁盘重读。
        if (doc.IsModified)
        {
            doc.CachedMarkdown = null;
            doc.IsModified = false;
        }
        else if (doc.CachedMarkdown != null)
        {
            // 关闭后快照缓存最多保留 10 个，FIFO 淘汰
            var path = doc.FullPath;
            if (!string.IsNullOrEmpty(path))
            {
                while (_closedDocumentCache.Count >= MaxClosedDocumentCache)
                    _closedDocumentCache.RemoveAt(0);
                _closedDocumentCache.Add((path, doc.CachedMarkdown));
            }
            doc.CachedMarkdown = null;
        }

        if (_activeDocument == doc)
        {
            if (list.Count > 1)
            {
                var next = idx > 0 ? list[idx - 1] : list[idx + 1];
                list.RemoveAt(idx);
                OnPropertyChanged(nameof(OpenDocuments));
                NotifyWelcomeViewChanged();
                ActiveDocument = next;
            }
            else
            {
                list.RemoveAt(idx);
                doc.IsOpen = false;
                ActiveDocument = null;
                OnPropertyChanged(nameof(OpenDocuments));
                NotifyWelcomeViewChanged();
                return;
            }
        }
        else
        {
            list.RemoveAt(idx);
        }
        doc.IsOpen = false;
        OnPropertyChanged(nameof(OpenDocuments));
        NotifyWelcomeViewChanged();
    }

    public void SaveCurrent() => TrySaveCurrent();

    /// <summary>另存为：将当前内容按当前编码写入指定路径并切换为当前文档。</summary>
    public void SaveToPath(string path)
    {
        try
        {
            var enc = _activeDocument != null ? GetEncodingByName(_activeDocument.EncodingName) : Encoding.UTF8;
            var textToSave = NormalizeOrderedLists(_currentMarkdown);
            _currentMarkdown = textToSave;
            OnPropertyChanged(nameof(CurrentMarkdown));
            File.WriteAllText(path, textToSave, enc);
            if (string.IsNullOrEmpty(CurrentFilePath) && _activeDocument != null)
            {
                var docToClose = _activeDocument;
                StopAutoSaveTimer(docToClose);
                _autoSaveDebounceTimer?.Stop();
                _autoSaveDebounceDoc = null;
                _openDocuments.Remove(docToClose);
                docToClose.IsOpen = false;
                OnPropertyChanged(nameof(OpenDocuments));
                NotifyWelcomeViewChanged();
            }
            LoadDocument(path);
            IsModified = false;
            if (_activeDocument != null)
            {
                _activeDocument.CachedMarkdown = textToSave;
                _activeDocument.IsModified = false;
                _activeDocument.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(path);
                if (string.Equals(_activeDocument.FullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentMarkdown = textToSave;
                    OnPropertyChanged(nameof(CurrentMarkdown));
                }
            }
        }
        catch { }
    }

    private bool TrySaveCurrent()
    {
        try
        {
            if (!string.IsNullOrEmpty(CurrentFilePath))
            {
                var enc = _activeDocument != null ? GetEncodingByName(_activeDocument.EncodingName) : Encoding.UTF8;
                var textToSave = NormalizeOrderedLists(_currentMarkdown);
                _currentMarkdown = textToSave;
                OnPropertyChanged(nameof(CurrentMarkdown));
                File.WriteAllText(CurrentFilePath, textToSave, enc);
                IsModified = false;
                if (_activeDocument is not null)
                {
                    StopAutoSaveTimer(_activeDocument);
                    _autoSaveDebounceTimer?.Stop();
                    _autoSaveDebounceDoc = null;
                    _activeDocument.CachedMarkdown = textToSave;
                    _activeDocument.IsModified = false;
                    _activeDocument.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(CurrentFilePath);
                }
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    /// <summary>打开并激活指定路径的文档（供视图导航等调用）。</summary>
    public void OpenDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        LoadDocument(path);        
    }

    /// <summary>在指定目录下新建子文件夹并刷新树。返回新文件夹路径，失败返回 null。</summary>
    public string? NewFolderInFolder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return null;
        var dir = Path.GetFullPath(directoryPath);
        var baseName = "新文件夹";
        var path = Path.Combine(dir, baseName);
        var n = 1;
        while (Directory.Exists(path))
            path = Path.Combine(dir, $"{baseName} ({n++})");
        try
        {
            Directory.CreateDirectory(path);
            // 使用统一的扫描/构建逻辑刷新整棵树，确保新建文件夹层级与缩进与磁盘结构严格一致
            RefreshFileTree();
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在指定目录下新建 Markdown 文件并打开。返回新文件路径，失败返回 null。</summary>
    public string? NewFileInFolder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return null;
        var dir = Path.GetFullPath(directoryPath);
        const string baseNameNoExt = "新文档";
        const string ext = ".md";
        var path = Path.Combine(dir, baseNameNoExt + ext);
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(dir, $"{baseNameNoExt} ({n++}){ext}");
        try
        {
            File.WriteAllText(path, "");
            LoadDocument(path);
            RefreshFileTree();
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>关闭并删除指定文件，从列表和树中移除。仅支持文件。返回是否成功。</summary>
    public bool DeleteFileByPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;
        var path = Path.GetFullPath(filePath);
        var doc = _openDocuments.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc != null)
        {
            _openDocuments.Remove(doc);
            doc.IsOpen = false;
            if (_activeDocument == doc)
                ActiveDocument = _openDocuments.FirstOrDefault();
            OnPropertyChanged(nameof(OpenDocuments));
        }
        for (int i = _documents.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_documents[i].FullPath, path, StringComparison.OrdinalIgnoreCase))
                _documents.RemoveAt(i);
        }
        try
        {
            File.Delete(path);
        }
        catch
        {
            return false;
        }
        RefreshFileTree();
        return true;
    }

    /// <summary>删除文件夹及其内部所有内容（文件与子目录）。根目录不可删除。返回是否成功。</summary>
    public bool DeleteFolderByPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return false;
        var path = Path.GetFullPath(folderPath);
        if (IsWorkspaceRoot(path))
            return false;
        for (int i = _openDocuments.Count - 1; i >= 0; i--)
        {
            var doc = _openDocuments[i];
            if (doc.FullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                _openDocuments.RemoveAt(i);
                doc.IsOpen = false;
                if (_activeDocument == doc)
                    ActiveDocument = _openDocuments.FirstOrDefault();
            }
        }
        for (int i = _documents.Count - 1; i >= 0; i--)
        {
            if (_documents[i].FullPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                _documents.RemoveAt(i);
        }
        OnPropertyChanged(nameof(OpenDocuments));
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            return false;
        }
        RefreshFileTree();
        return true;
    }

    /// <summary>重命名文件（仅文件）。新名为完整路径或仅文件名。返回 (新路径, 错误信息)，成功时错误为 null。</summary>
    public (string? NewPath, string? ErrorMessage) RenameFileByPath(string oldFilePath, string newNameOrFullPath)
    {
        if (string.IsNullOrWhiteSpace(oldFilePath) || !File.Exists(oldFilePath))
            return (null, "文件不存在或路径无效。");
        var oldPath = Path.GetFullPath(oldFilePath);
        var dir = Path.GetDirectoryName(oldPath);
        if (string.IsNullOrEmpty(dir)) return (null, "无法确定所在目录。");

        var newPath = Path.IsPathRooted(newNameOrFullPath)
            ? Path.GetFullPath(newNameOrFullPath)
            : Path.Combine(dir, newNameOrFullPath.Trim());
        if (string.IsNullOrWhiteSpace(Path.GetFileName(newPath)))
            return (null, "新文件名不能为空。");
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return (oldPath, null);
        if (File.Exists(newPath)) return (null, "目标位置已存在同名文件。");

        try
        {
            File.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        var openDoc = _openDocuments.FirstOrDefault(d => string.Equals(d.FullPath, oldPath, StringComparison.OrdinalIgnoreCase));
        var newRoot = GetWorkspaceRootForPath(newPath);
        var newRel = newRoot != null ? Path.GetRelativePath(newRoot, newPath) : Path.GetFileName(newPath);
        DocumentItem? newDocRef = null;
        if (openDoc != null)
        {
            var newDoc = new DocumentItem(newPath, newRel)
            {
                WorkspaceRoot = newRoot,
                CachedMarkdown = openDoc.CachedMarkdown,
                IsOpen = true,
                IsModified = openDoc.IsModified,
                LastKnownWriteTimeUtc = openDoc.LastKnownWriteTimeUtc,
                EditorZoomLevel = openDoc.EditorZoomLevel,
                PreviewZoomLevel = openDoc.PreviewZoomLevel,
                EncodingName = openDoc.EncodingName,
                LastCaretOffset = openDoc.LastCaretOffset
            };
            int idx = _openDocuments.IndexOf(openDoc);
            _openDocuments.Remove(openDoc);
            openDoc.IsOpen = false;
            _openDocuments.Insert(idx, newDoc);
            newDocRef = newDoc;
            if (_activeDocument == openDoc)
                ActiveDocument = newDoc;
            OnPropertyChanged(nameof(OpenDocuments));
        }

        for (int i = _documents.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_documents[i].FullPath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                if (newDocRef != null)
                {
                    _documents[i] = newDocRef;
                }
                else
                {
                    _documents[i] = new DocumentItem(newPath, newRel) { WorkspaceRoot = newRoot };
                }
            }
        }

        RefreshFileTree();
        return (newPath, null);
    }

    /// <summary>重命名文件夹。新名为完整路径或仅文件夹名。返回 (新路径, 错误信息)，成功时错误为 null。</summary>
    public (string? NewPath, string? ErrorMessage) RenameFolderByPath(string oldFolderPath, string newNameOrFullPath)
    {
        if (string.IsNullOrWhiteSpace(oldFolderPath) || !Directory.Exists(oldFolderPath))
            return (null, "文件夹不存在或路径无效。");
        var oldPath = Path.GetFullPath(oldFolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDir = Path.GetDirectoryName(oldPath);
        if (string.IsNullOrEmpty(parentDir)) return (null, "无法确定父目录。");

        var newPath = Path.IsPathRooted(newNameOrFullPath)
            ? Path.GetFullPath(newNameOrFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.Combine(parentDir, newNameOrFullPath.Trim());
        if (string.IsNullOrWhiteSpace(Path.GetFileName(newPath)))
            return (null, "新文件夹名不能为空。");
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            return (oldPath, null);
        if (Directory.Exists(newPath)) return (null, "目标位置已存在同名文件夹。");

        try
        {
            Directory.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        var prefix = oldPath + Path.DirectorySeparatorChar;
        var prefixAlt = oldPath + Path.AltDirectorySeparatorChar;
        foreach (var doc in _documents.ToList())
        {
            var fp = doc.FullPath;
            if (fp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || fp.StartsWith(prefixAlt, StringComparison.OrdinalIgnoreCase))
            {
                var rel = fp.Substring(oldPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var newFull = Path.Combine(newPath, rel);
                var root = GetWorkspaceRootForPath(newFull);
                var newRel = root != null ? Path.GetRelativePath(root, newFull) : Path.GetFileName(newFull);
                var newDoc = new DocumentItem(newFull, newRel) { WorkspaceRoot = root };
                var idx = _documents.IndexOf(doc);
                _documents[idx] = newDoc;
                if (_openDocuments.Contains(doc))
                {
                    var openIdx = _openDocuments.IndexOf(doc);
                    _openDocuments.RemoveAt(openIdx);
                    newDoc.IsOpen = true;
                    newDoc.CachedMarkdown = doc.CachedMarkdown;
                    newDoc.IsModified = doc.IsModified;
                    newDoc.LastKnownWriteTimeUtc = doc.LastKnownWriteTimeUtc;
                    newDoc.EncodingName = doc.EncodingName;
                    newDoc.EditorZoomLevel = doc.EditorZoomLevel;
                    newDoc.PreviewZoomLevel = doc.PreviewZoomLevel;
                    newDoc.LastCaretOffset = doc.LastCaretOffset;
                    _openDocuments.Insert(openIdx, newDoc);
                    if (_activeDocument == doc)
                        ActiveDocument = newDoc;
                }
            }
        }
        OnPropertyChanged(nameof(OpenDocuments));
        RefreshFileTree();
        return (newPath, null);
    }

    private static int _untitledCounter;

    /// <summary>
    /// 新建一个空白可编辑窗口（不基于文件）。持久化通过“保存/另存为”再选择路径完成。
    /// 与“在文件管理窗口新建文件”不同：后者会先创建磁盘文件再打开（<see cref="NewFileInFolder"/>）。
    /// </summary>
    public void NewDocument()
    {
        _untitledCounter++;
        var rel = _untitledCounter == 1 ? "未命名" : $"未命名 {_untitledCounter}";
        var doc = new DocumentItem("", rel);
        doc.CachedMarkdown = "";
        doc.IsModified = false;
        doc.IsOpen = true;
        _openDocuments.Add(doc);
        OnPropertyChanged(nameof(OpenDocuments));
        NotifyWelcomeViewChanged();
        ActiveDocument = doc;
    }

    private string? GetWorkspaceRootForPath(string path)
    {
        if (_workspaceFolderPaths.Count == 0) return null;
        var full = Path.GetFullPath(path);
        foreach (var root in _workspaceFolderPaths)
        {
            var r = Path.GetFullPath(root);
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return _workspaceFolderPaths[0];
    }

    /// <summary>指定路径是否为工作区根目录之一（根目录不可删除）。</summary>
    public bool IsWorkspaceRoot(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || _workspaceFolderPaths.Count == 0) return false;
        var normalized = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var root in _workspaceFolderPaths)
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, r, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void LoadDocument(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            PushRecentFile(path);

        var doc = _documents.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            var root = GetWorkspaceRootForPath(path);
            var rel = root != null ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
            doc = new DocumentItem(path, rel) { WorkspaceRoot = root };
            _documents.Add(doc);
        }

        // 优先从关闭文档缓存恢复（最多 10 个），否则从磁盘读取
        var cacheIdx = _closedDocumentCache.FindIndex(t => string.Equals(t.path, path, StringComparison.OrdinalIgnoreCase));
        if (cacheIdx >= 0)
        {
            var (_, content) = _closedDocumentCache[cacheIdx];
            _closedDocumentCache.RemoveAt(cacheIdx);
            doc.CachedMarkdown = content;
        }

        // 无缓存时在后台读取内容
        if (doc.CachedMarkdown is null && File.Exists(path))
        {
            var docRef = doc;
            var pathCopy = Path.GetFullPath(path);
            Task.Run(() =>
            {
                string text = "";
                DateTime? writeTime = null;
                try
                {
                    var enc = GetEncodingByName(docRef.EncodingName);
                    text = File.ReadAllText(pathCopy, enc);
                    if (File.Exists(pathCopy))
                        writeTime = File.GetLastWriteTimeUtc(pathCopy);
                }
                catch
                {
                    text = "";
                }

                Dispatcher.UIThread.Post(() =>
                {
                    docRef.CachedMarkdown = text;
                    docRef.IsModified = false;
                    if (writeTime.HasValue)
                        docRef.LastKnownWriteTimeUtc = writeTime.Value;

                    if (_activeDocument == docRef)
                    {
                        _currentMarkdown = text;
                        _isModified = false;
                        OnPropertyChanged(nameof(CurrentMarkdown));
                        OnPropertyChanged(nameof(IsModified));
                    }
                });
            });
        }

        if (!_openDocuments.Contains(doc))
        {
            doc.IsOpen = true;
            _openDocuments.Add(doc);
            OnPropertyChanged(nameof(OpenDocuments));
            NotifyWelcomeViewChanged();
        }

        if (File.Exists(path))
            doc.LastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        ActiveDocument = doc;        
    }

    private void ClearEditor()
    {
        _currentMarkdown = "";
        _currentFilePath = "";
        _currentFileName = "";
        _isModified = false;
        _activeDocument = null;
        PreviewImagePath = null;
        SyncDocumentBasePathWithCurrentFile();
        OnPropertyChanged(nameof(CurrentMarkdown));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(CenterTitle));
    }

    private void LoadFromDocumentItem(DocumentItem doc)
    {
        if (IsPreviewableImagePath(doc.FullPath))
        {
            PreviewImagePath = doc.FullPath;
            _currentMarkdown = "";
            _currentFilePath = doc.FullPath;
            _currentFileName = doc.DisplayName;
            _isModified = false;
            var pathKey = doc.FullPath;
            PendingPreviewScrollRatio = _previewScrollRatiosByPath.TryGetValue(pathKey, out var r) ? r : null;
            SyncDocumentBasePathWithCurrentFile();
            OnPropertyChanged(nameof(CurrentMarkdown));
            OnPropertyChanged(nameof(CurrentFilePath));
            OnPropertyChanged(nameof(CurrentFileName));
            OnPropertyChanged(nameof(IsModified));
            OnPropertyChanged(nameof(CenterTitle));
            return;
        }

        PreviewImagePath = null;
        _currentMarkdown = doc.CachedMarkdown ?? "";
        _currentFilePath = doc.FullPath;
        _currentFileName = doc.DisplayName;
        _isModified = doc.IsModified;

        var pathKey2 = string.IsNullOrEmpty(doc.FullPath) ? "untitled:" + doc.DisplayName : doc.FullPath;
        PendingPreviewScrollRatio = _previewScrollRatiosByPath.TryGetValue(pathKey2, out var r2) ? r2 : null;

        SyncDocumentBasePathWithCurrentFile();
        OnPropertyChanged(nameof(CurrentMarkdown));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(CenterTitle));
    }

    private void FilterDocuments()
    {
        _filteredDocuments.Clear();
        var query = SearchQuery.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(query))
        {
            foreach (var d in _documents)
                _filteredDocuments.Add(d);
        }
        else
        {
            foreach (var d in _documents)
            {
                if (d.DisplayName.ToLowerInvariant().Contains(query) ||
                    d.RelativePath.ToLowerInvariant().Contains(query))
                {
                    _filteredDocuments.Add(d);
                }
            }
        }
    }

    /// <summary>参与搜索的单文件最大字节数，超过部分不读取（流式截断）。</summary>
    private const int MaxFileSizeForSearch = 2 * 1024 * 1024;

    /// <summary>每搜索多少文件后强制提交一批结果（与满批结果一起触发更新）。</summary>
    private const int SearchFileBatchSize = 100;

    /// <summary>停止当前搜索任务，已显示的结果保留；仅在新搜索或关键词/工作区变更时清空。</summary>
    public void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        IsSearching = false;
        SearchResultCount = _searchResults.Count;
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(SearchResultCount));
        OnPropertyChanged(nameof(SearchResultCountText));
        OnPropertyChanged(nameof(SearchResultStatusText));
    }

    /// <summary>清空搜索结果（工作区切换时调用；新搜索时由 DoSearch 内清空）。</summary>
    private void ClearSearchResults()
    {
        _searchResults.Clear();
        _searchResultGroups.Clear();
        _flatSearchResultRows.Clear();
        SearchResultCount = 0;
        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(SearchResultGroups));
        OnPropertyChanged(nameof(FlatSearchResultRows));
        OnPropertyChanged(nameof(SearchResultCount));
        OnPropertyChanged(nameof(SearchResultCountText));
        OnPropertyChanged(nameof(SearchResultStatusText));
    }

    private void StartSearchLoadingDotsTimer()
    {
        StopSearchLoadingDotsTimer();
        _searchLoadingDots = ".";
        OnPropertyChanged(nameof(SearchResultStatusText));
        _searchLoadingDotsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _searchLoadingDotsTimer.Tick += (_, _) =>
        {
            _searchLoadingDots = _searchLoadingDots switch { "." => "..", ".." => "...", "..." => "....", _ => "." };
            OnPropertyChanged(nameof(SearchResultStatusText));
        };
        _searchLoadingDotsTimer.Start();
    }

    private void StopSearchLoadingDotsTimer()
    {
        _searchLoadingDotsTimer?.Stop();
        _searchLoadingDotsTimer = null;
    }

    /// <summary>全工作区 grep 式搜索：后台线程逐层枚举目录并搜索，大文件流式截断。工作区或关键词变更时可中断。</summary>
    public void DoSearch()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(DoSearch, DispatcherPriority.Normal);
            return;
        }

        // 关键词：先 Trim 再去掉全部空格，再判空
        var q = string.Concat((SearchQuery ?? "").Trim().Where(c => c != ' '));
        if (string.IsNullOrEmpty(q))
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _searchResults.Clear();
            _searchResultGroups.Clear();
            _flatSearchResultRows.Clear();
            IsSearching = false;
            SearchResultCount = 0;
            OnPropertyChanged(nameof(SearchResults));
            OnPropertyChanged(nameof(SearchResultGroups));
            OnPropertyChanged(nameof(FlatSearchResultRows));
            OnPropertyChanged(nameof(SearchResultCount));
            OnPropertyChanged(nameof(SearchResultCountText));
            OnPropertyChanged(nameof(SearchResultStatusText));
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        var roots = _workspaceFolderPaths.ToList();
        if (roots.Count == 0)
        {
            IsSearching = false;
            return;
        }

        _searchResults.Clear();
        _searchResultGroups.Clear();
        _flatSearchResultRows.Clear();
        SearchResultCount = 0;
        IsSearching = true;
        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(SearchResultGroups));
        OnPropertyChanged(nameof(FlatSearchResultRows));
        OnPropertyChanged(nameof(SearchResultCount));
        OnPropertyChanged(nameof(SearchResultCountText));
        OnPropertyChanged(nameof(SearchResultStatusText));

        var queryLower = q.ToLowerInvariant();
        const int resultBatchSize = 5;

        _searchTask = Task.Run(() =>
        {
            var batch = new List<SearchResultGroup>();
            var dirQueue = new Queue<string>();
            foreach (var r in roots)
                if (Directory.Exists(r))
                    dirQueue.Enqueue(r);
            int filesSearched = 0;

            while (dirQueue.Count > 0 && !token.IsCancellationRequested)
            {
                var dir = dirQueue.Dequeue();
                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(dir))
                    {
                        if (token.IsCancellationRequested) return;
                        dirQueue.Enqueue(subDir);
                    }
                }
                catch (DirectoryNotFoundException) { }
                catch (UnauthorizedAccessException) { }

                foreach (var pattern in FolderFilePatterns)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        foreach (var fullPath in Directory.EnumerateFiles(dir, pattern))
                        {
                            if (token.IsCancellationRequested) return;
                            filesSearched++;

                            string text;
                            try
                            {
                                text = ReadTextUpToMaxBytes(fullPath, MaxFileSizeForSearch);
                            }
                            catch { continue; }
                            if (string.IsNullOrEmpty(text)) continue;

                            var fileResults = new List<SearchResultItem>();
                            var lines = text.Split('\n');
                            for (int i = 0; i < lines.Length; i++)
                            {
                                if (token.IsCancellationRequested) return;
                                var line = lines[i];
                                if (line.IndexOf(queryLower, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var preview = line.Trim();
                                    if (preview.Length > 80) preview = preview[..77] + "...";
                                    fileResults.Add(new SearchResultItem(fullPath, i + 1, preview));
                                }
                            }
                            if (fileResults.Count == 0) continue;

                            batch.Add(new SearchResultGroup(fullPath, fileResults.OrderBy(r => r.LineNumber).ToList()));
                            if (batch.Count >= resultBatchSize)
                            {
                                var toPost = batch.ToList();
                                batch.Clear();
                                PostSearchBatch(toPost, token);
                            }
                        }
                    }
                    catch (DirectoryNotFoundException) { }
                    catch (UnauthorizedAccessException) { }
                }

                if (filesSearched > 0 && filesSearched % SearchFileBatchSize == 0 && batch.Count > 0)
                {
                    var toPost = batch.ToList();
                    batch.Clear();
                    PostSearchBatch(toPost, token);
                }
            }

            if (batch.Count > 0)
                PostSearchBatch(batch, token);

            Dispatcher.UIThread.Post(() =>
            {
                if (_searchCts != cts) return;
                IsSearching = false;
                SearchResultCount = _searchResults.Count;
                OnPropertyChanged(nameof(IsSearching));
                OnPropertyChanged(nameof(SearchResultCount));
                OnPropertyChanged(nameof(SearchResultCountText));
                OnPropertyChanged(nameof(SearchResultStatusText));
            });
        }, token).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_searchCts != cts) return;
                IsSearching = false;
                SearchResultCount = _searchResults.Count;
                OnPropertyChanged(nameof(IsSearching));
                OnPropertyChanged(nameof(SearchResultCount));
                OnPropertyChanged(nameof(SearchResultCountText));
                OnPropertyChanged(nameof(SearchResultStatusText));
            });
        }, TaskContinuationOptions.None);
    }

    /// <summary>截断读取时的重叠量（overlap），前段与后段在边界处重叠，避免在行中间切断。</summary>
    private const int SearchTruncateOverlapBytes = 64 * 1024;

    /// <summary>大文件分两段截断读取：先读「前面长度+overlap」，再读「overlap+后面长度」，合并后供搜索。</summary>
    private static string ReadTextUpToMaxBytes(string fullPath, int maxBytes)
    {
        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = fs.Length;
        if (fileLen == 0) return "";
        if (fileLen <= maxBytes)
        {
            var buf = new byte[(int)fileLen];
            int total = 0;
            while (total < buf.Length)
            {
                int n = fs.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total == 0 ? "" : Encoding.UTF8.GetString(buf, 0, total);
        }

        int overlap = SearchTruncateOverlapBytes;
        // 前段：0 ～ maxBytes + overlap
        int lenA = (int)Math.Min(fileLen, maxBytes + overlap);
        var bytesA = new byte[lenA];
        int totalA = 0;
        fs.Position = 0;
        while (totalA < lenA)
        {
            int n = fs.Read(bytesA, totalA, lenA - totalA);
            if (n <= 0) break;
            totalA += n;
        }
        if (totalA == 0) return "";

        int cutA = Math.Min(totalA, maxBytes);
        int safeCutA = FindUtf8SafeCut(bytesA, cutA);
        var seg1 = Encoding.UTF8.GetString(bytesA, 0, safeCutA);
        if (safeCutA < totalA)
        {
            var tail = Encoding.UTF8.GetString(bytesA, safeCutA, totalA - safeCutA);
            int firstNl = tail.IndexOf('\n');
            if (firstNl >= 0) tail = tail.AsSpan(0, firstNl + 1).ToString();
            seg1 += tail;
        }

        // 后段：maxBytes - overlap ～ 文件末尾（overlap + 后面长度）
        long startB = Math.Max(0, maxBytes - overlap);
        int lenB = (int)(fileLen - startB);
        if (lenB <= 0) return seg1;
        var bytesB = new byte[lenB];
        fs.Position = startB;
        int totalB = 0;
        while (totalB < lenB)
        {
            int n = fs.Read(bytesB, totalB, lenB - totalB);
            if (n <= 0) break;
            totalB += n;
        }
        if (totalB == 0) return seg1;

        // 后段中跳过与前段重叠的字节（从 overlap 起才是“后面长度”），并按 UTF-8 边界与首行截断
        int skipB = (int)(maxBytes - startB);
        if (skipB >= totalB) return seg1;
        int startInB = skipB;
        while (startInB < totalB && (bytesB[startInB] & 0xC0) == 0x80)
            startInB++;
        var seg2Raw = Encoding.UTF8.GetString(bytesB, startInB, totalB - startInB);
        int firstNl2 = seg2Raw.IndexOf('\n');
        var seg2 = firstNl2 >= 0 ? seg2Raw[(firstNl2 + 1)..] : seg2Raw;

        return seg1 + seg2;
    }

    /// <summary>在 limit 处按完整 UTF-8 字符边界截断，返回可安全解码的字节长度。</summary>
    private static int FindUtf8SafeCut(byte[] bytes, int limit)
    {
        if (limit <= 0) return 0;
        int len = Math.Min(limit, bytes.Length);
        int i = len - 1;
        while (i >= 0 && (bytes[i] & 0xC0) == 0x80)
            i--;
        if (i < 0) return 0;
        int charLen = bytes[i] < 0x80 ? 1 : bytes[i] < 0xE0 ? 2 : bytes[i] < 0xF0 ? 3 : 4;
        if (i + charLen <= len) return i + charLen;
        return i;
    }

    /// <summary>将一批搜索结果提交到 UI 线程，实现搜索过程中结果与数量的动态更新。</summary>
    private void PostSearchBatch(List<SearchResultGroup> toPost, CancellationToken token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            foreach (var g in toPost)
            {
                _searchResultGroups.Add(g);
                foreach (var r in g.Items)
                    _searchResults.Add(r);
                _flatSearchResultRows.Add(new SearchResultGroupRowViewModel(g));
                foreach (var r in g.Items)
                    _flatSearchResultRows.Add(new SearchResultLineRowViewModel(r, g));
            }
            SearchResultCount = _searchResults.Count;
            OnPropertyChanged(nameof(SearchResults));
            OnPropertyChanged(nameof(SearchResultGroups));
            OnPropertyChanged(nameof(FlatSearchResultRows));
            OnPropertyChanged(nameof(SearchResultCount));
            OnPropertyChanged(nameof(SearchResultCountText));
            OnPropertyChanged(nameof(SearchResultStatusText));
        }, DispatcherPriority.Background);
    }
}

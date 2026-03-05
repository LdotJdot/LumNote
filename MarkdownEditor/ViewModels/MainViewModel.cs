using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MarkdownEditor.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public Core.AppConfig Config { get; } = Core.AppConfig.Load(Core.AppConfig.DefaultConfigPath);
    private string _documentFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private string _searchQuery = "";
    private string _currentMarkdown = "";
    private string _currentFilePath = "";
    private string _currentFileName = "";
    private DocumentItem? _selectedDocument;
    private FileTreeNode? _selectedTreeNode;
    private ObservableCollection<DocumentItem> _documents = [];
    private ObservableCollection<DocumentItem> _filteredDocuments = [];
    private ObservableCollection<DocumentItem> _openDocuments = [];
    private ObservableCollection<FileTreeNode> _fileTreeRoot = [];
    private bool _isModified;
    private EditorLayoutMode _layoutMode = EditorLayoutMode.Both;
    private bool _isExplorerActive = true;
    private bool _isSearchActive;
    private bool _isSettingsActive;
    private bool _isGitActive;

    private DocumentItem? _activeDocument;

    public string DocumentFolder
    {
        get => _documentFolder;
        set => SetProperty(ref _documentFolder, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
                FilterDocuments();
        }
    }

    public string CurrentMarkdown
    {
        get => _currentMarkdown;
        set
        {
            if (SetProperty(ref _currentMarkdown, value) && !string.IsNullOrEmpty(CurrentFilePath))
            {
                IsModified = true;
                if (_activeDocument is not null)
                {
                    _activeDocument.CachedMarkdown = value;
                    _activeDocument.IsModified = true;
                }
            }
        }
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

    public string CurrentFileName
    {
        get => _currentFileName;
        set => SetProperty(ref _currentFileName, value);
    }

    public DocumentItem? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (_selectedDocument == value) return;
            if (_isModified && !string.IsNullOrEmpty(_currentFilePath))
            {
                TrySaveCurrent();
            }
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

    /// <summary>当前活动的文档（与选项卡选中项保持一致）。</summary>
    public DocumentItem? ActiveDocument
    {
        get => _activeDocument;
        set
        {
            if (_activeDocument == value) return;

            if (_isModified && !string.IsNullOrEmpty(_currentFilePath))
            {
                // 保持原有行为：切换前尝试保存当前文档
                TrySaveCurrent();
            }

            _activeDocument = value;
            OnPropertyChanged();

            if (value is null)
            {
                ClearEditor();
            }
            else
            {
                LoadFromDocumentItem(value);
            }
        }
    }

    /// <summary>文件树根节点（VSCode 风格侧边栏用）。</summary>
    public ObservableCollection<FileTreeNode> FileTreeRoot => _fileTreeRoot;

    public FileTreeNode? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (_selectedTreeNode == value) return;
            _selectedTreeNode = value;
            OnPropertyChanged();
            switch (value)
            {
                case { IsFolder: true } folder:
                    // 单击整行切换文件夹展开/折叠
                    folder.IsExpanded = !folder.IsExpanded;
                    break;
                case { IsFolder: false } node:
                    if (_isModified && !string.IsNullOrEmpty(_currentFilePath))
                        TrySaveCurrent();
                    LoadDocument(node.FullPath);
                    break;
            }
        }
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
        }
    }

    public ICommand SaveCommand => new RelayCommand(SaveCurrent);
    public ICommand CloseDocumentCommand => new RelayCommand<DocumentItem>(CloseDocument);

    public EditorLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (SetProperty(ref _layoutMode, value))
            {
                OnPropertyChanged(nameof(ShowEditor));
                OnPropertyChanged(nameof(ShowPreview));
            }
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

    public void LoadFolder(string path)
    {
        if (!Directory.Exists(path)) return;

        DocumentFolder = path;
        _documents.Clear();
        _filteredDocuments.Clear();
        _fileTreeRoot.Clear();
        _openDocuments.Clear();
        _activeDocument = null;
        ClearEditor();

        foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(path, file);
            _documents.Add(new DocumentItem(file, rel));
        }

        BuildFileTree(path);
        FilterDocuments();
    }

    private void BuildFileTree(string rootPath)
    {
        _fileTreeRoot.Clear();
        var rootMap = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in _documents)
        {
            var segments = doc.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Length == 0) continue;

            ObservableCollection<FileTreeNode> parentChildren = _fileTreeRoot;
            string currentPath = rootPath;

            for (int i = 0; i < segments.Length; i++)
            {
                bool isLast = i == segments.Length - 1;
                currentPath = Path.Combine(currentPath, segments[i]);

                if (isLast)
                {
                    var fileNode = new FileTreeNode(segments[i], doc.FullPath, false);
                    parentChildren.Add(fileNode);
                    continue;
                }

                if (!rootMap.TryGetValue(currentPath, out var folderNode))
                {
                    folderNode = new FileTreeNode(segments[i], currentPath, true);
                    rootMap[currentPath] = folderNode;
                    parentChildren.Add(folderNode);
                }
                parentChildren = folderNode.Children;
            }
        }

        SortTreeNodes(_fileTreeRoot);
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

    public void OnMarkdownChanged()
    {
        IsModified = true;
    }

    public void CloseDocument(DocumentItem? doc)
    {
        if (doc is null) return;
        var list = _openDocuments;
        var idx = list.IndexOf(doc);
        if (idx < 0) return;
        if (_activeDocument == doc)
        {
            if (list.Count > 1)
            {
                var next = idx > 0 ? list[idx - 1] : list[idx + 1];
                list.RemoveAt(idx);
                ActiveDocument = next;
            }
            else
            {
                list.RemoveAt(idx);
                ActiveDocument = null;
            }
        }
        else
        {
            list.RemoveAt(idx);
        }
        doc.IsOpen = false;
    }

    public void SaveCurrent() => TrySaveCurrent();

    private bool TrySaveCurrent()
    {
        try
        {
            if (!string.IsNullOrEmpty(CurrentFilePath))
            {
                File.WriteAllText(CurrentFilePath, CurrentMarkdown);
                IsModified = false;
                if (_activeDocument is not null)
                {
                    _activeDocument.CachedMarkdown = CurrentMarkdown;
                    _activeDocument.IsModified = false;
                }
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private void LoadDocument(string path)
    {
        // 在所有文档列表中查找对应项，没有则创建一个挂载到当前文件夹下的 DocumentItem。
        var doc = _documents.FirstOrDefault(d => string.Equals(d.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            var rel = string.IsNullOrEmpty(DocumentFolder)
                ? Path.GetFileName(path)
                : Path.GetRelativePath(DocumentFolder, path);
            doc = new DocumentItem(path, rel);
            _documents.Add(doc);
        }

        // 首次打开时从磁盘读取内容；后续切换使用缓存，以便多标签快速切换。
        if (doc.CachedMarkdown is null)
        {
            try
            {
                doc.CachedMarkdown = File.ReadAllText(path);
                doc.IsModified = false;
            }
            catch
            {
                doc.CachedMarkdown = "";
                doc.IsModified = false;
            }
        }

        if (!_openDocuments.Contains(doc))
        {
            doc.IsOpen = true;
            _openDocuments.Add(doc);
            OnPropertyChanged(nameof(OpenDocuments));
        }

        ActiveDocument = doc;
    }

    private void ClearEditor()
    {
        _currentMarkdown = "";
        _currentFilePath = "";
        _currentFileName = "";
        _isModified = false;
        _activeDocument = null;
        OnPropertyChanged(nameof(CurrentMarkdown));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(IsModified));
    }

    private void LoadFromDocumentItem(DocumentItem doc)
    {
        _currentMarkdown = doc.CachedMarkdown ?? "";
        _currentFilePath = doc.FullPath;
        _currentFileName = doc.DisplayName;
        _isModified = doc.IsModified;

        OnPropertyChanged(nameof(CurrentMarkdown));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(CurrentFileName));
        OnPropertyChanged(nameof(IsModified));
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
}

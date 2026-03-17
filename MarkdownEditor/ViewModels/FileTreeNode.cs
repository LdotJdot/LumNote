using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MarkdownEditor.ViewModels;

/// <summary>
/// 文件树节点，用于侧边栏 VSCode 风格树形展示。支持文件夹（可折叠）与文件。
/// </summary>
public sealed class FileTreeNode : ViewModelBase
{
    /// <summary>文件夹是否展开。工作区多根时根节点新建后设为 false，子文件夹默认 true。</summary>
    private bool _isExpanded = true;
    private bool _isRenaming;
    private string _editName = "";

    public string DisplayName { get; }
    public string FullPath { get; }
    public bool IsFolder { get; }
    public ObservableCollection<FileTreeNode> Children { get; }

    /// <summary>文件夹是否展开。仅对文件夹有效，用于绑定 TreeViewItem.IsExpanded。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ShowRightArrow));
            OnPropertyChanged(nameof(ShowDownArrow));
        }
    }

    public FileTreeNode(string displayName, string fullPath, bool isFolder)
    {
        DisplayName = displayName;
        FullPath = fullPath;
        IsFolder = isFolder;
        Children = new ObservableCollection<FileTreeNode>();
    }

    /// <summary>是否显示向右箭头（折叠状态）。</summary>
    public bool ShowRightArrow => IsFolder && !IsExpanded;

    /// <summary>是否显示向下箭头（展开状态）。</summary>
    public bool ShowDownArrow => IsFolder && IsExpanded;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"
    };

    /// <summary>节点图标列内容：图片为 🖼️，其他文件为 📄，文件夹为空但保留固定宽度。</summary>
    public string IconText => IsFolder ? "" : (ImageExtensions.Contains(System.IO.Path.GetExtension(FullPath)) ? "🖼️" : "📄");

    /// <summary>是否处于就地重命名状态（显示 TextBox 代替名称）。</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (SetProperty(ref _isRenaming, value))
            {
                OnPropertyChanged(nameof(ShowLabel));
            }
        }
    }

    /// <summary>重命名时编辑中的名称，与 DisplayName 同步进入编辑时。</summary>
    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value ?? "");
    }

    /// <summary>是否显示名称标签（非重命名时）。用于模板中 IsVisible。</summary>
    public bool ShowLabel => !IsRenaming;

}

using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MarkdownEditor.ViewModels;

/// <summary>
/// 文件树节点，用于侧边栏 VSCode 风格树形展示。支持文件夹（可折叠）与文件。
/// </summary>
public sealed class FileTreeNode : ViewModelBase
{
    private bool _isExpanded = true;

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

    /// <summary>节点图标列内容：文件为 📄，文件夹为空但保留固定宽度。</summary>
    public string IconText => IsFolder ? "" : "📄";

}

using System.ComponentModel;

namespace MarkdownEditor.ViewModels;

/// <summary>搜索结果扁平行：用于虚拟化列表，每行要么是“文件组头”，要么是“匹配行”。</summary>
public abstract class SearchResultRowViewModel : INotifyPropertyChanged
{
    public abstract bool IsGroupHeader { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>按文件分组的表头行（文件名、展开箭头）。</summary>
public sealed class SearchResultGroupRowViewModel : SearchResultRowViewModel
{
    public override bool IsGroupHeader => true;
    public SearchResultGroup Group { get; }

    public SearchResultGroupRowViewModel(SearchResultGroup group)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchResultGroup.IsExpanded) || e.PropertyName == nameof(SearchResultGroup.ExpandArrow))
                OnPropertyChanged(e.PropertyName!);
        };
    }

    public string FileName => Group.FileName;
    public string FolderPath => Group.FolderPath;
    public bool HasFolderPath => Group.HasFolderPath;
    public bool IsExpanded => Group.IsExpanded;
    public string ExpandArrow => Group.ExpandArrow;
    public void ToggleExpand() => Group.IsExpanded = !Group.IsExpanded;
}

/// <summary>匹配行（行号 + 预览），可见性跟随所属组的 IsExpanded。</summary>
public sealed class SearchResultLineRowViewModel : SearchResultRowViewModel
{
    public override bool IsGroupHeader => false;
    public SearchResultItem Item { get; }
    public SearchResultGroup Group { get; }

    public SearchResultLineRowViewModel(SearchResultItem item, SearchResultGroup group)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Group = group ?? throw new ArgumentNullException(nameof(group));
        group.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchResultGroup.IsExpanded))
                OnPropertyChanged(nameof(IsVisible));
        };
    }

    public bool IsVisible => Group.IsExpanded;
    public int LineNumber => Item.LineNumber;
    public string LinePreview => Item.LinePreview;
}

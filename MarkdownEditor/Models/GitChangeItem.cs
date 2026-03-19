namespace MarkdownEditor.Models;

/// <summary>版本管理面板中一条变更项（文件路径 + 状态）。</summary>
public sealed class GitChangeItem
{
    public string RelativePath { get; }
    public string FullPath { get; }
    /// <summary>是否已暂存（将纳入下次提交）。</summary>
    public bool IsStaged { get; }
    /// <summary>工作区中是否已修改（相对上次提交或暂存）。</summary>
    public bool IsModified { get; }
    public bool IsNew { get; }
    public bool IsDeleted { get; }
    public bool IsUntracked { get; }

    public GitChangeItem(string relativePath, string fullPath, bool isStaged, bool isModified, bool isNew, bool isDeleted, bool isUntracked)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        IsStaged = isStaged;
        IsModified = isModified;
        IsNew = isNew;
        IsDeleted = isDeleted;
        IsUntracked = isUntracked;
    }

    public string DisplayStatus =>
        IsUntracked ? "未跟踪" :
        IsDeleted ? "已删除" :
        IsNew ? "新文件" :
        IsStaged && IsModified ? "已暂存（有修改）" :
        IsStaged ? "已暂存" :
        IsModified ? "已修改" : "—";

    /// <summary>单字母状态（M=修改 A=新增 D=删除 ?=未跟踪），用于列表左侧着色。</summary>
    public string StatusLetter => IsDeleted ? "D" : (IsNew || IsUntracked) ? "A" : "M";
}

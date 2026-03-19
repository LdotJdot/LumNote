namespace MarkdownEditor.Models;

/// <summary>编辑区 diff 用：当前文档每一行的差异类型（用于红/绿背景）。</summary>
public enum GitDiffLineKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

/// <summary>比对时“已删除行”的占位：显示在当前文档某行之前，用于让删除的内容可见。</summary>
public sealed class GitDiffDeletedLine
{
    /// <summary>在当前文档中的插入位置：显示在 1-based 行号之前；0 表示文档最上方。</summary>
    public int ShowBeforeNewLine { get; }
    public string Content { get; }
    public GitDiffDeletedLine(int showBeforeNewLine, string content)
    {
        ShowBeforeNewLine = showBeforeNewLine;
        Content = content ?? "";
    }
}

/// <summary>行号（1-based）到差异类型的映射；仅包含有变化的行，未出现的行视为 Unchanged。并可携带“删除行”列表供渲染。</summary>
public sealed class GitDiffLineMap
{
    private readonly Dictionary<int, GitDiffLineKind> _map = new();
    private readonly List<GitDiffDeletedLine> _deletedLines = new();

    public void Set(int oneBasedLineNumber, GitDiffLineKind kind)
    {
        _map[oneBasedLineNumber] = kind;
    }

    public GitDiffLineKind Get(int oneBasedLineNumber)
    {
        return _map.TryGetValue(oneBasedLineNumber, out var k) ? k : GitDiffLineKind.Unchanged;
    }

    public void AddDeletedLine(int showBeforeNewLine, string content)
    {
        _deletedLines.Add(new GitDiffDeletedLine(showBeforeNewLine, content));
    }

    public IReadOnlyList<GitDiffDeletedLine> DeletedLines => _deletedLines;

    public bool HasAnyDiff => _map.Count > 0 || _deletedLines.Count > 0;
}

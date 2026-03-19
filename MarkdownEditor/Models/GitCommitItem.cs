namespace MarkdownEditor.Models;

/// <summary>Timeline 中一条提交记录（某文件的历史）。</summary>
public sealed class GitCommitItem
{
    public string ShaShort { get; }
    public string Sha { get; }
    public string MessageShort { get; }
    public string AuthorName { get; }
    public DateTimeOffset Date { get; }

    public GitCommitItem(string shaShort, string sha, string messageShort, string authorName, DateTimeOffset date)
    {
        ShaShort = shaShort;
        Sha = sha;
        MessageShort = messageShort;
        AuthorName = authorName;
        Date = date;
    }
}

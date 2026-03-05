namespace MarkdownEditor.ViewModels;

public sealed class DocumentItem
{
    public string FullPath { get; }
    public string RelativePath { get; }
    public string DisplayName => System.IO.Path.GetFileName(FullPath);

    /// <summary>Tab 标签中使用的文档图标（与文件树保持一致）。</summary>
    public string IconText => "📄";

    /// <summary>
    /// 当前文档在内存中的内容缓存，用于多标签快速切换。
    /// </summary>
    public string? CachedMarkdown { get; set; }

    /// <summary>
    /// 当前文档是否在编辑选项卡中打开。
    /// </summary>
    public bool IsOpen { get; set; }

    /// <summary>
    /// 当前文档是否有未保存修改。
    /// </summary>
    public bool IsModified { get; set; }

    public DocumentItem(string fullPath, string relativePath)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
    }
}

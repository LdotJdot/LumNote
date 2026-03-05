namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 文档源 - 支持大文件、流式、内存映射
/// 零拷贝优先，避免整文档加载
/// </summary>
public interface IDocumentSource
{
    /// <summary>总行数，-1 表示未知（流式）</summary>
    int LineCount { get; }

    /// <summary>获取指定行，从 0 开始</summary>
    ReadOnlySpan<char> GetLine(int index);

    /// <summary>获取行范围 [start, end) 的文本</summary>
    ReadOnlySpan<char> GetLines(int start, int count);

    /// <summary>完整文本（小文档用），大文档可能抛或不实现</summary>
    ReadOnlySpan<char> FullText { get; }

    /// <summary>是否支持随机访问</summary>
    bool SupportsRandomAccess { get; }
}

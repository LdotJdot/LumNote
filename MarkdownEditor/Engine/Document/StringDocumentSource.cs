using System.Runtime.CompilerServices;
using System.Text;

namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 基于字符串的文档源 - 小文档，O(1) 行访问
/// 行偏移表惰性构建，首次访问时建立索引
/// </summary>
public sealed class StringDocumentSource : IDocumentSource
{
    private readonly string _text;
    private int[]? _lineStarts;
    private int _lineCount = -1;

    public StringDocumentSource(string text)
    {
        _text = text ?? "";
    }

    public ReadOnlySpan<char> FullText => _text.AsSpan();

    public bool SupportsRandomAccess => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetLine(int index)
    {
        EnsureLineIndex();
        if (index < 0 || index >= _lineCount) return default;
        int start = _lineStarts![index];
        int end = index + 1 < _lineCount ? _lineStarts[index + 1] : _text.Length;
        // 排除行尾的 \n 和 \r（避免 ParseBlock 拼接时产生多余换行）
        if (end > start && _text[end - 1] == '\n') end--;
        if (end > start && _text[end - 1] == '\r') end--;
        return _text.AsSpan(start, end - start);
    }

    public ReadOnlySpan<char> GetLines(int start, int count)
    {
        EnsureLineIndex();
        if (start < 0 || count <= 0 || start >= _lineCount) return default;
        int endLine = Math.Min(start + count, _lineCount);
        int startPos = _lineStarts![start];
        int endPos = endLine < _lineCount ? _lineStarts[endLine] : _text.Length;
        return _text.AsSpan(startPos, endPos - startPos);
    }

    public int LineCount
    {
        get
        {
            EnsureLineIndex();
            return _lineCount;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureLineIndex()
    {
        if (_lineStarts != null) return;
        var list = new List<int>(Math.Max(256, _text.Length / 40)) { 0 };
        for (int i = 0; i < _text.Length; i++)
        {
            if (_text[i] == '\n')
                list.Add(i + 1);
        }
        _lineStarts = list.ToArray();
        _lineCount = list.Count;
    }
}

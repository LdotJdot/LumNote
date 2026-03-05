namespace MarkdownEditor.Engine;

/// <summary>
/// 选区范围 - 支持跨块
/// </summary>
public readonly record struct SelectionRange(int StartBlock, int StartOffset, int EndBlock, int EndOffset)
{
    public bool IsEmpty => StartBlock == EndBlock && StartOffset == EndOffset;

    public (int block, int start, int end) ForBlock(int blockIndex)
    {
        if (blockIndex < StartBlock || blockIndex > EndBlock) return (blockIndex, 0, 0);
        var start = blockIndex == StartBlock ? StartOffset : 0;
        var end = blockIndex == EndBlock ? EndOffset : int.MaxValue;
        return (blockIndex, start, end);
    }
}

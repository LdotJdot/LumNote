using MarkdownEditor.Core;

namespace MarkdownEditor.Engine.Document;

/// <summary>
/// 增量解析管理器：基于 BlockScanner 和 MarkdownParser 构建顶层块 AST 列表。
/// 当前提供：
/// - ReparseFull：全量重新解析（与旧 RenderEngine.ParseFullDocument 行为一致）；
/// - ReparseRange：基于行区间的局部重解析，只替换受影响块所在的 BlockSpan，其他块保持引用不变。
/// </summary>
internal sealed class IncrementalParseManager
{
    /// <summary>最新一次解析得到的原始块列表（尚未做脚注归一化）。</summary>
    public IReadOnlyList<MarkdownNode?> Blocks => _blocks;

    /// <summary>与 <see cref="Blocks"/> 一一对应的源码行范围 (StartLine, EndLine)。</summary>
    public IReadOnlyList<(int startLine, int endLine)> Ranges => _ranges;

    /// <summary>返回当前已解析块列表的快照，不重新解析。用于仅滚动时复用解析结果、只重算布局。</summary>
    public BlockListSnapshot? GetCurrentSnapshot()
    {
        if (_blocks.Count == 0 || _ranges.Count == 0)
            return null;
        return new BlockListSnapshot(_blocks, _ranges);
    }

    private List<MarkdownNode?> _blocks = [];
    private List<(int startLine, int endLine)> _ranges = [];

    /// <summary>
    /// 对整个文档重新解析，返回最新的块列表快照。
    /// - 使用 BlockScanner 先按行快速划分 BlockSpan；
    /// - 对每个 BlockSpan 提取源码文本并调用 MarkdownParser.Parse；
    /// - 将 DocumentNode.Children 追加到块列表中，每个子节点都继承该 BlockSpan 的行范围。
    /// 注意：不在此处做脚注 FootnoteDef/FootnoteSection 归一化，交由调用方统一处理。
    /// </summary>
    public BlockListSnapshot ReparseFull(IDocumentSource doc)
    {
        var rawBlocks = new List<MarkdownNode?>();
        var rawRanges = new List<(int startLine, int endLine)>();

        int line = 0;
        while (line < doc.LineCount)
        {
            var span = BlockScanner.ScanNextBlock(doc, line);
            if (span.LineCount <= 0)
                break;

            var text = GetSpanText(doc, span);
            var fullDoc = MarkdownParser.Parse(text);

            foreach (var child in fullDoc.Children)
            {
                rawBlocks.Add(child);
                rawRanges.Add((span.StartLine, span.EndLine));
            }

            line = span.EndLine;
        }

        _blocks = rawBlocks;
        _ranges = rawRanges;
        return new BlockListSnapshot(rawBlocks, rawRanges);
    }

    /// <summary>
    /// 基于受影响的源码行区间 [lineStart, lineEnd) 进行局部重解析：
    /// - 找出与该行区间有交集的原有块区间 [firstDirtyBlock, lastDirtyBlock]；
    /// - 以这些块的行范围作为新的扫描窗口，使用 BlockScanner + MarkdownParser 只重建这一小段；
    /// - 其余块直接复用旧的 AST 节点和行范围。
    /// 若当前尚无缓存，或行区间无法可靠映射到块，则退化为 ReparseFull。
    /// 为简化脚注/跨块结构，若脏区间中包含 FootnoteDefNode 或 FootnoteSectionNode，也退化为全量解析。
    /// </summary>
    public BlockListSnapshot ReparseRange(
        IDocumentSource doc,
        int lineStart,
        int lineEnd)
    {
        if (lineStart < 0) lineStart = 0;
        if (lineEnd < lineStart) lineEnd = lineStart;
        if (_blocks.Count == 0 || _ranges.Count == 0)
        {
            return ReparseFull(doc);
        }

        // 1) 找到与 [lineStart, lineEnd) 有交集的块索引区间
        int firstDirty = -1;
        int lastDirty = -1;
        for (int i = 0; i < _ranges.Count; i++)
        {
            var (s, e) = _ranges[i];
            if (e <= lineStart || s >= lineEnd)
                continue;
            if (firstDirty < 0)
                firstDirty = i;
            lastDirty = i;
        }

        // 若没有任何块与该行区间有交集（例如在两个块之间新增了一整段新内容），
        // 为保证正确性先退化为全量解析。
        if (firstDirty < 0 || lastDirty < firstDirty)
        {
            return ReparseFull(doc);
        }

        // 2) 若脏区间包含脚注定义或脚注区，为避免复杂的归一化依赖，直接全量解析。
        for (int i = firstDirty; i <= lastDirty; i++)
        {
            var node = _blocks[i];
            if (node is FootnoteDefNode or FootnoteSectionNode)
                return ReparseFull(doc);
        }

        // 3) 计算需要重新扫描的源码行窗口：覆盖所有脏块的完整行范围。
        var firstRange = _ranges[firstDirty];
        var lastRange = _ranges[lastDirty];
        int scanStartLine = firstRange.startLine;
        int scanEndLine = lastRange.endLine;

        // 4) 使用 BlockScanner 从 scanStartLine 开始重新扫描，直到超出 scanEndLine。
        var newBlocksSegment = new List<MarkdownNode?>();
        var newRangesSegment = new List<(int startLine, int endLine)>();
        int line = scanStartLine;
        while (line < doc.LineCount && line < scanEndLine)
        {
            var span = BlockScanner.ScanNextBlock(doc, line);
            if (span.LineCount <= 0)
                break;

            // 若扫描到的块已经完全超出原脏区行窗口，则停止。
            if (span.StartLine >= scanEndLine)
                break;

            var text = GetSpanText(doc, span);
            var fullDoc = MarkdownParser.Parse(text);
            foreach (var child in fullDoc.Children)
            {
                newBlocksSegment.Add(child);
                newRangesSegment.Add((span.StartLine, span.EndLine));
            }

            line = span.EndLine;
        }

        // 5) 组装新的全量块列表：前段 + 新段 + 后段。
        var mergedBlocks = new List<MarkdownNode?>(_blocks.Count - (lastDirty - firstDirty + 1) + newBlocksSegment.Count);
        var mergedRanges = new List<(int startLine, int endLine)>(mergedBlocks.Capacity);

        // 前段：0 .. firstDirty-1
        for (int i = 0; i < firstDirty; i++)
        {
            mergedBlocks.Add(_blocks[i]);
            mergedRanges.Add(_ranges[i]);
        }

        // 中段：新的解析结果
        mergedBlocks.AddRange(newBlocksSegment);
        mergedRanges.AddRange(newRangesSegment);

        // 后段：lastDirty+1 .. 末尾
        for (int i = lastDirty + 1; i < _blocks.Count; i++)
        {
            mergedBlocks.Add(_blocks[i]);
            mergedRanges.Add(_ranges[i]);
        }

        _blocks = mergedBlocks;
        _ranges = mergedRanges;
        return new BlockListSnapshot(mergedBlocks, mergedRanges);
    }

    /// <summary>
    /// 将给定 BlockSpan 对应的源码行拼接为单个字符串（行间以 \\n 相连）。
    /// </summary>
    private static string GetSpanText(IDocumentSource doc, BlockSpan span)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (i > span.StartLine)
                sb.Append('\n');
            sb.Append(doc.GetLine(i).ToString());
        }
        return sb.ToString();
    }
}


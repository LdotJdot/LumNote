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
    /// <summary>是否将 <c>:name:</c> 短码展开为 emoji（与 <see cref="Engine.EngineConfig.EnableEmojiShortcodes"/> 对齐）。</summary>
    public bool EnableEmojiShortcodes { get; set; } = true;

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
            var fullDoc = MarkdownParser.Parse(text, EnableEmojiShortcodes);

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

        // 2) 计算需要重新扫描的源码行窗口：覆盖所有脏块的完整行范围，且至少覆盖本次请求的 [lineStart, lineEnd)。
        //    插入行时旧块 endLine 可能小于 lineEnd，导致新下移行（如图片）未被扫描而丢失，故取 max。
        var firstRange = _ranges[firstDirty];
        var lastRange = _ranges[lastDirty];
        int scanStartLine = firstRange.startLine;
        int scanEndLine = Math.Max(lastRange.endLine, lineEnd);

        // 3) 使用 BlockScanner 从 scanStartLine 开始重新扫描，直到超出 scanEndLine。
        //    注意：span.EndLine 可能会“跨过”scanEndLine（因为我们以 span.StartLine 作为起点继续解析）。
        //    因此合并替换范围不能只用 scanEndLine，而应基于实际解析覆盖到的 actualScanEndLine。
        var newBlocksSegment = new List<MarkdownNode?>();
        var newRangesSegment = new List<(int startLine, int endLine)>();
        int parsedEmptyLineBlocks = 0;
        int parsedMaxEmptyLineCount = 0;
        int actualScanEndLine = scanStartLine;
        int line = scanStartLine;
        while (line < doc.LineCount && line < scanEndLine)
        {
            var span = BlockScanner.ScanNextBlock(doc, line);
            if (span.LineCount <= 0)
                break;

            // 若扫描到的块已经完全超出原脏区行窗口，则停止。
            if (span.StartLine >= scanEndLine)
                break;

            actualScanEndLine = Math.Max(actualScanEndLine, span.EndLine);

            var text = GetSpanText(doc, span);
            var fullDoc = MarkdownParser.Parse(text, EnableEmojiShortcodes);
            foreach (var child in fullDoc.Children)
            {
                if (child is EmptyLineNode el)
                {
                    parsedEmptyLineBlocks++;
                    parsedMaxEmptyLineCount = Math.Max(parsedMaxEmptyLineCount, el.LineCount);
                }
                newBlocksSegment.Add(child);
                newRangesSegment.Add((span.StartLine, span.EndLine));
            }

            line = span.EndLine;
        }

        // 4) 对齐第 6 步合并替换范围：用 actualScanEndLine 覆盖 span.EndLine 跨越的部分，
        //    避免“解析了额外块内容但没把对应旧块一起替换”，从而在插入行附近留下多余留白/行。
        int lastDirtyExpanded = lastDirty;
        for (int i = lastDirty + 1; i < _ranges.Count; i++)
        {
            var (s, _) = _ranges[i];
            if (s < actualScanEndLine)
                lastDirtyExpanded = i;
            else
                break; // _ranges 按块顺序递增，startLine >= actualScanEndLine 后后续都不会被覆盖
        }

        // 5) 若脏区间（含实际扫描覆盖范围）包含脚注定义或脚注区，为避免复杂的归一化依赖，直接全量解析。
        for (int i = firstDirty; i <= lastDirtyExpanded; i++)
        {
            var node = _blocks[i];
            if (node is FootnoteDefNode or FootnoteSectionNode)
                return ReparseFull(doc);
        }

        // 5.5) 夹在两个 BulletListNode 之间的空行/仅空白段落会渲染成“多一行”留白，合并前过滤掉。
        FilterMiddleEmptyBlocksBetweenLists(newBlocksSegment, newRangesSegment);
        // 5.6) 重扫可能把一段列表拆成多段列表块（中间有空行），合并相邻同类型列表以避免块间留白。
        MergeConsecutiveListBlocks(newBlocksSegment, newRangesSegment);

        // 6) 组装新的全量块列表：前段 + 新段 + 后段。
        //    注意：中段替换的旧块范围使用 lastDirtyExpanded（避免新旧重叠/遗漏）。
        var mergedBlocks = new List<MarkdownNode?>(_blocks.Count - (lastDirtyExpanded - firstDirty + 1) + newBlocksSegment.Count);
        var mergedRanges = new List<(int startLine, int endLine)>(mergedBlocks.Capacity);

        // 前段：0 .. firstDirty-1
        for (int i = 0; i < firstDirty; i++)
        {
            mergedBlocks.Add(_blocks[i]);
            mergedRanges.Add(_ranges[i]);
        }

        // 中段：新的解析结果（可能已被 FilterMiddleEmptyBlocksBetweenLists 收缩）
        mergedBlocks.AddRange(newBlocksSegment);
        mergedRanges.AddRange(newRangesSegment);

        // 后段：lastDirtyExpanded+1 .. 末尾
        for (int i = lastDirtyExpanded + 1; i < _blocks.Count; i++)
        {
            mergedBlocks.Add(_blocks[i]);
            mergedRanges.Add(_ranges[i]);
        }

        // 6.5) 对整份块列表合并相邻的同类型列表块（解决“中间插入后再在末尾插入”仍出现两段列表留白，以及 -/1. 等列表模式）
        MergeConsecutiveListBlocksInFullList(mergedBlocks, mergedRanges);

        _blocks = mergedBlocks;
        _ranges = mergedRanges;
        return new BlockListSnapshot(mergedBlocks, mergedRanges);
    }

    /// <summary>
    /// 仅移除“夹在两个列表块之间”的**单行**空块，避免列表中间误插一行空行时出现留白；
    /// 多行回车（两行及以上空行）保留，以正确渲染段落间距。原地修改两个列表。
    /// </summary>
    private static void FilterMiddleEmptyBlocksBetweenLists(
        List<MarkdownNode?> newBlocksSegment,
        List<(int startLine, int endLine)> newRangesSegment)
    {
        if (newBlocksSegment.Count != newRangesSegment.Count || newBlocksSegment.Count < 3)
            return;

        var keep = new List<int>();
        for (int i = 0; i < newBlocksSegment.Count; i++)
        {
            if (i > 0 && i < newBlocksSegment.Count - 1 &&
                IsListBlock(newBlocksSegment[i - 1]) &&
                IsListBlock(newBlocksSegment[i + 1]) &&
                IsEmptyOrWhitespaceOnlyBlock(newBlocksSegment[i]) &&
                IsSingleLineRange(newRangesSegment[i]) &&
                !IsMultiLineEmptyBlock(newBlocksSegment[i]))
                continue;
            keep.Add(i);
        }

        if (keep.Count == newBlocksSegment.Count)
            return;

        var newBlocks = new List<MarkdownNode?>(keep.Count);
        var newRanges = new List<(int startLine, int endLine)>(keep.Count);
        foreach (int idx in keep)
        {
            newBlocks.Add(newBlocksSegment[idx]);
            newRanges.Add(newRangesSegment[idx]);
        }
        newBlocksSegment.Clear();
        newBlocksSegment.AddRange(newBlocks);
        newRangesSegment.Clear();
        newRangesSegment.AddRange(newRanges);
    }

    private static bool IsListBlock(MarkdownNode? node) =>
        node is BulletListNode or OrderedListNode;

    /// <summary>仅当该块对应源码为单行（或 0 行）时返回 true，用于保留多行空行的渲染。</summary>
    private static bool IsSingleLineRange((int startLine, int endLine) r) =>
        r.endLine - r.startLine <= 1;

    /// <summary>多行空行块（EmptyLineNode.LineCount &gt; 1）一律保留，不参与“夹在两列表之间单行空”的过滤。</summary>
    private static bool IsMultiLineEmptyBlock(MarkdownNode? node) =>
        node is EmptyLineNode el && el.LineCount > 1;

    private static bool IsEmptyOrWhitespaceOnlyBlock(MarkdownNode? node)
    {
        if (node is EmptyLineNode)
            return true;
        if (node is not ParagraphNode p || p.Content == null)
            return false;
        if (p.Content.Count == 0)
            return true;
        for (int i = 0; i < p.Content.Count; i++)
        {
            if (p.Content[i] is not TextNode t || !string.IsNullOrWhiteSpace(t.Content))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 将列表中连续的多个同类型列表块（BulletListNode 或 OrderedListNode）合并为一块，
    /// 避免因中间空行被拆成多段而在渲染时出现块间留白。原地修改两个列表。
    /// </summary>
    private static void MergeConsecutiveListBlocks(
        List<MarkdownNode?> blocks,
        List<(int startLine, int endLine)> ranges)
    {
        if (blocks.Count != ranges.Count || blocks.Count < 2)
            return;

        var outBlocks = new List<MarkdownNode?>();
        var outRanges = new List<(int startLine, int endLine)>();

        int i = 0;
        while (i < blocks.Count)
        {
            if (blocks[i] is BulletListNode firstBullet)
            {
                int rangeStart = ranges[i].startLine;
                int rangeEnd = ranges[i].endLine;
                var allItems = new List<ListItemNode>(firstBullet.Items);
                int j = i + 1;
                while (j < blocks.Count && blocks[j] is BulletListNode nextList)
                {
                    allItems.AddRange(nextList.Items);
                    rangeEnd = ranges[j].endLine;
                    j++;
                }
                outBlocks.Add(new BulletListNode { Items = allItems });
                outRanges.Add((rangeStart, rangeEnd));
                i = j;
                continue;
            }

            if (blocks[i] is OrderedListNode firstOrdered)
            {
                int rangeStart = ranges[i].startLine;
                int rangeEnd = ranges[i].endLine;
                var allItems = new List<ListItemNode>(firstOrdered.Items);
                int j = i + 1;
                while (j < blocks.Count && blocks[j] is OrderedListNode nextList)
                {
                    allItems.AddRange(nextList.Items);
                    rangeEnd = ranges[j].endLine;
                    j++;
                }
                outBlocks.Add(new OrderedListNode { StartNumber = firstOrdered.StartNumber, Items = allItems });
                outRanges.Add((rangeStart, rangeEnd));
                i = j;
                continue;
            }

            outBlocks.Add(blocks[i]);
            outRanges.Add(ranges[i]);
            i++;
        }

        if (outBlocks.Count == blocks.Count)
            return;

        blocks.Clear();
        blocks.AddRange(outBlocks);
        ranges.Clear();
        ranges.AddRange(outRanges);
    }

    /// <summary>
    /// 对整份块列表合并相邻的同类型列表块，解决“中间插入后再在末尾插入”仍出现两段列表留白等问题。
    /// </summary>
    private static void MergeConsecutiveListBlocksInFullList(
        List<MarkdownNode?> blocks,
        List<(int startLine, int endLine)> ranges)
    {
        MergeConsecutiveListBlocks(blocks, ranges);
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


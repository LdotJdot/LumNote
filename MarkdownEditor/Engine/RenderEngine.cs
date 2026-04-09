using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Engine;

/// <summary>
/// 渲染引擎 - 统一入口
/// 文档 → 块扫描 → 解析（含缓存）→ 布局快照 → 渲染
/// </summary>
public sealed class RenderEngine : IDisposable
{
    private readonly EngineConfig _config;
    private readonly ILayoutEngine _layout;
    private readonly SkiaRenderer _renderer;
    private readonly IImageLoader? _imageLoader;
    private float _width;
    private bool _disposed;

    /// <summary>当前文档完整文本的行起始索引（用于从块内偏移换算到全局字符偏移）。</summary>
    private int[]? _fullTextLineStarts;

    private IDocumentSource? _cachedDoc;
    private int _cachedDocVersion;
    private List<MarkdownNode?> _cachedBlocks = [];
    /// <summary>每个块对应的行范围 (StartLine, EndLine)，与 _cachedBlocks 一一对应。</summary>
    private List<(int startLine, int endLine)> _cachedBlockRanges = [];
    private List<LayoutBlock>? _cachedLayouts;
    private float _cachedTotalHeight;
    /// <summary>布局后内容的实际最大宽度（含代码块等长行），用于横向滚动。</summary>
    private float _cachedContentWidth;
    /// <summary>累积 Y[i] = 块 0..i-1 的高度和，Y[0]=0，用于可见区间与 HitTest。</summary>
    private float[] _cumulativeY = [];
    /// <summary>当前增量布局窗口 [Start, End)，用于判断是否需重新构建 _cachedLayouts。</summary>
    private (int start, int end) _layoutWindow = (-1, -1);

    private ViewportTileRasterCache? _tileCache;

    public RenderEngine(float width, EngineConfig? config = null, IImageLoader? imageLoader = null, ILayoutEngine? layout = null, SkiaRenderer? renderer = null)
    {
        _width = Math.Max(1, width);
        _config = config ?? new EngineConfig();
        _imageLoader = imageLoader ?? new DefaultImageLoader();
        _renderer = renderer ?? new SkiaRenderer(_config, _imageLoader);
        _layout = layout ?? new SkiaLayoutEngine(_config, _imageLoader, _renderer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _tileCache?.Dispose();
        }
        catch { }
        _tileCache = null;

        try
        {
            _renderer.InvalidateBlockPictureCache();
        }
        catch { }

        try { _imageLoader?.Dispose(); } catch { }
    }

    /// <summary>
    /// 使用解析得到的块列表快照更新当前块缓存。
    /// 内部会做脚注归一化。可由后台解析任务完成后在 UI 线程调用。
    /// </summary>
    public void ApplyBlocksSnapshot(BlockListSnapshot snapshot, IDocumentSource doc)
    {
        if (snapshot == null || doc == null)
            return;
        if (_disposed)
            return;

        var (normalizedBlocks, normalizedRanges) = NormalizeFootnotes(snapshot.Blocks, snapshot.Ranges);
        _cachedDoc = doc;
        _cachedDocVersion = doc is IVersionedDocumentSource vds ? vds.Version : 0;
        _cachedBlocks = normalizedBlocks;
        _cachedBlockRanges = normalizedRanges;
        _cachedLayouts = null;
        _cachedTotalHeight = 0;
        _cachedContentWidth = 0;
        _layoutWindow = (-1, -1);
        _cumulativeY = [];
        _renderer.PruneBlockPictureCacheBeyondBlockCount(normalizedBlocks.Count);
        _tileCache?.Clear();
    }

    /// <summary>
    /// 使用外部计算好的布局快照替换当前缓存。
    /// 该方法本身不做任何重计算，可由后台任务完成 LayoutBlocksSnapshot 计算后在 UI 线程调用。
    /// </summary>
    public void ApplyLayoutSnapshot(LayoutBlocksSnapshot snapshot)
    {
        if (snapshot == null)
            return;
        if (_disposed)
            return;

        // 将快照中的只读集合转换为内部可变结构，便于后续同步路径继续沿用。
        _cachedLayouts = new List<LayoutBlock>(snapshot.Blocks);

        // 累积高度数组与聚合信息直接替换。
        _cumulativeY = snapshot.CumulativeY is float[] arr
            ? arr
            : snapshot.CumulativeY.ToArray();
        _cachedTotalHeight = snapshot.TotalHeight;
        _cachedContentWidth = snapshot.ContentWidth;
        _layoutWindow = snapshot.LayoutWindow;

        var newCum = _cumulativeY;
        var (ws, we) = snapshot.LayoutWindow;
        if (ws < 0 || we <= ws)
        {
            _renderer.InvalidateBlockPictureCache();
            _tileCache?.Clear();
        }
        else
        {
            _renderer.InvalidateBlockPictureCacheRange(ws, we);
            if (_tileCache != null && newCum != null && newCum.Length >= 2)
                _tileCache.InvalidateDocumentBlockRange(newCum, ws, we);
        }
    }

    /// <summary>获取当前使用的图片加载器，可用于订阅 ImageLoaded 以在图片加载完成后重绘。</summary>
    public IImageLoader? GetImageLoader() => _imageLoader;

    /// <summary>丢弃 Skia 块级录制缓存与视口瓦片缓存（布局变更或异步图片就绪时应调用）。</summary>
    public void InvalidateBlockPictureCache()
    {
        if (_disposed)
            return;
        _renderer.InvalidateBlockPictureCache();
        _tileCache?.Clear();
    }

    /// <summary>获取累积高度数组的副本，供布局任务复用以保持 cum 一致性。若尚未布局则返回 null。</summary>
    internal float[]? GetCumulativeYSnapshot()
    {
        var cum = _cumulativeY;
        if (cum == null || cum.Length < 2)
            return null;
        var copy = new float[cum.Length];
        Array.Copy(cum, copy, cum.Length);
        return copy;
    }

    /// <summary>获取布局引擎，供后台布局任务使用。</summary>
    internal ILayoutEngine GetLayoutEngine() => _layout;

    /// <summary>获取引擎配置，供后台布局任务使用。</summary>
    internal EngineConfig GetConfig() => _config;

    /// <summary>当前内容区域宽度，供调用方判断 SetWidth 是否会清空布局。</summary>
    internal float GetWidth() => _width;

    public void SetWidth(float width)
    {
        if (_disposed)
            return;
        var w = Math.Max(1, width);
        if (Math.Abs(w - _width) > 0.1f)
        {
            _width = w;
            _cachedLayouts = null;
            _layoutWindow = (-1, -1);
            _cumulativeY = [];
            _cachedTotalHeight = 0;
            _renderer.InvalidateBlockPictureCache();
            _tileCache?.Clear();
        }
    }

    /// <summary>
    /// 获取视口内需渲染的块区间（基于真实布局高度）。可多渲染一屏作为缓存。
    /// 使用 _cumulativeY 二分查找，O(log n) 替代 O(n) 线性遍历。
    /// 当 _layoutWindow 为部分布局时，将块索引转换为 _cachedLayouts 内的局部索引。
    /// </summary>
    public (int startBlock, int endBlock) GetVisibleBlockRange(IDocumentSource doc, float scrollY, float viewportHeight)
    {
        if (_cachedLayouts == null || _cachedLayouts.Count == 0)
            return (0, 0);
        var cum = _cumulativeY;
        if (cum == null || cum.Length < 2)
            return (0, 0);
        int n = cum.Length - 1;
        int start = FindFirstVisibleBlockIndexBinary(cum, n, scrollY);
        float limit = scrollY + viewportHeight * 2;
        int end = FindFirstBlockIndexAtOrAboveY(cum, n, limit);
        return ToLayoutIndices(start, end);
    }

    /// <summary>
    /// 获取视口内仅包含“整块”的区间（块底部不超过 scrollY+viewportHeight），用于 PDF 分页避免块被截断。
    /// </summary>
    public (int startBlock, int endBlock) GetVisibleBlockRangeFullBlocksOnly(IDocumentSource doc, float scrollY, float viewportHeight)
    {
        if (_cachedLayouts == null || _cachedLayouts.Count == 0)
            return (0, 0);
        var cum = _cumulativeY;
        if (cum == null || cum.Length < 2)
            return (0, 0);
        int n = cum.Length - 1;
        int start = FindFirstVisibleBlockIndexBinary(cum, n, scrollY);
        float pageBottom = scrollY + viewportHeight;
        int end = start;
        for (int i = start; i < n; i++)
        {
            if (cum[i] >= pageBottom)
                break;
            if (cum[i + 1] <= pageBottom)
                end = i + 1;
        }
        return ToLayoutIndices(start, end);
    }

    /// <summary>将块索引转为 _cachedLayouts 内的索引；若为部分布局则按 _layoutWindow 裁剪并偏移。</summary>
    private (int start, int end) ToLayoutIndices(int blockStart, int blockEnd)
    {
        var (winStart, winEnd) = _layoutWindow;
        if (winStart < 0 || winEnd <= winStart)
            return (blockStart, Math.Min(blockEnd, _cachedLayouts!.Count));
        int start = Math.Max(blockStart, winStart);
        int end = Math.Min(blockEnd, winEnd);
        if (start >= end)
            return (0, 0);
        return (start - winStart, end - winStart);
    }

    /// <summary>二分查找：第一个满足 cum[i+1] >= scrollY 的块索引 i；若均在上方则返回 0。</summary>
    private static int FindFirstVisibleBlockIndexBinary(float[] cum, int blockCount, float scrollY)
    {
        int left = 0, right = blockCount - 1;
        while (left < right)
        {
            int mid = (left + right) >> 1;
            if (cum[mid + 1] >= scrollY)
                right = mid;
            else
                left = mid + 1;
        }
        return left;
    }

    /// <summary>二分查找：最小的 i 使得 cum[i] >= y；若均小于 y 则返回 blockCount。</summary>
    private static int FindFirstBlockIndexAtOrAboveY(float[] cum, int blockCount, float y)
    {
        int left = 0, right = blockCount;
        while (left < right)
        {
            int mid = (left + right) >> 1;
            if (cum[mid] >= y)
                right = mid;
            else
                left = mid + 1;
        }
        return left;
    }

    /// <summary>
    /// 布局并渲染可见块
    /// </summary>
    public void Render(ISkiaRenderContext ctx, IDocumentSource doc, float scrollY, float viewportHeight,
        SelectionRange? selection = null)
    {
        Render(ctx, doc, scrollY, viewportHeight, selection, out _);
    }

    /// <summary>
    /// 布局并渲染可见块；若仅整块分页则传入 fullBlocksOnly=true 并得到 nextScrollY 用于下一页。
    /// </summary>
    public void Render(ISkiaRenderContext ctx, IDocumentSource doc, float scrollY, float viewportHeight,
        SelectionRange? selection, out float nextScrollY, bool fullBlocksOnly = false)
    {
        nextScrollY = scrollY + viewportHeight;
        if (_disposed)
            return;
        // 仅消费布局快照，不再在 UI 线程调用 EnsureLayout
        var layouts = _cachedLayouts;
        if (layouts == null || layouts.Count == 0)
            return;

        var (startBlock, endBlock) = fullBlocksOnly
            ? GetVisibleBlockRangeFullBlocksOnly(doc, scrollY, viewportHeight)
            : GetVisibleBlockRange(doc, scrollY, viewportHeight);
        var count = Math.Max(0, endBlock - startBlock);
        startBlock = Math.Clamp(startBlock, 0, layouts.Count - 1);
        count = Math.Min(count, layouts.Count - startBlock);
        if (count > 0)
            nextScrollY = layouts[startBlock + count - 1].Bounds.Bottom;

        bool tryTiles =
            !fullBlocksOnly
            && _config.EnableViewportTileCache
            && _layoutWindow.start < 0
            && (selection == null || selection.Value.IsEmpty);

        if (tryTiles)
        {
            var tileCache = _tileCache ??= new ViewportTileRasterCache();
            tileCache.Configure(_config.ViewportTileHeightPx, _config.ViewportTileCacheMaxEntries);
            uint bg = _config.PageBackground;
            var bgColor = new SKColor((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg, (byte)(bg >> 24));
            ctx.Canvas.Save();
            ctx.Canvas.Translate(0, -scrollY);
            bool tiledOk = tileCache.TryRenderTiled(
                ctx.Canvas,
                _renderer,
                layouts,
                scrollY,
                viewportHeight,
                _width,
                _cachedContentWidth,
                bgColor,
                ctx.Scale);
            ctx.Canvas.Restore();
            if (tiledOk)
                return;
        }

        ctx.Canvas.Save();
        ctx.Canvas.Translate(0, -scrollY);
        _renderer.Render(ctx, layouts, startBlock, count, selection);
        ctx.Canvas.Restore();
    }

    /// <summary>
    /// 命中测试 - 将文档坐标转为 (blockIndex, charOffset, isSelectable, linkUrl, lineIndexInBlock)
    /// lineIndexInBlock: 命中的布局行在该块内的索引（用于 todo 等按行定位）
    /// </summary>
    public (int blockIndex, int charOffset, bool isSelectable, string? linkUrl, int lineIndexInBlock)? HitTest(IDocumentSource doc, float contentX, float contentY)
    {
        if (_disposed)
            return null;
        if (_cachedLayouts == null)
            return null;

        var cum = _cumulativeY;
        if (cum == null || cum.Length < 2)
            return null;

        int blockCount = cum.Length - 1;
        int bi = FindBlockIndexForContentY(cum, blockCount, contentY);
        if (bi < 0)
            return null;

        LayoutBlock? layoutBlock = null;
        foreach (var lb in _cachedLayouts)
        {
            if (lb.BlockIndex == bi)
            {
                layoutBlock = lb;
                break;
            }
        }
        if (layoutBlock == null)
            return null;

        if (contentY < layoutBlock.Bounds.Top || contentY >= layoutBlock.Bounds.Bottom)
            return null;

        var localX = contentX - layoutBlock.Bounds.Left;
        int lineIdx = FindLineIndexForContentY(layoutBlock, contentY);
        if (lineIdx < 0)
            return null;

        var layoutLine = layoutBlock.Lines[lineIdx];
        var runs = layoutLine.Runs;
        if (runs.Count == 0)
            return (layoutBlock.BlockIndex, 0, false, null, lineIdx);

        LayoutRun? bestRun = null;
        float bestDist = float.MaxValue;
        int offsetInRun = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.Text.Length == 0) continue;
            if (localX >= run.Bounds.Left && localX < run.Bounds.Right)
            {
                float textLeft = run.Style is RunStyle.TableHeaderCell or RunStyle.TableCell
                    ? run.Bounds.Left + 8f
                    : run.Bounds.Left;
                float xInRun = Math.Max(0, localX - textLeft);
                offsetInRun = _layout.MeasureTextOffset(run.Text, xInRun, run.Style);
                return (layoutBlock.BlockIndex, run.CharOffset + offsetInRun, true, run.LinkUrl, lineIdx);
            }
            var distLeft = run.Bounds.Left - localX;
            var distRight = localX - run.Bounds.Right;
            if (localX < run.Bounds.Left && distLeft < bestDist)
            {
                bestDist = Math.Abs(distLeft);
                bestRun = run;
                offsetInRun = 0;
            }
            else if (localX >= run.Bounds.Right && distRight < bestDist)
            {
                bestDist = Math.Abs(distRight);
                bestRun = run;
                offsetInRun = run.Text.Length;
            }
        }
        if (bestRun != null)
            return (layoutBlock.BlockIndex, bestRun.Value.CharOffset + offsetInRun, true, null, lineIdx);
        return (layoutBlock.BlockIndex, runs[0].CharOffset, runs[0].Text.Length > 0, null, lineIdx);
    }

    /// <summary>文档 Y 落在哪一块：cum[i] 为块 i 顶部，块区间为 [cum[i], cum[i+1])。</summary>
    private static int FindBlockIndexForContentY(float[] cum, int blockCount, float contentY)
    {
        if (blockCount <= 0 || contentY < cum[0] || contentY >= cum[blockCount])
            return -1;

        int lo = 0, hi = blockCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (cum[mid] <= contentY)
                lo = mid;
            else
                hi = mid - 1;
        }
        if (contentY >= cum[lo + 1])
            return -1;
        return lo;
    }

    /// <summary>在块内按行 Y 二分，找到包含 contentY 的行索引。</summary>
    private static int FindLineIndexForContentY(LayoutBlock layoutBlock, float contentY)
    {
        var lines = layoutBlock.Lines;
        if (lines.Count == 0)
            return -1;

        int lo = 0, hi = lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            float lineTop = layoutBlock.Bounds.Top + lines[mid].Y;
            if (lineTop <= contentY)
                lo = mid;
            else
                hi = mid - 1;
        }

        float t = layoutBlock.Bounds.Top + lines[lo].Y;
        float b = t + lines[lo].Height;
        if (contentY >= t && contentY < b)
            return lo;
        return -1;
    }

    /// <summary>
    /// 基于 doc.FullText 构建行起始索引，便于从 (line, column) 或块内偏移转换到全局字符偏移。
    /// 仅在文档变更时重建一次。
    /// </summary>
    private void EnsureLineIndex(IDocumentSource doc)
    {
        if (!doc.SupportsRandomAccess)
            return;
        if (_fullTextLineStarts != null && ReferenceEquals(doc, _cachedDoc))
            return;

        var text = doc.FullText;
        if (text.IsEmpty)
        {
            _fullTextLineStarts = new[] { 0 };
            return;
        }

        int estLines = 1 + (text.Length >> 6);
        var list = new List<int>(Math.Max(64, estLines)) { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                list.Add(i + 1);
        }
        _fullTextLineStarts = list.ToArray();
    }

    /// <summary>将原始块快照做脚注归一化，供布局计算使用。</summary>
    public static (IReadOnlyList<MarkdownNode?> blocks, IReadOnlyList<(int startLine, int endLine)> ranges) NormalizeBlockSnapshot(BlockListSnapshot raw)
    {
        if (raw == null || raw.Count == 0)
            return ([], []);
        return NormalizeFootnotes(raw.Blocks, raw.Ranges);
    }

    private static (List<MarkdownNode?> blocks, List<(int startLine, int endLine)> ranges) NormalizeFootnotes(
        IReadOnlyList<MarkdownNode?> blocks,
        IReadOnlyList<(int startLine, int endLine)> ranges)
    {
        var defs = new Dictionary<string, FootnoteDefNode>(StringComparer.Ordinal);
        var refOrder = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is FootnoteDefNode def && !defs.ContainsKey(def.Id))
                defs[def.Id] = def;
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is FootnoteDefNode)
                continue;
            CollectFootnoteRefs(blocks[i], refOrder, seen);
        }

        if (refOrder.Count == 0)
            return (new List<MarkdownNode?>(blocks), new List<(int startLine, int endLine)>(ranges));

        var numberById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < refOrder.Count; i++)
            numberById[refOrder[i]] = i + 1;

        var newBlocks = new List<MarkdownNode?>(blocks.Count + 1);
        var newRanges = new List<(int startLine, int endLine)>(ranges.Count + 1);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b is FootnoteDefNode)
                continue;
            newBlocks.Add(ReplaceFootnoteRefs(b, numberById));
            if (i < ranges.Count)
                newRanges.Add(ranges[i]);
            else
                newRanges.Add((0, 0));
        }

        var items = new List<FootnoteEntry>(refOrder.Count);
        foreach (var id in refOrder)
        {
            var num = numberById[id];
            List<MarkdownNode> content;
            if (defs.TryGetValue(id, out var def))
            {
                content = new List<MarkdownNode>(def.Content.Count);
                foreach (var n in def.Content)
                {
                    var replaced = ReplaceFootnoteRefs(n, numberById);
                    if (replaced != null)
                        content.Add(replaced);
                }
                if (content.Count == 0)
                    content = [new ParagraphNode { Content = [new TextNode { Content = "" }] }];
            }
            else
            {
                content = [new ParagraphNode { Content = [new TextNode { Content = "(缺少脚注定义)" }] }];
            }
            items.Add(new FootnoteEntry { Id = id, Number = num, Content = content });
        }

        newBlocks.Add(new FootnoteSectionNode { Items = items });
        newRanges.Add((0, 0));
        return (newBlocks, newRanges);
    }

    private static void CollectFootnoteRefs(MarkdownNode? node, List<string> refOrder, HashSet<string> seen)
    {
        if (node == null) return;
        switch (node)
        {
            case ParagraphNode p:
                CollectFootnoteRefsFromInlines(p.Content, refOrder, seen);
                break;
            case HeadingNode h:
                CollectFootnoteRefsFromInlines(h.Content, refOrder, seen);
                break;
            case BlockquoteNode bq:
                foreach (var c in bq.Children) CollectFootnoteRefs(c, refOrder, seen);
                break;
            case BulletListNode bl:
                foreach (var it in bl.Items) CollectFootnoteRefs(it, refOrder, seen);
                break;
            case OrderedListNode ol:
                foreach (var it in ol.Items) CollectFootnoteRefs(it, refOrder, seen);
                break;
            case ListItemNode li:
                foreach (var c in li.Content) CollectFootnoteRefs(c, refOrder, seen);
                break;
            case DefinitionListNode dl:
                foreach (var item in dl.Items)
                    foreach (var def in item.Definitions)
                        CollectFootnoteRefs(def, refOrder, seen);
                break;
            case DefinitionItemNode di:
                CollectFootnoteRefsFromInlines(di.Term, refOrder, seen);
                foreach (var def in di.Definitions) CollectFootnoteRefs(def, refOrder, seen);
                break;
        }
    }

    /// <summary>
    /// 将块内字符偏移转换为整个 Markdown 文本中的全局字符偏移。
    /// </summary>
    public int? GetDocumentOffsetForBlockPosition(IDocumentSource doc, int blockIndex, int charOffsetInBlock)
    {
        EnsureLineIndex(doc);
        if (_cachedLayouts == null || _cachedBlockRanges.Count == 0 || _fullTextLineStarts == null)
            return null;
        if (blockIndex < 0 || blockIndex >= _cachedBlockRanges.Count)
            return null;

        var (startLine, _) = _cachedBlockRanges[blockIndex];
        if (startLine < 0 || startLine >= _fullTextLineStarts.Length)
            return null;

        int blockStart = _fullTextLineStarts[startLine];
        return blockStart + Math.Max(0, charOffsetInBlock);
    }

    /// <summary>
    /// 根据全局字符偏移反查所属块索引（顶层 Block 列表中的下标）。
    /// 利用全文行起始索引与每块的行范围实现 offset → Block 的快速映射。
    /// </summary>
    public int? GetBlockIndexForDocumentOffset(IDocumentSource doc, int documentOffset)
    {
        EnsureLineIndex(doc);
        if (_cachedBlockRanges.Count == 0 || _fullTextLineStarts == null)
            return null;
        if (documentOffset < 0 || documentOffset > doc.FullText.Length)
            return null;

        // 1) 通过行起始索引二分查找出所在行号
        var starts = _fullTextLineStarts;
        int line = Array.BinarySearch(starts, documentOffset);
        if (line < 0)
        {
            line = ~line - 1;
            if (line < 0)
                line = 0;
        }
        if (line >= doc.LineCount)
            line = doc.LineCount - 1;

        // 2) 在线号所属的块范围中查找第一个满足 startLine <= line < endLine 的块
        for (int i = 0; i < _cachedBlockRanges.Count; i++)
        {
            var (startLine, endLine) = _cachedBlockRanges[i];
            if (line >= startLine && line < endLine)
                return i;
        }

        return null;
    }

    /// <summary>
    /// 将全文中的字符偏移映射到预览内容坐标系中的 Y（用于编辑区光标 ↔ 预览滚动对齐）。
    /// </summary>
    public float? GetContentYForDocumentOffset(IDocumentSource doc, int documentOffset)
    {
        EnsureLineIndex(doc);
        if (_cachedBlockRanges.Count == 0 || _fullTextLineStarts == null)
            return null;
        if (doc.FullText.Length == 0)
            return 0f;

        int off = Math.Clamp(documentOffset, 0, doc.FullText.Length);
        int? bi = GetBlockIndexForDocumentOffset(doc, off);
        if (!bi.HasValue && _cachedBlocks.Count > 0)
            bi = _cachedBlocks.Count - 1;
        if (!bi.HasValue)
            return null;

        int b = bi.Value;
        if (b < 0 || b >= _cachedBlockRanges.Count)
            return null;

        var (startLine, _) = _cachedBlockRanges[b];
        if (startLine < 0 || startLine >= _fullTextLineStarts.Length)
            return GetContentYForBlockOffset(doc, b, 0);

        int blockStart = _fullTextLineStarts[startLine];
        int charInBlock = Math.Max(0, off - blockStart);
        return GetContentYForBlockOffset(doc, b, charInBlock);
    }

    /// <summary>
    /// 根据列表块索引与块内布局行号，解析出该行在文档中的真实行号（0-based）。
    /// 按「列表行」计数（含 - / * / + 与 [ ] 任务行），与布局一行一项一致，避免与普通列表相连时偏移。
    /// </summary>
    public int? GetTodoSourceLineForListBlock(IDocumentSource doc, int blockIndex, int lineIndexInBlock)
    {
        if (blockIndex < 0 || blockIndex >= _cachedBlockRanges.Count)
            return null;
        var ast = _cachedBlocks[blockIndex];
        if (ast is not BulletListNode and not OrderedListNode)
            return null;

        var (startLine, endLine) = _cachedBlockRanges[blockIndex];
        if (startLine < 0 || endLine > doc.LineCount)
            return null;

        int count = 0;
        for (int i = startLine; i < endLine; i++)
        {
            if (!IsListLine(doc.GetLine(i)))
                continue;
            if (count == lineIndexInBlock)
                return i;
            count++;
        }
        return null;
    }

    /// <summary>是否为任意列表行（无序 - * + 或有序 N. 或任务 [ ] / [x]），用于与布局行一一对应。</summary>
    private static bool IsListLine(ReadOnlySpan<char> line)
    {
        line = line.TrimStart();
        if (line.Length < 2) return false;
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
            return true;
        if (line.StartsWith("- [ ]", StringComparison.Ordinal) || line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("* [ ]", StringComparison.Ordinal) || line.StartsWith("* [x]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("+ [ ]", StringComparison.Ordinal) || line.StartsWith("+ [x]", StringComparison.OrdinalIgnoreCase))
            return true;
        if (line[0] >= '0' && line[0] <= '9')
        {
            int i = 1;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9') i++;
            if (i + 1 < line.Length && line[i] == '.' && line[i + 1] == ' ')
                return true;
        }
        return false;
    }

    private static void CollectFootnoteRefsFromInlines(List<InlineNode> inlines, List<string> refOrder, HashSet<string> seen)
    {
        foreach (var n in inlines)
        {
            if (n is FootnoteRefNode fn && seen.Add(fn.Id))
                refOrder.Add(fn.Id);
            else if (n is BoldNode bn)
                CollectFootnoteRefsFromInlines(bn.Content, refOrder, seen);
            else if (n is ItalicNode inv)
                CollectFootnoteRefsFromInlines(inv.Content, refOrder, seen);
            else if (n is StrikethroughNode sn)
                CollectFootnoteRefsFromInlines(sn.Content, refOrder, seen);
        }
    }

    private static MarkdownNode? ReplaceFootnoteRefs(MarkdownNode? node, IReadOnlyDictionary<string, int> numberById)
    {
        if (node == null) return null;
        switch (node)
        {
            case ParagraphNode p:
                return new ParagraphNode { Content = ReplaceFootnoteRefsInlines(p.Content, numberById) };
            case HeadingNode h:
                return new HeadingNode { Level = h.Level, Content = ReplaceFootnoteRefsInlines(h.Content, numberById) };
            case BlockquoteNode bq:
                var bqChildren = new List<MarkdownNode>(bq.Children.Count);
                foreach (var n in bq.Children)
                {
                    var r = ReplaceFootnoteRefs(n, numberById);
                    if (r != null) bqChildren.Add(r);
                }
                return new BlockquoteNode { Children = bqChildren };
            case BulletListNode bl:
                var blItems = new List<ListItemNode>(bl.Items.Count);
                foreach (var it in bl.Items)
                {
                    var r = ReplaceFootnoteRefs(it, numberById);
                    if (r is ListItemNode li) blItems.Add(li);
                }
                return new BulletListNode { Items = blItems };
            case OrderedListNode ol:
                var olItems = new List<ListItemNode>(ol.Items.Count);
                foreach (var it in ol.Items)
                {
                    var r = ReplaceFootnoteRefs(it, numberById);
                    if (r is ListItemNode oli) olItems.Add(oli);
                }
                return new OrderedListNode { StartNumber = ol.StartNumber, Items = olItems };
            case ListItemNode li:
                var liContent = new List<MarkdownNode>(li.Content.Count);
                foreach (var n in li.Content)
                {
                    var r = ReplaceFootnoteRefs(n, numberById);
                    if (r != null) liContent.Add(r);
                }
                return new ListItemNode { IsTask = li.IsTask, IsChecked = li.IsChecked, Content = liContent };
            case DefinitionListNode dl:
                var dlItems = new List<DefinitionItemNode>(dl.Items.Count);
                foreach (var it in dl.Items)
                {
                    var r = ReplaceFootnoteRefs(it, numberById);
                    if (r is DefinitionItemNode di) dlItems.Add(di);
                }
                return new DefinitionListNode { Items = dlItems };
            case DefinitionItemNode di:
                var diDefs = new List<MarkdownNode>(di.Definitions.Count);
                foreach (var n in di.Definitions)
                {
                    var r = ReplaceFootnoteRefs(n, numberById);
                    if (r != null) diDefs.Add(r);
                }
                return new DefinitionItemNode { Term = ReplaceFootnoteRefsInlines(di.Term, numberById), Definitions = diDefs };
            default:
                return node;
        }
    }

    private static List<InlineNode> ReplaceFootnoteRefsInlines(List<InlineNode> inlines, IReadOnlyDictionary<string, int> numberById)
    {
        var list = new List<InlineNode>(inlines.Count);
        foreach (var n in inlines)
        {
            switch (n)
            {
                case FootnoteRefNode fn:
                    var num = numberById.TryGetValue(fn.Id, out var v) ? v : 0;
                    list.Add(new FootnoteMarkerNode { Id = fn.Id, Number = num });
                    break;
                case BoldNode bn:
                    list.Add(new BoldNode { Content = ReplaceFootnoteRefsInlines(bn.Content, numberById) });
                    break;
                case ItalicNode inv:
                    list.Add(new ItalicNode { Content = ReplaceFootnoteRefsInlines(inv.Content, numberById) });
                    break;
                case StrikethroughNode sn:
                    list.Add(new StrikethroughNode { Content = ReplaceFootnoteRefsInlines(sn.Content, numberById) });
                    break;
                default:
                    list.Add(n);
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// 获取选中文本（支持跨块，多行含换行）
    /// </summary>
    public string GetSelectedText(IDocumentSource doc, SelectionRange sel)
    {
        if (_disposed)
            return "";
        if (sel.IsEmpty) return "";
        if (_cachedLayouts == null) return "";
        var (startBlock, startOff, endBlock, endOff) = (sel.StartBlock, sel.StartOffset, sel.EndBlock, sel.EndOffset);
        if (startBlock > endBlock || (startBlock == endBlock && startOff > endOff))
            (startBlock, startOff, endBlock, endOff) = (endBlock, endOff, startBlock, startOff);

        var sb = new System.Text.StringBuilder();
        bool firstBlock = true;

        foreach (var layoutBlock in _cachedLayouts)
        {
            var bi = layoutBlock.BlockIndex;
            if (bi < startBlock || bi > endBlock)
                continue;

            if (!firstBlock)
                sb.Append('\n');
            firstBlock = false;

            int blockStart = bi == startBlock ? startOff : 0;
            int blockEnd = bi == endBlock ? endOff : int.MaxValue;
            bool firstLine = true;

            foreach (var layoutLine in layoutBlock.Lines)
            {
                if (!firstLine) sb.Append('\n');
                firstLine = false;
                foreach (var run in layoutLine.Runs)
                {
                    int runStart = run.CharOffset;
                    int runEnd = run.CharOffset + run.Text.Length;
                    int oStart = Math.Max(runStart, blockStart);
                    int oEnd = Math.Min(runEnd, blockEnd);
                    if (oStart < oEnd)
                        sb.Append(run.Text.AsSpan(oStart - runStart, oEnd - oStart));
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>文档总高度（ScrollViewer 内容高度）。由布局快照提供，无快照时返回占位高度。</summary>
    public float MeasureTotalHeight(IDocumentSource doc)
    {
        if (_disposed)
            return _config.ExtraBottomPadding;
        if (_cachedLayouts == null || _cachedLayouts.Count == 0)
            return _config.ExtraBottomPadding;
        return _cachedTotalHeight;
    }

    /// <summary>
    /// 同步执行全量解析与布局，供导出等阻塞场景使用。
    /// 预览区日常渲染由后台 LayoutTaskScheduler 驱动，不调用此方法。
    /// </summary>
    public void EnsureFullLayout(IDocumentSource doc)
    {
        if (doc == null)
            return;
        if (_disposed)
            return;

        var parseManager = new IncrementalParseManager();
        var blockSnapshot = parseManager.ReparseFull(doc);
        ApplyBlocksSnapshot(blockSnapshot, doc);

        var (blocks, ranges) = NormalizeBlockSnapshot(blockSnapshot);
        var layoutSnapshot = LayoutComputeService.ComputeFull(
            blocks,
            ranges,
            _width,
            _layout,
            _config);

        ApplyLayoutSnapshot(layoutSnapshot);
    }

    /// <summary>
    /// 计算内容实际需要的宽度（含代码块等长行）。有布局快照时返回缓存值，否则返回当前宽度。
    /// </summary>
    public float MeasureContentWidth(IDocumentSource doc)
    {
        if (_disposed)
            return Math.Max(1, _width - _config.BlockIndent) + _config.BlockIndent;
        if (_cachedLayouts != null && _cachedContentWidth > 0)
            return _cachedContentWidth;
        return Math.Max(1, _width - _config.BlockIndent) + _config.BlockIndent;
    }

    /// <summary>脚注区顶部在内容坐标系中的 Y，用于点击脚注上标后滚动。无脚注时返回 null。</summary>
    public float? GetContentYForFootnoteSection(IDocumentSource doc)
    {
        if (_disposed)
            return null;
        if (_cachedLayouts != null)
        {
            foreach (var b in _cachedLayouts)
            {
                if (b.Kind == BlockKind.Footnotes)
                    return b.Bounds.Top;
            }
        }

        // Slim 布局：脚注块不在当前 _cachedLayouts 窗口内时，用全文 cum 定位块顶。
        int? idx = FindFootnoteSectionBlockIndex(_cachedBlocks);
        if (idx.HasValue && TryGetContentYForBlockTop(idx.Value, out float y))
            return y;
        return null;
    }

    /// <summary>按脚注 id 查找正文中第一次出现的脚注引用位置（用于 ↑ 回链）。</summary>
    public float? GetContentYForFirstFootnoteRefId(IDocumentSource doc, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_disposed)
            return null;

        if (_cachedLayouts != null)
        {
            foreach (var block in _cachedLayouts)
            {
                foreach (var line in block.Lines)
                {
                    foreach (var run in line.Runs)
                    {
                        if (run.Style == RunStyle.FootnoteRef && string.Equals(run.FootnoteRefId, id, StringComparison.Ordinal))
                            return block.Bounds.Top + line.Y + run.Bounds.Top;
                    }
                }
            }
        }

        int? bi = FindFirstBodyBlockIndexWithFootnoteId(_cachedBlocks, id);
        if (bi.HasValue && TryGetContentYForBlockTop(bi.Value, out float y))
            return y;
        return null;
    }

    /// <summary>指定块内字符偏移对应的内容 Y（用于 ↩︎ 回链滚动）。</summary>
    public float? GetContentYForBlockOffset(IDocumentSource doc, int blockIndex, int charOffset)
    {
        if (_disposed)
            return null;
        if (_cachedLayouts != null)
        {
            foreach (var block in _cachedLayouts)
            {
                if (block.BlockIndex != blockIndex) continue;
                foreach (var line in block.Lines)
                {
                    foreach (var run in line.Runs)
                    {
                        int end = run.CharOffset + run.Text.Length;
                        if (charOffset >= run.CharOffset && charOffset <= end)
                            return block.Bounds.Top + line.Y + run.Bounds.Top;
                    }
                }
                return block.Bounds.Top;
            }
        }

        if (TryGetContentYForBlockTop(blockIndex, out float y))
            return y;
        return null;
    }

    /// <summary>
    /// 块顶在内容坐标系中的 Y（来自 cum）；要求已与当前块列表同步布局过。
    /// </summary>
    private bool TryGetContentYForBlockTop(int blockIndex, out float y)
    {
        y = 0;
        var cum = _cumulativeY;
        if (cum.Length != _cachedBlocks.Count + 1)
            return false;
        if (blockIndex < 0 || blockIndex >= _cachedBlocks.Count)
            return false;
        y = cum[blockIndex];
        return true;
    }

    private static int? FindFootnoteSectionBlockIndex(IReadOnlyList<MarkdownNode?> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is FootnoteSectionNode)
                return i;
        }
        return null;
    }

    /// <summary>正文里第一次出现该脚注 id 的块下标（跳过文末 FootnoteSectionNode）。</summary>
    private static int? FindFirstBodyBlockIndexWithFootnoteId(IReadOnlyList<MarkdownNode?> blocks, string id)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] is FootnoteSectionNode)
                continue;
            if (NodeContainsBodyFootnoteCitation(blocks[i], id))
                return i;
        }
        return null;
    }

    private static bool NodeContainsBodyFootnoteCitation(MarkdownNode? node, string id)
    {
        if (node == null) return false;
        switch (node)
        {
            case FootnoteSectionNode:
                return false;
            case ParagraphNode p:
                return InlinesContainFootnoteId(p.Content, id);
            case HeadingNode h:
                return InlinesContainFootnoteId(h.Content, id);
            case BlockquoteNode bq:
                foreach (var c in bq.Children)
                {
                    if (NodeContainsBodyFootnoteCitation(c, id))
                        return true;
                }
                return false;
            case BulletListNode bl:
                foreach (var it in bl.Items)
                {
                    if (ListItemContainsFootnoteId(it, id))
                        return true;
                }
                return false;
            case OrderedListNode ol:
                foreach (var it in ol.Items)
                {
                    if (ListItemContainsFootnoteId(it, id))
                        return true;
                }
                return false;
            case DefinitionListNode dl:
                foreach (var item in dl.Items)
                {
                    if (InlinesContainFootnoteId(item.Term, id))
                        return true;
                    foreach (var def in item.Definitions)
                    {
                        if (NodeContainsBodyFootnoteCitation(def, id))
                            return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static bool ListItemContainsFootnoteId(ListItemNode li, string id)
    {
        foreach (var c in li.Content)
        {
            if (NodeContainsBodyFootnoteCitation(c, id))
                return true;
        }
        return false;
    }

    private static bool InlinesContainFootnoteId(List<InlineNode> inlines, string id)
    {
        foreach (var n in inlines)
        {
            if (n is FootnoteMarkerNode fm && string.Equals(fm.Id, id, StringComparison.Ordinal))
                return true;
            if (n is FootnoteRefNode fn && string.Equals(fn.Id, id, StringComparison.Ordinal))
                return true;
            if (n is BoldNode bn && InlinesContainFootnoteId(bn.Content, id))
                return true;
            if (n is ItalicNode inv && InlinesContainFootnoteId(inv.Content, id))
                return true;
            if (n is StrikethroughNode sn && InlinesContainFootnoteId(sn.Content, id))
                return true;
        }
        return false;
    }
}

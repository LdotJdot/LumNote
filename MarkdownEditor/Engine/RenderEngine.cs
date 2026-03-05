using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Engine;

/// <summary>
/// 渲染引擎 - 统一入口
/// 文档 → 块扫描 → 解析（含缓存）→ 布局 → 渲染
/// </summary>
public sealed class RenderEngine
{
    private readonly ILayoutEngine _layout;
    private readonly SkiaRenderer _renderer;
    private readonly IImageLoader? _imageLoader;
    private float _width;

    private IDocumentSource? _cachedDoc;
    private List<MarkdownNode?> _cachedBlocks = [];
    /// <summary>每个块对应的行范围 (StartLine, EndLine)，与 _cachedBlocks 一一对应。</summary>
    private List<(int startLine, int endLine)> _cachedBlockRanges = [];
    private List<LayoutBlock>? _cachedLayouts;
    private float _cachedTotalHeight;

    public RenderEngine(float width, EngineConfig? config = null, IImageLoader? imageLoader = null, ILayoutEngine? layout = null, SkiaRenderer? renderer = null)
    {
        _width = Math.Max(1, width);
        config ??= new EngineConfig();
        _imageLoader = imageLoader ?? new DefaultImageLoader();
        _layout = layout ?? new SkiaLayoutEngine(config, _imageLoader);
        _renderer = renderer ?? new SkiaRenderer(config, _imageLoader);
    }

    /// <summary>获取当前使用的图片加载器，可用于订阅 ImageLoaded 以在图片加载完成后重绘。</summary>
    public IImageLoader? GetImageLoader() => _imageLoader;

    public void SetWidth(float width)
    {
        var w = Math.Max(1, width);
        if (Math.Abs(w - _width) > 0.1f)
        {
            _width = w;
            _cachedLayouts = null; // 宽度变化时需要重新布局
        }
    }

    /// <summary>
    /// 获取视口内需渲染的块区间（基于真实布局高度）
    /// </summary>
    public (int startBlock, int endBlock) GetVisibleBlockRange(IDocumentSource doc, float scrollY, float viewportHeight)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null || _cachedLayouts.Count == 0)
            return (0, 0);

        var blocks = _cachedLayouts;
        int start = 0;
        int end = blocks.Count;

        // 找到第一个与视口相交的块
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Bounds.Bottom >= scrollY)
            {
                start = i;
                break;
            }
        }

        // 找到最后一个与视口相交的块（向下多渲染一屏作为缓存）
        float limit = scrollY + viewportHeight * 2;
        for (int i = start; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Bounds.Top >= limit)
            {
                end = i + 1;
                break;
            }
        }

        return (start, Math.Min(end, blocks.Count));
    }

    /// <summary>
    /// 布局并渲染可见块
    /// </summary>
    public void Render(ISkiaRenderContext ctx, IDocumentSource doc, float scrollY, float viewportHeight,
        SelectionRange? selection = null)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null || _cachedLayouts.Count == 0)
            return;

        var (startBlock, endBlock) = GetVisibleBlockRange(doc, scrollY, viewportHeight);
        var blocks = _cachedLayouts.GetRange(startBlock, Math.Max(0, endBlock - startBlock));

        ctx.Canvas.Save();
        ctx.Canvas.Translate(0, -scrollY);
        _renderer.Render(ctx, blocks, selection);
        ctx.Canvas.Restore();
    }

    private void ParseFullDocument(IDocumentSource doc)
    {
        _cachedBlocks.Clear();
        _cachedBlockRanges.Clear();
        int line = 0;
        while (line < doc.LineCount)
        {
            var span = BlockScanner.ScanNextBlock(doc, line);
            if (span.LineCount <= 0) break;
            var text = GetSpanText(doc, span);
            var fullDoc = MarkdownParser.Parse(text);
            foreach (var child in fullDoc.Children)
            {
                _cachedBlocks.Add(child);
                _cachedBlockRanges.Add((span.StartLine, span.EndLine));
            }
            line = span.EndLine;
        }
    }

    private static string GetSpanText(IDocumentSource doc, BlockSpan span)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = span.StartLine; i < span.EndLine; i++)
        {
            if (i > span.StartLine) sb.Append('\n');
            sb.Append(doc.GetLine(i).ToString());
        }
        return sb.ToString();
    }

    private static float EstimateBlockHeight(BlockSpan span)
    {
        return span.Kind switch
        {
            BlockKind.CodeBlock => span.LineCount * 20 + 16,
            BlockKind.Heading => 36,
            BlockKind.HorizontalRule => 24,
            BlockKind.HtmlBlock => span.LineCount * 24,
            _ => span.LineCount * 24
        };
    }

    /// <summary>
    /// 命中测试 - 将文档坐标转为 (blockIndex, charOffset, isSelectable, linkUrl)
    /// isSelectable: 是否处于可选中文本区域（用于光标形态）
    /// linkUrl: 若命中链接/图片则返回目标 URL，否则为 null
    /// </summary>
    public (int blockIndex, int charOffset, bool isSelectable, string? linkUrl)? HitTest(IDocumentSource doc, float contentX, float contentY)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null)
            return null;

        foreach (var layoutBlock in _cachedLayouts)
        {
            if (contentY < layoutBlock.Bounds.Top || contentY >= layoutBlock.Bounds.Bottom)
                continue;

            var localX = contentX - layoutBlock.Bounds.Left;

            foreach (var layoutLine in layoutBlock.Lines)
            {
                var lineTop = layoutBlock.Bounds.Top + layoutLine.Y;
                var lineBottom = lineTop + layoutLine.Height;
                if (contentY < lineTop || contentY >= lineBottom)
                    continue;

                var runs = layoutLine.Runs;
                if (runs.Count == 0)
                    return (layoutBlock.BlockIndex, 0, false, null);

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
                        return (layoutBlock.BlockIndex, run.CharOffset + offsetInRun, true, run.LinkUrl);
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
                    return (layoutBlock.BlockIndex, bestRun.Value.CharOffset + offsetInRun, true, bestRun.Value.LinkUrl);
                return (layoutBlock.BlockIndex, runs[0].CharOffset, runs[0].Text.Length > 0, runs[0].LinkUrl);
            }
        }

        return null;
    }

    private void EnsureParsed(IDocumentSource doc)
    {
        if (ReferenceEquals(doc, _cachedDoc)) return;
        _cachedDoc = doc;
        ParseFullDocument(doc);
        _cachedLayouts = null;
        _cachedTotalHeight = 0;
    }

    /// <summary>
    /// 确保当前文档在指定宽度下完成完整布局，并缓存结果用于虚拟渲染 / 命中测试 / 文本选择。
    /// </summary>
    private void EnsureLayout(IDocumentSource doc)
    {
        EnsureParsed(doc);

        var (refOrder, defs) = CollectFootnoteRefOrderAndDefs(_cachedBlocks);

        var layouts = new List<LayoutBlock>();
        const float blockIndent = 14f;
        var contentWidth = Math.Max(1, _width - blockIndent);
        float y = 0;

        for (int blockIndex = 0; blockIndex < _cachedBlocks.Count; blockIndex++)
        {
            var ast = _cachedBlocks[blockIndex];
            if (ast is FootnoteDefNode)
                continue;
            if (ast == null)
                continue;
            var (startLine, endLine) = blockIndex < _cachedBlockRanges.Count ? _cachedBlockRanges[blockIndex] : (0, 0);
            var layoutBlock = _layout.Layout(ast, contentWidth, blockIndex, startLine, endLine, refOrder);
            var h = layoutBlock.Bounds.Height;
            layoutBlock.Bounds = new SKRect(blockIndent, y, _width, y + h);
            layouts.Add(layoutBlock);
            y += h;
        }

        if (refOrder.Count > 0)
        {
            var refPositionsById = CollectFootnoteRefPositions(layouts);
            const float footnoteTopMargin = 24f;
            y += footnoteTopMargin;
            var footnoteBlock = _layout.LayoutFootnoteSection(
                refOrder,
                defs,
                refPositionsById,
                contentWidth,
                _cachedBlocks.Count
            );
            var fh = footnoteBlock.Bounds.Height;
            if (fh > 0)
            {
                footnoteBlock.Bounds = new SKRect(blockIndent, y, _width, y + fh);
                layouts.Add(footnoteBlock);
                y += fh;
            }
        }

        // 在末尾裁掉所有“完全没有可见行”的尾随块（例如只由空行组成的块），
        // 避免它们将文档总高度向下拉长，造成预览底部出现一大片无内容留白。
        for (int i = layouts.Count - 1; i >= 0; i--)
        {
            var b = layouts[i];
            if (b.Lines == null || b.Lines.Count == 0)
            {
                y -= b.Bounds.Height;
                layouts.RemoveAt(i);
            }
            else
            {
                break;
            }
        }

        // 在文档底部仅留出极小的缓冲高度，避免产生明显的多余留白。
        const float extraBottomPadding = 8f;

        _cachedLayouts = layouts;
        _cachedTotalHeight = y + extraBottomPadding;
    }

    private static Dictionary<string, List<(int blockIndex, int charOffset)>> CollectFootnoteRefPositions(List<LayoutBlock> layouts)
    {
        var byId = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        foreach (var block in layouts)
        {
            foreach (var line in block.Lines)
            {
                foreach (var run in line.Runs)
                {
                    if (run.Style == RunStyle.FootnoteRef && run.FootnoteRefId != null)
                    {
                        if (!byId.TryGetValue(run.FootnoteRefId, out var list))
                        {
                            list = new List<(int, int)>();
                            byId[run.FootnoteRefId] = list;
                        }
                        list.Add((run.BlockIndex, run.CharOffset));
                    }
                }
            }
        }
        return byId;
    }

    private static (List<string> refOrder, Dictionary<string, FootnoteDefNode> defs) CollectFootnoteRefOrderAndDefs(List<MarkdownNode?> blocks)
    {
        var refOrder = new List<string>();
        var seen = new HashSet<string>();
        var defs = new Dictionary<string, FootnoteDefNode>(StringComparer.Ordinal);

        // 为避免在渲染线程遍历过程中，其他线程重新解析文档修改 blocks 导致
        // “Collection was modified; enumeration operation may not execute.” 异常，
        // 这里对列表做一次快照，保证遍历过程使用的是稳定的数据。
        var snapshot = blocks.Count == 0 ? Array.Empty<MarkdownNode?>() : blocks.ToArray();

        foreach (var node in snapshot)
        {
            if (node is FootnoteDefNode fn)
            {
                if (!defs.ContainsKey(fn.Id))
                    defs[fn.Id] = fn;
                continue;
            }
            CollectRefsFromNode(node, refOrder, seen);
        }
        return (refOrder, defs);
    }

    private static void CollectRefsFromNode(MarkdownNode? node, List<string> refOrder, HashSet<string> seen)
    {
        if (node == null) return;
        if (node is ParagraphNode p)
        {
            foreach (var n in p.Content)
            {
                if (n is FootnoteRefNode fn && seen.Add(fn.Id))
                    refOrder.Add(fn.Id);
                if (n is BoldNode bn) CollectRefsFromInlines(bn.Content, refOrder, seen);
                else if (n is ItalicNode inv) CollectRefsFromInlines(inv.Content, refOrder, seen);
                else if (n is StrikethroughNode sn) CollectRefsFromInlines(sn.Content, refOrder, seen);
            }
            return;
        }
        if (node is BlockquoteNode bq)
        {
            foreach (var c in bq.Children)
                CollectRefsFromNode(c, refOrder, seen);
            return;
        }
        if (node is ListItemNode li)
        {
            foreach (var c in li.Content)
                CollectRefsFromNode(c, refOrder, seen);
            return;
        }
        if (node is DefinitionListNode dl)
        {
            foreach (var item in dl.Items)
            {
                foreach (var def in item.Definitions)
                    CollectRefsFromNode(def, refOrder, seen);
            }
        }
    }

    private static void CollectRefsFromInlines(List<InlineNode> content, List<string> refOrder, HashSet<string> seen)
    {
        foreach (var n in content)
        {
            if (n is FootnoteRefNode fn && seen.Add(fn.Id))
                refOrder.Add(fn.Id);
            if (n is BoldNode bn) CollectRefsFromInlines(bn.Content, refOrder, seen);
            else if (n is ItalicNode inv) CollectRefsFromInlines(inv.Content, refOrder, seen);
            else if (n is StrikethroughNode sn) CollectRefsFromInlines(sn.Content, refOrder, seen);
        }
    }

    /// <summary>
    /// 获取选中文本（支持跨块，多行含换行）
    /// </summary>
    public string GetSelectedText(IDocumentSource doc, SelectionRange sel)
    {
        if (sel.IsEmpty) return "";
        EnsureLayout(doc);
        var (startBlock, startOff, endBlock, endOff) = (sel.StartBlock, sel.StartOffset, sel.EndBlock, sel.EndOffset);
        if (startBlock > endBlock || (startBlock == endBlock && startOff > endOff))
            (startBlock, startOff, endBlock, endOff) = (endBlock, endOff, startBlock, startOff);

        var sb = new System.Text.StringBuilder();
        bool firstBlock = true;

        if (_cachedLayouts == null)
            return "";

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

    /// <summary>
    /// 精确计算整篇文档的总高度（用于 ScrollViewer 内容高度）
    /// </summary>
    public float MeasureTotalHeight(IDocumentSource doc)
    {
        EnsureLayout(doc);
        return _cachedTotalHeight;
    }

    /// <summary>脚注区顶部在内容坐标系中的 Y，用于点击脚注上标后滚动。无脚注时返回 null。</summary>
    public float? GetContentYForFootnoteSection(IDocumentSource doc)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null) return null;
        foreach (var b in _cachedLayouts)
        {
            if (b.Kind == BlockKind.Footnotes)
                return b.Bounds.Top;
        }
        return null;
    }

    /// <summary>指定块内字符偏移对应的内容 Y（用于 ↩︎ 回链滚动）。</summary>
    public float? GetContentYForBlockOffset(IDocumentSource doc, int blockIndex, int charOffset)
    {
        EnsureLayout(doc);
        if (_cachedLayouts == null) return null;
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
        return null;
    }
}

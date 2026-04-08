using System.Collections.Generic;
using System.Linq;
using System.Text;
using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Highlighting;
using MarkdownEditor.Engine.Render;
using SkiaSharp;
using System.IO;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 基于 Skia 的布局引擎 - 精确测量，CJK 换行，配置字体
/// 实现 ILayoutEnvironment 供各 BlockLayouter 使用；通过 IBlockLayouter 调度实现类 DOM 的块级布局。
/// </summary>
public sealed class SkiaLayoutEngine : ILayoutEngine, ILayoutEnvironment
{
    private const string EmbeddedMathFontResourceName = "MarkdownEditor.asserts.otf.latinmodern-math.otf";
    private readonly string _bodyFontFamily;
    private readonly string _codeFontFamily;
    private readonly string _mathFontFamily;
    private readonly string? _mathFontFilePath;
    private readonly float _baseFontSize;
    private readonly float _lineSpacing;
    private readonly float _paraSpacing;
    private readonly float _codeBlockPadding;
    private readonly float _blockInnerPadding;
    private readonly float _blockquotePadding;
    private readonly float _blockquoteIndent;
    private readonly float _listItemIndent;
    private readonly float _listBlockMarginTop;
    private readonly float _listBlockMarginBottom;
    private readonly float _listItemGap;
    private readonly float _footnoteTopMargin;
    private readonly float _definitionListIndent;
    private readonly IImageLoader _imageLoader;
    private readonly ITextMeasurer? _tableTextMeasurer;
    private readonly SKPaint _measurePaint = new();
    private readonly SkiaPrefixWidthCache _prefixWidthCache = new();
    private readonly IBlockLayouter[] _blockLayouters;
    private SKTypeface? _bodyTypeface;
    private SKFont? _fontCache;
    private SKFont? _boldFontCache;
    private SKFont? _italicFontCache;
    private SKFont? _codeFontCache;
    private SKTypeface? _mathTypeface;

    public bool RequiresRelayoutOnWidthChange => true;

    public SkiaLayoutEngine(EngineConfig? config = null, IImageLoader? imageLoader = null, ITextMeasurer? tableTextMeasurer = null)
    {
        config ??= new EngineConfig();
        _imageLoader = imageLoader ?? new DefaultImageLoader();
        _tableTextMeasurer = tableTextMeasurer;
        _bodyFontFamily = config.BodyFontFamily;
        _codeFontFamily = config.CodeFontFamily;
        _mathFontFamily = config.MathFontFamily;
        _mathFontFilePath = config.MathFontFilePath;
        _baseFontSize = config.BaseFontSize;
        _lineSpacing = config.LineSpacing;
        _paraSpacing = config.ParaSpacing;
        _codeBlockPadding = config.CodeBlockPadding;
        _blockInnerPadding = config.BlockInnerPadding;
        _blockquotePadding = config.BlockquotePadding;
        _blockquoteIndent = config.BlockquoteIndent;
        _listItemIndent = config.ListItemIndent;
        _listBlockMarginTop = config.ListBlockMarginTop;
        _listBlockMarginBottom = config.ListBlockMarginBottom;
        _listItemGap = config.ListItemGap;
        _footnoteTopMargin = config.FootnoteTopMargin;
        _definitionListIndent = config.DefinitionListIndent;
        _blockLayouters = BlockLayouterRegistry.CreateDefault(this);
    }

    public SkiaLayoutEngine(SKTypeface? typeface, float baseFontSize = 16, float lineSpacing = 1.35f, float paraSpacing = 8f)
        : this(new EngineConfig { BaseFontSize = baseFontSize, LineSpacing = lineSpacing, ParaSpacing = paraSpacing })
    {
        if (typeface != null)
            _bodyTypeface = typeface;
    }

    private SKTypeface GetBodyTypeface()
    {
        if (_bodyTypeface != null) return _bodyTypeface;
        foreach (var name in _bodyFontFamily.Split(',', StringSplitOptions.TrimEntries))
        {
            var tf = SKTypeface.FromFamilyName(name.Trim());
            if (tf != null) { _bodyTypeface = tf; return tf; }
        }
        _bodyTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI");
        return _bodyTypeface;
    }

    /// <summary>
    /// 选择用于数学公式的一套字体：
    /// 1. 若 EngineConfig.MathFontFilePath 指定了字体文件且存在，则优先从文件加载；
    /// 2. 否则按 EngineConfig.MathFontFamily 中的家族名顺序尝试；
    /// 3. 最后回退到正文字体。
    /// </summary>
    private SKTypeface GetMathTypeface()
    {
        if (_mathTypeface != null) return _mathTypeface;

        // 1) 优先：程序集内嵌字体资源（发布后不依赖外部路径）
        try
        {
            var asm = typeof(SkiaLayoutEngine).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedMathFontResourceName);
            if (stream != null)
            {
                using var data = SKData.Create(stream);
                var tfFromResource = data != null ? SKTypeface.FromData(data) : null;
                if (tfFromResource != null)
                {
                    _mathTypeface = tfFromResource;
                    return _mathTypeface;
                }
            }
        }
        catch
        {
            // 嵌入资源加载失败时继续后续回退
        }

        // 2) 其次：显式指定的数学字体文件
        if (!string.IsNullOrWhiteSpace(_mathFontFilePath))
        {
            try
            {
                if (File.Exists(_mathFontFilePath))
                {
                    var tfFromFile = SKTypeface.FromFile(_mathFontFilePath);
                    if (tfFromFile != null)
                    {
                        _mathTypeface = tfFromFile;
                        return _mathTypeface;
                    }
                }
            }
            catch
            {
                // 忽略单个字体失败，继续尝试后续候选
            }
        }

        // 3) 其次：MathFontFamily 中配置的数学字体家族列表
        foreach (var name in _mathFontFamily.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var tf = SKTypeface.FromFamilyName(name.Trim(), SKFontStyle.Normal);
                if (tf != null)
                {
                    _mathTypeface = tf;
                    return _mathTypeface;
                }
            }
            catch
            {
                // 继续尝试下一个候选
            }
        }

        // 4) 回退到正文字体
        _mathTypeface = GetBodyTypeface();
        return _mathTypeface;
    }

    private SKFont GetFont(bool bold, bool italic, bool code)
    {
        if (code)
        {
            if (_codeFontCache != null) return _codeFontCache;
            foreach (var name in _codeFontFamily.Split(',', StringSplitOptions.TrimEntries))
            {
                var tf = SKTypeface.FromFamilyName(name.Trim());
                if (tf != null) { _codeFontCache = new SKFont(tf, _baseFontSize * 0.9f); return _codeFontCache; }
            }
            _codeFontCache = new SKFont(SKTypeface.FromFamilyName("Cascadia Code"), _baseFontSize * 0.9f);
            return _codeFontCache;
        }
        var body = GetBodyTypeface();
        if (bold && italic) return _boldFontCache ??= new SKFont(SKTypeface.FromFamilyName(body.FamilyName, SKFontStyle.BoldItalic), _baseFontSize);
        if (bold) return _boldFontCache ??= new SKFont(SKTypeface.FromFamilyName(body.FamilyName, SKFontStyle.Bold), _baseFontSize);
        if (italic) return _italicFontCache ??= new SKFont(SKTypeface.FromFamilyName(body.FamilyName, SKFontStyle.Italic), _baseFontSize);
        return _fontCache ??= new SKFont(body, _baseFontSize);
    }

    public LayoutBlock Layout(MarkdownNode node, float width, int blockIndex, int startLine, int endLine)
    {
        var ctx = new BlockLayoutContext(width, blockIndex, startLine, endLine, this);
        foreach (var layouter in _blockLayouters)
        {
            if (layouter.Matches(node))
                return layouter.Layout(node, ctx);
        }
        return LayoutWithSwitch(node, width, blockIndex, startLine, endLine);
    }

    private LayoutBlock LayoutWithSwitch(MarkdownNode node, float width, int blockIndex, int startLine, int endLine)
    {
        var block = new LayoutBlock { BlockIndex = blockIndex, StartLine = startLine, EndLine = endLine };
        float x = 0, y = 0;

        switch (node)
        {
            case HeadingNode h:
                LayoutHeading(block, h, width, ref x, ref y);
                break;
            case ParagraphNode p:
                LayoutParagraph(block, p, width, ref x, ref y);
                break;
            case CodeBlockNode c:
                block.Kind = BlockKind.CodeBlock;
                LayoutCodeBlock(block, c, width, ref x, ref y);
                break;
            case HorizontalRuleNode:
                block.Kind = BlockKind.HorizontalRule;
                LayoutHorizontalRule(block, width, ref x, ref y);
                break;
            case MathBlockNode m:
                LayoutMathBlock(block, m, width, ref x, ref y);
                break;
            case TableNode t:
                block.Kind = BlockKind.Table;
                LayoutTable(block, t, width, ref x, ref y);
                break;
            case BlockquoteNode bq:
                LayoutBlockquote(block, bq, width, ref x, ref y);
                break;
            case BulletListNode:
            case OrderedListNode:
                LayoutList(block, node, width, ref x, ref y);
                break;
            case HtmlBlockNode html:
                block.Kind = BlockKind.HtmlBlock;
                LayoutHtmlBlock(block, html, width, ref x, ref y);
                break;
            case DefinitionListNode dl:
                block.Kind = BlockKind.DefinitionList;
                LayoutDefinitionList(block, dl, width, ref x, ref y);
                break;
            case FootnoteDefNode:
                break;
            case FootnoteSectionNode fs:
                block.Kind = BlockKind.Footnotes;
                LayoutFootnoteSectionBlock(block, fs, width, blockIndex, ref x, ref y);
                break;
            case EmptyLineNode el:
                // 多空行按实际数量渲染（上限 10 行），保证多行回车可见
                int emptyLines = Math.Clamp(el.LineCount, 1, 10);
                y += _baseFontSize * _lineSpacing * emptyLines;
                break;
            default:
                block.Bounds = new SKRect(0, 0, width, _baseFontSize * _lineSpacing);
                break;
        }

        block.Bounds = new SKRect(0, 0, width, y);
        return block;
    }

    private void LayoutFootnoteSectionBlock(LayoutBlock block, FootnoteSectionNode section, float width, int blockIndex, ref float x, ref float y)
    {
        var lineH = _baseFontSize * _lineSpacing;
        // 使用与正文一致的字体/字号测量，避免箭头 run 宽度偏大导致可点击区域覆盖到左侧文字
        var bodyFont = new SKFont(GetBodyTypeface(), _baseFontSize);
        var spaceW = bodyFont.MeasureText(" ");
        // 与旧实现保持一致：脚注区前留出一段间距，使其与正文分隔更明显（分隔线仍在 y=0 绘制）。
        y += _footnoteTopMargin;
        // 顶部留白（分隔线由 DrawFootnotesStyle 在 y=0 绘制），然后开始列表
        y += lineH * 0.8f;
        int charOffset = 0;
        var fnIndent = _blockInnerPadding + _definitionListIndent;
        const string backArrow = "\u2191"; // ↑ 回顶符号，作为特殊符号放在本条脚注文末（多引用时每条脚注一个 ↑）
        for (int i = 0; i < section.Items.Count; i++)
        {
            var item = section.Items[i];
            var id = item.Id;
            var num = item.Number.ToString();
            var prefix = num + ". ";
            int lineCountBefore = block.Lines.Count;
            foreach (var child in item.Content)
            {
                if (child is ParagraphNode p)
                {
                    var content = FlattenInlinesForFootnote(p.Content);
                    var fullText = prefix + content;
                    prefix = "";
                    var segments = BreakTextWithWrap(fullText, width - fnIndent - _blockInnerPadding, bodyFont);
                    foreach (var seg in segments)
                    {
                        var textW = bodyFont.MeasureText(seg);
                        var line = new LayoutLine { Y = y, Height = lineH };
                        line.Runs.Add(new LayoutRun(seg, new SKRect(fnIndent, y, fnIndent + textW, y + lineH), RunStyle.Normal, blockIndex, charOffset));
                        block.Lines.Add(line);
                        y += lineH;
                        charOffset += seg.Length + 1;
                    }
                }
            }
            if (lineCountBefore != block.Lines.Count)
            {
                // “↑”作为文末特殊符号追加在最后一行尾部，不单独占行；多引用时每条脚注仍只放一个 ↑（点后回正文首次引用处）。
                var lastLine = block.Lines[^1];
                var arrowW = bodyFont.MeasureText(backArrow);
                float runX = lastLine.Runs.Count > 0
                    ? lastLine.Runs[^1].Bounds.Right + spaceW
                    : fnIndent;
                var linkUrl = "footnote-back:" + id;
                lastLine.Runs.Add(
                    new LayoutRun(
                        backArrow,
                        new SKRect(runX, lastLine.Y, runX + arrowW, lastLine.Y + lineH),
                        RunStyle.Link,
                        blockIndex,
                        charOffset,
                        linkUrl
                    )
                );
                charOffset += backArrow.Length;
            }
            y += lineH * 0.6f;
        }
    }

    private static string FlattenInlinesForFootnote(List<InlineNode> content)
    {
        var sb = new StringBuilder();
        foreach (var n in content)
        {
            if (n is TextNode tn) sb.Append(tn.Content);
            else if (n is BoldNode bn) sb.Append(FlattenInlinesForFootnote(bn.Content));
            else if (n is ItalicNode inNode) sb.Append(FlattenInlinesForFootnote(inNode.Content));
            else if (n is CodeNode cn) sb.Append(cn.Content);
            else if (n is LinkNode ln) sb.Append(ln.Text);
            else if (n is StrikethroughNode sn) sb.Append(FlattenInlinesForFootnote(sn.Content));
            else if (n is FootnoteRefNode fn) sb.Append("[^" + fn.Id + "]");
            else if (n is FootnoteMarkerNode fm) sb.Append("[" + fm.Number + "]");
        }
        return sb.ToString();
    }

    private void LayoutHeading(LayoutBlock block, HeadingNode h, float width, ref float x, ref float y)
    {
        var fontSize = h.Level switch { 1 => 28, 2 => 24, 3 => 20, 4 => 18, 5 => 16, _ => 14 };
        var plain = FlattenInlines(h.Content);
        var font = new SKFont(SKTypeface.FromFamilyName(GetBodyTypeface().FamilyName, SKFontStyle.Bold), fontSize);
        var lineH = fontSize * _lineSpacing;
        var innerW = width - _blockInnerPadding * 2;
        var segments = BreakTextWithWrap(plain, innerW, font);
        foreach (var seg in segments)
        {
            var line = new LayoutLine { Y = y, Height = lineH };
            var headingStyle = (RunStyle)((int)RunStyle.Heading1 + Math.Clamp(h.Level, 1, 6) - 1);
            line.Runs.Add(new LayoutRun(seg, new SKRect(_blockInnerPadding, y, width - _blockInnerPadding, y + lineH), headingStyle, block.BlockIndex, 0));
            block.Lines.Add(line);
            y += lineH;
        }
        y += _paraSpacing;
    }

    private void LayoutParagraph(LayoutBlock block, ParagraphNode p, float width, ref float x, ref float y, float leftMargin = 0f)
    {
        var innerLeft = leftMargin > 0 ? leftMargin : _blockInnerPadding;
        var innerWidth = width - innerLeft - (leftMargin > 0 ? 0 : _blockInnerPadding);

        // 仅包含单个图片的段落：单独一行、本身尺寸
        if (p.Content.Count == 1 && p.Content[0] is ImageNode imgNode)
        {
            LayoutImageBlock(block, imgNode, width, ref x, ref y, innerLeft);
            return;
        }

        var lineH = _baseFontSize * _lineSpacing;
        var runs = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
        int totalCharOffset = 0;
        foreach (var n in p.Content)
        {
            if (n is ImageNode img)
            {
                FlushParagraphLineFromRuns(block, runs, lineH, ref y, block.BlockIndex, innerLeft, ref totalCharOffset);
                runs.Clear();
                var (iw, ih) = GetImageIntrinsicSize(img.Url ?? "", innerWidth - 8f);
                var line = new LayoutLine { Y = y, Height = ih };
                line.Runs.Add(new LayoutRun(img.Alt ?? "", new SKRect(innerLeft, y, innerLeft + iw, y + ih), RunStyle.Image, block.BlockIndex, totalCharOffset, img.Url));
                block.Lines.Add(line);
                y += ih;
                totalCharOffset += 1;
                continue;
            }
            ExpandInlineNodeToRuns(n, false, false, runs);
        }
        if (runs.Count == 0)
        {
            if (block.Lines.Count == 0 || block.Lines[^1].Runs.Count > 0)
            {
                block.Lines.Add(new LayoutLine { Y = y, Height = lineH });
                y += lineH;
            }
            y += _paraSpacing;
            return;
        }

        var wrappedLines = BreakInlineRunsIntoLines(runs, innerWidth);
        foreach (var lineRuns in wrappedLines)
        {
            FlushParagraphLine(block, lineRuns, lineH, ref y, block.BlockIndex, ref totalCharOffset, innerLeft);
        }
        y += _paraSpacing;
    }

    private void FlushParagraphLineFromRuns(LayoutBlock block, List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)> runs, float lineH, ref float y, int blockIndex, float leftMargin, ref int totalCharOffset)
    {
        if (runs.Count == 0) return;
        var line = new LayoutLine { Y = y, Height = lineH };
        float x = leftMargin;
        int offset = totalCharOffset;
        foreach (var (text, style, linkUrl, footnoteRefId) in runs)
        {
            var font = GetFont(IsRunStyleBold(style), IsRunStyleItalic(style), style == RunStyle.Code);
            float w = font.MeasureText(text);
            line.Runs.Add(new LayoutRun(text, new SKRect(x, y, x + w, y + lineH), style, blockIndex, offset, linkUrl, null, footnoteRefId));
            offset += text.Length;
            x += w;
        }
        totalCharOffset = offset;
        block.Lines.Add(line);
        y += lineH;
    }

    private void FlushParagraphLine(LayoutBlock block, List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)> runs, float lineH, ref float y, int blockIndex, ref int offset, float leftMargin = 0f)
    {
        // 先根据当前行内是否包含数学公式，动态放大行高，避免根号/分数等被裁剪。
        float lineAbove = lineH * 0.8f;
        float lineBelow = lineH - lineAbove;
        foreach (var (text, style, _, _) in runs)
        {
            if (style != RunStyle.Math) continue;
            var (_, h, d) = MeasureMathMetrics(text);
            if (h > 0 || d > 0)
            {
                lineAbove = Math.Max(lineAbove, h);
                lineBelow = Math.Max(lineBelow, d);
            }
        }
        float actualLineH = lineAbove + lineBelow;

        var line = new LayoutLine { Y = y, Height = actualLineH };
        float x = leftMargin;
        foreach (var (text, style, linkUrl, footnoteRefId) in runs)
        {
            float w;
            if (style == RunStyle.Math)
            {
                // 对行内数学使用 MathSkiaRenderer 的测量结果，使 Run.Bounds 宽度与公式实际渲染宽度一致，
                // 同时行高已按 MeasureMathMetrics 的 height/depth 放大，保证不会截断。
                w = MeasureMathInline(text);
            }
            else if (style == RunStyle.Image)
            {
                var font = GetFont(false, false, false);
                w = Math.Max(120, Math.Min(400, font.MeasureText(text) + 24));
            }
            else
            {
                var font = GetFont(IsRunStyleBold(style), IsRunStyleItalic(style), style == RunStyle.Code);
                w = font.MeasureText(text);
                // 待办复选框 run 保证最小命中宽度，与绘制的小方框一致，避免误触到右侧链接
                if (linkUrl != null && linkUrl.StartsWith("todo-toggle:", StringComparison.Ordinal))
                    w = Math.Max(w, 22f);
            }
            line.Runs.Add(new LayoutRun(text, new SKRect(x, y, x + w, y + actualLineH), style, blockIndex, offset, linkUrl, null, footnoteRefId));
            offset += text.Length;
            x += w;
        }
        block.Lines.Add(line);
        y += actualLineH;
    }

    private void LayoutImageBlock(LayoutBlock block, ImageNode img, float width, ref float x, ref float y, float leftMargin = 0f)
    {
        var availW = width - (leftMargin > 0 ? leftMargin : _blockInnerPadding) - _blockInnerPadding - 8f;
        var (targetWidth, targetHeight) = GetImageIntrinsicSize(img.Url ?? "", Math.Max(80f, availW));
        var left = leftMargin > 0 ? leftMargin : _blockInnerPadding;
        var line = new LayoutLine { Y = y, Height = targetHeight };
        var rect = new SKRect(left, y, left + targetWidth, y + targetHeight);
        line.Runs.Add(new LayoutRun(img.Alt ?? "", rect, RunStyle.Image, block.BlockIndex, 0, img.Url));
        block.Lines.Add(line);
        y += targetHeight + _paraSpacing;
    }

    /// <summary>获取图片本身尺寸（像素），仅当超过 maxWidth 时按宽等比缩小。</summary>
    private (float w, float h) GetImageIntrinsicSize(string url, float maxWidth)
    {
        var contentWidth = Math.Max(80f, maxWidth);
        var fallbackH = _baseFontSize * 6f;
        if (
            _imageLoader.TryGetImagePixelSize(url, out var iw, out var ih)
            && iw > 0
            && ih > 0
        )
        {
            float w = iw;
            float h = ih;
            if (w > contentWidth)
            {
                var scale = contentWidth / w;
                w = contentWidth;
                h *= scale;
            }
            return (w, h);
        }
        return (contentWidth, fallbackH);
    }

    /// <summary>
    /// Markdown 换行规则：单个 \n 渲染为换行；连续多个 \n 最多显示 2 个（即最多 1 个空行）；"  \n" 与 "\" + 换行为硬换行。
    /// 支持 CJK 的断行，西文优先在空格处断。
    /// 返回 (行内容, 在原文中的起始索引, 在原文中的结束索引)
    /// </summary>
    private static List<(string line, int start, int end)> BreakTextIntoLinesWithOffsets(string text, float maxWidth, SKFont font)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var result = new List<(string, int, int)>();
        var effectiveMaxWidth = maxWidth <= 0 ? float.MaxValue : maxWidth;

        // 按 Markdown 规则切分逻辑行：单 \n = 换行；连续 \n 最多 2 个（1 个空行）；"  \n" / "\" + \n = 硬换行
        var logicalLines = new List<(string content, int origStart, int origEnd)>();
        var sb = new StringBuilder();
        int origStart = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (i + 2 < text.Length && text[i] == ' ' && text[i + 1] == ' ' && text[i + 2] == '\n')
            {
                logicalLines.Add((sb.ToString().TrimEnd(), origStart, i + 2));
                sb.Clear();
                origStart = i + 3;
                i += 3;
            }
            else if (i + 1 < text.Length && text[i] == '\\' && text[i + 1] == '\n')
            {
                logicalLines.Add((sb.ToString().TrimEnd(), origStart, i));
                sb.Clear();
                origStart = i + 2;
                i += 2;
            }
            else if (text[i] == '\n')
            {
                logicalLines.Add((sb.ToString().TrimEnd(), origStart, i));
                sb.Clear();
                origStart = i + 1;
                i++;
                // 连续 \n 最多显示 2 个（即最多 1 个空行）
                if (i < text.Length && text[i] == '\n')
                {
                    int runStart = i;
                    while (i < text.Length && text[i] == '\n')
                        i++;
                    logicalLines.Add(("", runStart - 1, i));
                    origStart = i;
                }
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        if (sb.Length > 0 || logicalLines.Count == 0)
            logicalLines.Add((sb.ToString(), origStart, text.Length));

        foreach (var (content, baseStart, baseEnd) in logicalLines)
        {
            int start = 0;
            if (content.Length == 0)
            {
                result.Add(("", baseStart, baseStart));
                continue;
            }
            while (start < content.Length)
            {
                int end = FindBreakEnd(content, start, effectiveMaxWidth, font);
                if (end <= start) end = start + 1;
                var seg = content[start..end];
                var segStart = baseStart + start;
                var segEnd = baseStart + end;
                result.Add((seg, segStart, segEnd));
                start = end;
            }
        }
        return result;
    }

    private static List<string> BreakTextWithWrap(string text, float maxWidth, SKFont font)
    {
        var withOffsets = BreakTextIntoLinesWithOffsets(text, maxWidth, font);
        var list = new List<string>(withOffsets.Count);
        for (int i = 0; i < withOffsets.Count; i++)
            list.Add(withOffsets[i].line);
        return list;
    }

    private static int FindBreakEnd(string text, int start, float maxWidth, SKFont font)
    {
        if (start >= text.Length) return start;
        int len = text.Length - start;
        if (len == 1) return start + 1;
        int lastGood = start;
        int lastSpace = -1;
        for (int i = start + 1; i <= text.Length; i++)
        {
            var span = text.AsSpan(start, i - start);
            float w = font.MeasureText(span);
            if (w <= maxWidth)
            {
                lastGood = i;
                if (i > start && (text[i - 1] == ' ' || text[i - 1] == '\t'))
                    lastSpace = i;
            }
            else
            {
                if (lastSpace > start) return lastSpace;
                if (lastGood > start) return lastGood;
                return start + 1;
            }
        }
        return lastGood;
    }

    /// <summary>``` 代码块：内部文字与框边缘留出间隙，不贴边。</summary>

    private static RunStyle TokenKindToRunStyle(CodeTokenKind kind)
    {
        return kind switch
        {
            CodeTokenKind.Keyword => RunStyle.CodeKeyword,
            CodeTokenKind.String => RunStyle.CodeString,
            CodeTokenKind.Comment => RunStyle.CodeComment,
            CodeTokenKind.Number => RunStyle.CodeNumber,
            _ => RunStyle.CodeDefault,
        };
    }

    private void LayoutCodeBlock(LayoutBlock block, CodeBlockNode c, float width, ref float x, ref float y)
    {
        var font = GetFont(false, false, true);
        var lineHeight = _baseFontSize * 0.95f * _lineSpacing;
        var lines = c.Code.Split('\n');
        int charOffset = 0;
        var language = c.Language ?? "";

        y += _codeBlockPadding;
        var left = _codeBlockPadding;
        var right = width - _codeBlockPadding;

        foreach (var codeLine in lines)
        {
            var layoutLine = new LayoutLine { Y = y, Height = lineHeight };
            var spansRaw = CodeTokenizer.TokenizeLine(language, codeLine);
            IReadOnlyList<CodeTokenizer.TokenSpan> spans = (spansRaw.Count == 0 && codeLine.Length > 0)
                ? new List<CodeTokenizer.TokenSpan> { new CodeTokenizer.TokenSpan(0, codeLine.Length, CodeTokenKind.Default) }
                : spansRaw;
            float runX = left;

            foreach (var span in spans)
            {
                var tokenText = span.Length > 0 && span.Start + span.Length <= codeLine.Length
                    ? codeLine.Substring(span.Start, span.Length)
                    : "";
                if (string.IsNullOrEmpty(tokenText)) continue;

                var tokenWidth = font.MeasureText(tokenText);
                var runBounds = new SKRect(runX, y, runX + tokenWidth, y + lineHeight);
                var runStyle = TokenKindToRunStyle(span.Kind);
                layoutLine.Runs.Add(new LayoutRun(tokenText, runBounds, runStyle, block.BlockIndex, charOffset));
                runX += tokenWidth;
                charOffset += span.Length;
            }

            block.Lines.Add(layoutLine);
            y += lineHeight;
            charOffset += 1; // \n
        }

        float blockContentWidth = left;
        foreach (var line in block.Lines)
            foreach (var run in line.Runs)
                if (run.Bounds.Right > blockContentWidth) blockContentWidth = run.Bounds.Right;
        block.ContentWidth = blockContentWidth + _codeBlockPadding;

        y += _codeBlockPadding;
        y += _paraSpacing;
    }

    private void LayoutHorizontalRule(LayoutBlock block, float width, ref float x, ref float y)
    {
        var line = new LayoutLine { Y = y, Height = 12 };
        block.Lines.Add(line);
        y += 12 + _paraSpacing;
    }

    /// <summary>公式块：与代码块一致的内边距，公式不贴边；公式字号略大于正文。</summary>
    private static float MathBlockFontSize(float baseFontSize) => Math.Max(16, baseFontSize * 1.15f);

    private const float MathBlockPadding = 16f;

    private void LayoutMathBlock(LayoutBlock block, MathBlockNode m, float width, ref float x, ref float y)
    {
        var fontSize = MathBlockFontSize(_baseFontSize);
        var latex = m.LaTeX ?? "";

        var bodyTypeface = GetBodyTypeface();
        var mathTypeface = GetMathTypeface();
        var (_, height, depth) = MarkdownEditor.Latex.MathSkiaRenderer.MeasureFormula(latex, bodyTypeface, mathTypeface, fontSize);
        var contentH = height + depth;
        if (contentH <= 0)
            contentH = fontSize * 1.5f;

        // 在四周都留出 MathBlockPadding 作为内边距：背景矩形覆盖整个区域，
        // 公式在 DrawFormula 内部会根据 Bounds 自动垂直/水平居中。
        var totalH = contentH + MathBlockPadding * 2;
        var left = MathBlockPadding;
        var right = width - MathBlockPadding;
        var line = new LayoutLine { Y = y, Height = totalH };
        line.Runs.Add(new LayoutRun(latex, new SKRect(left, y, right, y + totalH), RunStyle.Math, block.BlockIndex, 0));
        block.Lines.Add(line);
        y += totalH + _paraSpacing;
    }

    /// <summary>HTML 块：与代码块相同样式（背景+等宽字+内边距），原样显示标签源码。</summary>
    private void LayoutHtmlBlock(LayoutBlock block, HtmlBlockNode html, float width, ref float x, ref float y)
    {
        var font = GetFont(false, false, true);
        var lineHeight = _baseFontSize * 0.95f * _lineSpacing;
        var raw = html.RawHtml ?? "";
        var lines = raw.Split('\n');
        int charOffset = 0;

        y += _codeBlockPadding;
        var left = _codeBlockPadding;
        var right = width - _codeBlockPadding;

        foreach (var codeLine in lines)
        {
            float lineW = font.MeasureText(codeLine);
            float runRight = left + lineW;
            var run = new LayoutRun(codeLine, new SKRect(left, y, runRight, y + lineHeight), RunStyle.Code, block.BlockIndex, charOffset);
            var layoutLine = new LayoutLine { Y = y, Height = lineHeight };
            layoutLine.Runs.Add(run);
            block.Lines.Add(layoutLine);
            y += lineHeight;
            charOffset += codeLine.Length + 1;
        }

        float htmlContentWidth = left;
        foreach (var line in block.Lines)
            foreach (var run in line.Runs)
                if (run.Bounds.Right > htmlContentWidth) htmlContentWidth = run.Bounds.Right;
        block.ContentWidth = htmlContentWidth + _codeBlockPadding;

        y += _codeBlockPadding;
        y += _paraSpacing;
    }

    private static string StripHtmlTags(string html)
    {
        var sb = new StringBuilder();
        bool inTag = false;
        foreach (var c in html)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString().Trim().Replace("\n\n", "\n");
    }

    private static string FlattenInlines(List<InlineNode> nodes)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes)
        {
            if (n is TextNode tn) sb.Append(tn.Content);
            else if (n is BoldNode bn) sb.Append(FlattenInlines(bn.Content));
            else if (n is ItalicNode inNode) sb.Append(FlattenInlines(inNode.Content));
            else if (n is StrikethroughNode sn) sb.Append(FlattenInlines(sn.Content));
            else if (n is CodeNode cn) sb.Append(cn.Content);
            else if (n is LinkNode ln) sb.Append(ln.Text);
            else if (n is ImageNode img) sb.Append("[" + img.Alt + "]");
            else if (n is MathInlineNode math) sb.Append(math.LaTeX ?? "");
            else if (n is FootnoteRefNode fn) sb.Append("[^" + fn.Id + "]");
            else if (n is FootnoteMarkerNode fm) sb.Append("[" + fm.Number + "]");
        }
        return sb.ToString();
    }

    private static RunStyle CombineEmphasisStyle(bool bold, bool italic) =>
        bold && italic
            ? RunStyle.BoldItalic
            : bold ? RunStyle.Bold : italic ? RunStyle.Italic : RunStyle.Normal;

    private static RunStyle CombineLinkStyle(bool bold, bool italic) =>
        bold && italic
            ? RunStyle.LinkBoldItalic
            : bold ? RunStyle.LinkBold : italic ? RunStyle.LinkItalic : RunStyle.Link;

    private static bool IsRunStyleBold(RunStyle style) =>
        style
            is RunStyle.Bold
                or RunStyle.BoldItalic
                or RunStyle.LinkBold
                or RunStyle.LinkBoldItalic
                or RunStyle.Heading1
                or RunStyle.Heading2
                or RunStyle.Heading3
                or RunStyle.Heading4
                or RunStyle.Heading5
                or RunStyle.Heading6
                or RunStyle.TableHeaderCell;

    private static bool IsRunStyleItalic(RunStyle style) =>
        style
            is RunStyle.Italic
                or RunStyle.BoldItalic
                or RunStyle.Math
                or RunStyle.LinkItalic
                or RunStyle.LinkBoldItalic;

    /// <summary>将行内 AST 展开为带正确粗/斜体组合的 runs（支持 <c>***</c> 产生的 Bold→Italic 嵌套及链接上叠加强调）。</summary>
    private static void ExpandInlineNodeToRuns(
        InlineNode n,
        bool bold,
        bool italic,
        List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)> runs
    )
    {
        switch (n)
        {
            case TextNode tn:
                if (!string.IsNullOrEmpty(tn.Content))
                    runs.Add((tn.Content, CombineEmphasisStyle(bold, italic), null, null));
                break;
            case BoldNode bn:
                foreach (var c in bn.Content)
                    ExpandInlineNodeToRuns(c, true, italic, runs);
                break;
            case ItalicNode it:
                foreach (var c in it.Content)
                    ExpandInlineNodeToRuns(c, bold, true, runs);
                break;
            case StrikethroughNode sn:
                runs.Add((FlattenInlines(sn.Content), RunStyle.Strikethrough, null, null));
                break;
            case CodeNode cn:
                runs.Add((cn.Content ?? "", RunStyle.Code, null, null));
                break;
            case LinkNode ln:
                runs.Add((ln.Text ?? "", CombineLinkStyle(bold, italic), ln.Url, null));
                break;
            case ImageNode img:
                runs.Add((img.Alt ?? "", RunStyle.Image, img.Url, null));
                break;
            case MathInlineNode math:
                runs.Add((math.LaTeX ?? "", RunStyle.Math, null, null));
                break;
            case FootnoteRefNode fn:
                runs.Add(("[^" + fn.Id + "]", RunStyle.FootnoteRef, null, null));
                break;
            case FootnoteMarkerNode fm:
            {
                var num = fm.Number.ToString();
                runs.Add(($"[{num}]", RunStyle.FootnoteRef, "footnote:" + num, fm.Id));
                break;
            }
        }
    }

    /// <summary>表格单元格内换行时使用的安全边距（px），避免测量舍入与 ITextMeasurer 的 +1 余量累积导致文本超出右边界。</summary>
    private const float TableCellBreakMargin = 6f;
    /// <summary>剩余宽度低于此值时本行不再接新内容，提前换行，避免“挤到边界再截断”的视觉效果。</summary>
    private const float TableCellMinSpaceBeforeWrap = 8f;

    private void LayoutTable(LayoutBlock block, TableNode t, float width, ref float x, ref float y)
    {
        const float cellPaddingV = 4f;
        const float cellPaddingH = 8f;
        const float cellPaddingHRight = 12f; // 右侧留出更大边距，避免文字贴线
        var lineHeight = _baseFontSize * _lineSpacing;
        var colCount = Math.Max(1, t.Headers.Count);
        var tableWidth = Math.Max(1, width - _blockInnerPadding * 2);

        var allRows = new List<List<string>> { t.Headers };
        allRows.AddRange(t.Rows);
        var rowCount = allRows.Count;

        var paint = _measurePaint;

        // 1) 按内容计算列宽：每列取该列所有单元格内容宽度上限（封顶、保底），再归一化到 tableWidth
        const float minColWidth = 32f;
        var maxColWidth = tableWidth * 0.55f;
        var desiredWidths = new float[colCount];
        for (int c = 0; c < colCount; c++)
            desiredWidths[c] = minColWidth;
        for (int r = 0; r < rowCount; r++)
        {
            var row = allRows[r];
            for (int c = 0; c < colCount; c++)
            {
                var cellText = c < row.Count ? (row[c] ?? "") : "";
                var runs = GetCellRuns(cellText);
                float w = 0;
                bool isHeader = r == 0;
                foreach (var (text, style, _) in runs)
                {
                    if (string.IsNullOrEmpty(text)) continue;
                    var effectiveStyle = GetEffectiveTableRunStyleForMeasure(style, isHeader);
                    w += MeasureTableRun(text, effectiveStyle);
                }
                // 列宽需包含左右 padding，避免归一化后 innerW 不足导致文字超出右边界
                w = Math.Min(maxColWidth, Math.Max(w + cellPaddingH + cellPaddingHRight, minColWidth));
                if (w > desiredWidths[c]) desiredWidths[c] = w;
            }
        }
        float sumDesired = 0;
        for (int c = 0; c < colCount; c++) sumDesired += desiredWidths[c];
        var colWidths = new float[colCount];
        if (sumDesired > 0)
        {
            for (int c = 0; c < colCount; c++)
                colWidths[c] = desiredWidths[c] * (tableWidth / sumDesired);
        }
        else
        {
            for (int c = 0; c < colCount; c++)
                colWidths[c] = (tableWidth - 1f) / colCount;
        }

        // 2) 按换行后的行数计算每行高度（含公式额外高度）
        var rowHeights = new float[rowCount];
        for (int r = 0; r < rowCount; r++)
        {
            var row = allRows[r];
            float rowH = lineHeight + cellPaddingV * 2;
            for (int c = 0; c < colCount; c++)
            {
                var cellText = c < row.Count ? (row[c] ?? "") : "";
                var innerW = Math.Max(0, colWidths[c] - cellPaddingH - cellPaddingHRight);
                var innerWForBreak = Math.Max(0, innerW - TableCellBreakMargin);
                var runs = GetCellRuns(cellText);
                var lines = BreakRunsIntoLines(runs, innerWForBreak, paint, r == 0, _tableTextMeasurer, TableCellMinSpaceBeforeWrap);
                float cellH = cellPaddingV * 2 + lines.Count * lineHeight;
                foreach (var n in MarkdownParser.ParseInline(cellText))
                {
                    if (n is MathInlineNode math && !string.IsNullOrWhiteSpace(math.LaTeX))
                    {
                        var (_, h, d) = MeasureMathMetrics(math.LaTeX);
                        cellH = Math.Max(cellH, (h + d) + cellPaddingV * 2);
                    }
                }
                if (cellH > rowH) rowH = cellH;
            }
            rowHeights[r] = rowH;
        }

        block.TableInfo = new TableLayoutInfo
        {
            ColumnWidths = colWidths,
            RowHeights = rowHeights,
            ColCount = colCount,
            RowCount = rowCount
        };

        int charOffset = 0;
        var left = _blockInnerPadding;
        for (int r = 0; r < rowCount; r++)
        {
            var row = allRows[r];
            var rowHeight = rowHeights[r];
            var line = new LayoutLine { Y = y, Height = rowHeight };
            float cellX = left;
            var isHeader = r == 0;
            for (int c = 0; c < colCount; c++)
            {
                var cellText = c < row.Count ? (row[c] ?? "") : "";
                var align = GetTableCellAlign(t, c);
                var cellLeft = cellX;
                var cellW = colWidths[c];
                var cellRight = cellX + cellW;
                var innerLeft = cellLeft + cellPaddingH;
                var innerRight = cellRight - cellPaddingHRight;
                var innerW = Math.Max(0, innerRight - innerLeft);
                var innerWForBreak = Math.Max(0, innerW - TableCellBreakMargin);

                var runs = GetCellRuns(cellText);
                var wrappedLines = BreakRunsIntoLines(runs, innerWForBreak, paint, isHeader, _tableTextMeasurer, TableCellMinSpaceBeforeWrap);
                if (wrappedLines.Count == 0)
                {
                    line.Runs.Add(new LayoutRun("", new SKRect(cellLeft, y, cellRight, y + rowHeight), isHeader ? RunStyle.TableHeaderCell : RunStyle.TableCell, block.BlockIndex, charOffset, null, align));
                    charOffset += 1;
                }
                else
                {
                    for (int lineIndex = 0; lineIndex < wrappedLines.Count; lineIndex++)
                    {
                        var runList = wrappedLines[lineIndex];
                        float lineY = y + cellPaddingV + lineIndex * lineHeight;
                        float totalLineW = 0;
                        foreach (var (text, style, _) in runList)
                        {
                            if (string.IsNullOrEmpty(text)) continue;
                            var effectiveStyle = GetEffectiveTableRunStyleForMeasure(style, isHeader);
                            totalLineW += MeasureTableRun(text, effectiveStyle);
                        }
                        float runX = align == TableCellAlign.Center
                            ? innerLeft + Math.Max(0, (innerW - totalLineW) / 2f)
                            : align == TableCellAlign.Right
                                ? innerRight - totalLineW
                                : innerLeft;
                        foreach (var (text, style, linkUrl) in runList)
                        {
                            if (string.IsNullOrEmpty(text)) continue;
                            var effectiveStyle = GetEffectiveTableRunStyleForMeasure(style, isHeader);
                            float w = MeasureTableRun(text, effectiveStyle);
                            var runStyle = isHeader ? RunStyle.TableHeaderCell : RunStyle.TableCell;
                            if (style == RunStyle.Math) runStyle = RunStyle.Math;
                            else if (style == RunStyle.Link) runStyle = RunStyle.Link;
                            else if (style == RunStyle.LinkBold) runStyle = RunStyle.LinkBold;
                            else if (style == RunStyle.LinkItalic) runStyle = RunStyle.LinkItalic;
                            else if (style == RunStyle.LinkBoldItalic) runStyle = RunStyle.LinkBoldItalic;
                            else if (style == RunStyle.Bold) runStyle = RunStyle.Bold;
                            else if (style == RunStyle.Italic) runStyle = RunStyle.Italic;
                            else if (style == RunStyle.BoldItalic) runStyle = RunStyle.BoldItalic;

                            // 将 run 右边界钳位到单元格内边界，并限制 runX 递进不超出 innerRight，避免舍入/测量余量导致文本溢出
                            var runRight = Math.Min(runX + w, innerRight);

                            // 普通文本仍按固定行高布置；数学公式则使用整行/整行高作为裁剪区域，
                            // 避免分数、根号等高度超过行高时被 ClipRect 截断。
                            SKRect rect;
                            if (runStyle == RunStyle.Math)
                            {
                                float mathTop = y + cellPaddingV;
                                float mathBottom = y + rowHeight - cellPaddingV;
                                rect = new SKRect(runX, mathTop, runRight, mathBottom);
                            }
                            else
                            {
                                rect = new SKRect(runX, lineY, runRight, lineY + lineHeight);
                            }

                            line.Runs.Add(new LayoutRun(text, rect, runStyle, block.BlockIndex, charOffset, linkUrl, align));
                            runX = runRight;
                            charOffset += text.Length;
                        }
                    }
                    charOffset += 1;
                }
                cellX += colWidths[c];
            }
            block.Lines.Add(line);
            y += rowHeight;
        }
        y += _paraSpacing;
    }

    private static List<(string text, RunStyle style, string? linkUrl)> GetCellRuns(string cellText)
    {
        var runs = new List<(string text, RunStyle style, string? linkUrl)>();
        foreach (var n in MarkdownParser.ParseInline(cellText))
        {
            var (text, style, linkUrl, _) = FlattenInlineStatic(n);
            if (string.IsNullOrEmpty(text)) continue;
            if (style == RunStyle.Image) continue;
            if (style == RunStyle.Math)
                runs.Add((text, RunStyle.Math, null));
            else
                runs.Add(
                    (
                        text,
                        style switch
                        {
                            RunStyle.Bold => RunStyle.Bold,
                            RunStyle.Italic => RunStyle.Italic,
                            RunStyle.BoldItalic => RunStyle.BoldItalic,
                            RunStyle.Link => RunStyle.Link,
                            RunStyle.LinkBold => RunStyle.LinkBold,
                            RunStyle.LinkItalic => RunStyle.LinkItalic,
                            RunStyle.LinkBoldItalic => RunStyle.LinkBoldItalic,
                            _ => RunStyle.Normal,
                        },
                        linkUrl
                    )
                );
        }
        return runs;
    }

    private static (string text, RunStyle style, string? linkUrl, bool _) FlattenInlineStatic(InlineNode n)
    {
        if (n is TextNode t) return (t.Content ?? "", RunStyle.Normal, null, false);
        if (n is BoldNode b)
        {
            if (b.Content.Count == 1 && b.Content[0] is ItalicNode loneIt)
            {
                var sb = new StringBuilder();
                foreach (var child in loneIt.Content)
                {
                    var (txt, _, _, _) = FlattenInlineStatic(child);
                    sb.Append(txt);
                }
                return (sb.ToString(), RunStyle.BoldItalic, null, false);
            }
            var sbBold = new StringBuilder();
            foreach (var child in b.Content) { var (txt, _, _, _) = FlattenInlineStatic(child); sbBold.Append(txt); }
            return (sbBold.ToString(), RunStyle.Bold, null, false);
        }
        if (n is ItalicNode i)
        {
            var sb = new StringBuilder();
            foreach (var child in i.Content) { var (txt, _, _, _) = FlattenInlineStatic(child); sb.Append(txt); }
            return (sb.ToString(), RunStyle.Italic, null, false);
        }
        if (n is StrikethroughNode s)
        {
            var sb = new StringBuilder();
            foreach (var child in s.Content) { var (txt, _, _, _) = FlattenInlineStatic(child); sb.Append(txt); }
            return (sb.ToString(), RunStyle.Strikethrough, null, false);
        }
        if (n is CodeNode cn) return (cn.Content ?? "", RunStyle.Code, null, false);
        if (n is LinkNode ln) return (ln.Text ?? "", RunStyle.Link, ln.Url, false);
        if (n is ImageNode img) return (img.Alt ?? "", RunStyle.Image, null, false);
        if (n is MathInlineNode m) return (m.LaTeX ?? "", RunStyle.Math, null, false);
        if (n is FootnoteRefNode fn) return (fn.Id ?? "", RunStyle.Normal, null, false);
        return ("", RunStyle.Normal, null, false);
    }

    /// <summary>
    /// 根据表格单元格所在行是否为表头，推导该段文本在实际渲染中应使用的样式：
    /// - 表头行中普通文本按 TableHeaderCell（加粗）测量；
    /// - 其他样式（如显式加粗/斜体/链接/公式）保持自身样式。
    /// </summary>
    private static RunStyle GetEffectiveTableRunStyleForMeasure(RunStyle originalStyle, bool isHeaderRow)
    {
        if (!isHeaderRow)
            return originalStyle;
        if (originalStyle is RunStyle.Normal or RunStyle.TableCell or RunStyle.TableHeaderCell)
            return RunStyle.TableHeaderCell;
        return originalStyle;
    }

    /// <summary>使用与渲染阶段完全一致的字体参数来测量指定样式的文本宽度。</summary>
    private float MeasureTextForStyle(string text, RunStyle style)
    {
        bool bold = IsRunStyleBold(style);
        bool italic = IsRunStyleItalic(style);
        bool code = style is RunStyle.Code;
        var font = GetFont(bold, italic, code);
        var w = font.MeasureText(text);
        LayoutDiagnostics.OnSkiaMeasureText();
        return w;
    }

    /// <summary>按 \n 切分并合并连续空行为最多 1 个空段（多换行最多显示 2 个）。</summary>
    private static List<string> SplitByNewlinesWithCollapse(string text)
    {
        var parts = text.Split('\n');
        var segments = new List<string>();
        int i = 0;
        while (i < parts.Length)
        {
            if (parts[i].Length > 0)
            {
                segments.Add(parts[i]);
                i++;
            }
            else
            {
                segments.Add("");
                while (i < parts.Length && parts[i].Length == 0)
                    i++;
            }
        }
        return segments;
    }

    /// <summary>
    /// 将段落/列表中的 inline runs 按 innerW 自动换行；单 \n 为换行，多 \n 最多 2 个（1 个空行）。
    /// 充分考虑不同 RunStyle（普通、粗体、行内代码、公式等）的真实宽度。
    /// </summary>
    private List<List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>> BreakInlineRunsIntoLines(
        List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)> runs,
        float innerW)
    {
        var result = new List<List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>>();
        var currentLine = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
        float currentLineW = 0;

        foreach (var (text, style, linkUrl, footnoteRefId) in runs)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            var effectiveStyle = style;

            if (effectiveStyle == RunStyle.Math)
            {
                float w = MeasureMathInline(text);
                if (currentLineW + w > innerW && currentLine.Count > 0)
                {
                    result.Add(currentLine);
                    currentLine = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                    currentLineW = 0;
                }
                currentLine.Add((text, style, linkUrl, footnoteRefId));
                currentLineW += w;
                continue;
            }

            var segments = SplitByNewlinesWithCollapse(text);
            for (int si = 0; si < segments.Count; si++)
            {
                var segment = segments[si];
                if (segment.Length == 0)
                {
                    if (currentLine.Count > 0)
                    {
                        result.Add(currentLine);
                        currentLine = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                        currentLineW = 0;
                    }
                    result.Add(new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>());
                    continue;
                }

                string remaining = segment;
                while (remaining.Length > 0)
                {
                    float spaceLeft = innerW - currentLineW;
                    var (prefix, suffix) = BreakTextToFit(remaining, spaceLeft, effectiveStyle);
                    if (string.IsNullOrEmpty(prefix) && currentLine.Count > 0)
                    {
                        result.Add(currentLine);
                        currentLine = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                        currentLineW = 0;
                        continue;
                    }
                    if (string.IsNullOrEmpty(prefix))
                    {
                        prefix = remaining.Length > 1 ? remaining.Substring(0, 1) : remaining;
                        suffix = remaining.Length > 1 ? remaining.Substring(1) : "";
                    }

                    currentLine.Add((prefix, style, linkUrl, footnoteRefId));
                    currentLineW += MeasureTextForStyle(prefix, effectiveStyle);
                    remaining = suffix;

                    if (remaining.Length > 0)
                    {
                        result.Add(currentLine);
                        currentLine = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                        currentLineW = 0;
                    }
                }

                if (si < segments.Count - 1)
                {
                    result.Add(currentLine);
                    currentLine = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                    currentLineW = 0;
                }
            }
        }

        if (currentLine.Count > 0)
            result.Add(currentLine);

        return result;
    }

    /// <summary>将单元格内 runs 按 innerW 自动换行，返回多行，每行为 (text, style, linkUrl) 列表。tableMeasurer 非空时用其测量以与渲染一致。minRemainingToWrap&gt;0 时剩余宽度低于该值则提前换行，不挤到边界。</summary>
    private List<List<(string text, RunStyle style, string? linkUrl)>> BreakRunsIntoLines(
        List<(string text, RunStyle style, string? linkUrl)> runs,
        float innerW,
        SKPaint paint,
        bool isHeaderRow,
        ITextMeasurer? tableMeasurer = null,
        float minRemainingToWrap = 0f)
    {
        var result = new List<List<(string text, RunStyle style, string? linkUrl)>>();
        var currentLine = new List<(string text, RunStyle style, string? linkUrl)>();
        float currentLineW = 0;
        foreach (var (text, style, linkUrl) in runs)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            var effectiveStyle = GetEffectiveTableRunStyleForMeasure(style, isHeaderRow);
            float Measure(string s) => tableMeasurer != null ? tableMeasurer.MeasureText(s, effectiveStyle) : (effectiveStyle == RunStyle.Math ? MeasureMathInline(s) : MeasureTextForStyle(s, effectiveStyle));

            if (minRemainingToWrap > 0 && currentLine.Count > 0 && (innerW - currentLineW) < minRemainingToWrap)
            {
                result.Add(currentLine);
                currentLine = new List<(string text, RunStyle style, string? linkUrl)>();
                currentLineW = 0;
            }

            if (effectiveStyle == RunStyle.Math)
            {
                float w = Measure(text);
                if (currentLineW + w > innerW && currentLine.Count > 0)
                {
                    result.Add(currentLine);
                    currentLine = new List<(string text, RunStyle style, string? linkUrl)>();
                    currentLineW = 0;
                }
                currentLine.Add((text, style, linkUrl));
                currentLineW += w;
                continue;
            }

            string remaining = text;
            while (remaining.Length > 0)
            {
                if (minRemainingToWrap > 0 && currentLine.Count > 0 && (innerW - currentLineW) < minRemainingToWrap)
                {
                    result.Add(currentLine);
                    currentLine = new List<(string text, RunStyle style, string? linkUrl)>();
                    currentLineW = 0;
                }
                float spaceLeft = innerW - currentLineW;
                var (prefix, suffix) = BreakTextToFit(remaining, spaceLeft, effectiveStyle, tableMeasurer);
                if (string.IsNullOrEmpty(prefix) && currentLine.Count > 0)
                {
                    result.Add(currentLine);
                    currentLine = new List<(string text, RunStyle style, string? linkUrl)>();
                    currentLineW = 0;
                    continue;
                }
                if (string.IsNullOrEmpty(prefix))
                {
                    prefix = remaining.Length > 1 ? remaining.Substring(0, 1) : remaining;
                    suffix = remaining.Length > 1 ? remaining.Substring(1) : "";
                }
                currentLine.Add((prefix, style, linkUrl));
                currentLineW += Measure(prefix);
                remaining = suffix;
                if (remaining.Length > 0)
                {
                    result.Add(currentLine);
                    currentLine = new List<(string text, RunStyle style, string? linkUrl)>();
                    currentLineW = 0;
                }
            }
        }
        if (currentLine.Count > 0)
            result.Add(currentLine);
        return result;
    }

    /// <summary>
    /// 将 text 在 maxWidth 内截断为可绘制的前缀与剩余部分。tableMeasurer 非空时用其测量以与渲染一致。
    /// 优先按“词”断行（空格/Tab 处），若一整个单词都放不下，则退化为字符级断行。
    /// </summary>
    private (string prefix, string suffix) BreakTextToFit(string text, float maxWidth, RunStyle style, ITextMeasurer? tableMeasurer = null)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return ("", text ?? "");

        if (tableMeasurer != null)
        {
            var limit = Math.Max(0, maxWidth - 0.5f);
            if (limit <= 0)
                return ("", text);
            if (tableMeasurer.MeasureText(text, style) <= limit)
                return (text, "");
            int lastGood = 0;
            int lastSpace = -1;
            for (int i = 1; i <= text.Length; i++)
            {
                float w = tableMeasurer.MeasureText(text.Substring(0, i), style);
                if (w <= limit)
                {
                    lastGood = i;
                    if (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t'))
                        lastSpace = i;
                }
                else
                    break;
            }
            if (lastGood == 0)
                return (text.Substring(0, 1), text.Substring(1));
            int breakPos = lastSpace > 0 ? lastSpace : lastGood;
            return (text.Substring(0, breakPos), text.Substring(breakPos));
        }

        var limitPaint = Math.Max(0, maxWidth - 2f);
        if (limitPaint <= 0)
            return ("", text);

        var font = GetFont(IsRunStyleBold(style), IsRunStyleItalic(style), style is RunStyle.Code);
        var prefixWidths = _prefixWidthCache.GetOrBuildPrefixWidths(text, style, font);
        if (prefixWidths != null)
        {
            if (prefixWidths[text.Length] <= limitPaint)
                return (text, "");
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (prefixWidths[mid] <= limitPaint)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            int lastGoodP = lo;
            if (lastGoodP == 0)
                return (text.Substring(0, 1), text.Substring(1));
            int lastSpaceP = -1;
            for (int i = lastGoodP; i >= 1; i--)
            {
                char ch = text[i - 1];
                if (ch == ' ' || ch == '\t')
                {
                    lastSpaceP = i;
                    break;
                }
            }
            int breakPosP = lastSpaceP > 0 ? lastSpaceP : lastGoodP;
            return (text.Substring(0, breakPosP), text.Substring(breakPosP));
        }

        if (font.MeasureText(text) <= limitPaint)
        {
            LayoutDiagnostics.OnSkiaMeasureText();
            return (text, "");
        }
        int lastGoodP2 = 0;
        int lastSpaceP2 = -1;
        for (int i = 1; i <= text.Length; i++)
        {
            float w = font.MeasureText(text.AsSpan(0, i));
            LayoutDiagnostics.OnSkiaMeasureText();
            if (w <= limitPaint)
            {
                lastGoodP2 = i;
                char ch = text[i - 1];
                if (ch == ' ' || ch == '\t')
                    lastSpaceP2 = i;
            }
            else
                break;
        }
        if (lastGoodP2 == 0)
            return (text.Substring(0, 1), text.Substring(1));
        int breakPosP2 = lastSpaceP2 > 0 ? lastSpaceP2 : lastGoodP2;
        return (text.Substring(0, breakPosP2), text.Substring(breakPosP2));
    }

    private (float width, float height, float depth) MeasureMathMetrics(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex)) return (0, 0, 0);
        var bodyTf = GetBodyTypeface();
        var mathTf = GetMathTypeface();
        var fontSize = MathBlockFontSize(_baseFontSize);
        return MarkdownEditor.Latex.MathSkiaRenderer.MeasureFormula(latex, bodyTf, mathTf, fontSize);
    }

    private float MeasureMathInline(string latex)
    {
        var (w, _, _) = MeasureMathMetrics(latex);
        // DrawFormula 在水平方向会为公式两侧各预留约 4px 内边距，
        // 这里在测量宽度时一并加上，避免背景块和后续文本侵入公式绘制区域。
        return w + 8f;
    }

    /// <summary>表格内 run 宽度：有 Measurer 时与渲染一致，避免裁切；否则用布局自身测量。</summary>
    private float MeasureTableRun(string text, RunStyle effectiveStyle)
    {
        if (_tableTextMeasurer != null)
            return _tableTextMeasurer.MeasureText(text, effectiveStyle);
        return effectiveStyle == RunStyle.Math ? MeasureMathInline(text) : MeasureTextForStyle(text, effectiveStyle);
    }

    private static TableCellAlign? GetTableCellAlign(TableNode t, int c)
    {
        if (t.ColumnAlignments == null || c >= t.ColumnAlignments.Count) return null;
        return t.ColumnAlignments[c] switch
        {
            TableAlign.Left => TableCellAlign.Left,
            TableAlign.Center => TableCellAlign.Center,
            TableAlign.Right => TableCellAlign.Right,
            _ => null
        };
    }

    private void LayoutBlockquote(LayoutBlock block, BlockquoteNode bq, float width, ref float x, ref float y)
    {
        block.Kind = BlockKind.Blockquote;
        y += _blockquotePadding;
        var indent = _blockInnerPadding + _blockquoteIndent;
        foreach (var child in bq.Children)
        {
            switch (child)
            {
                case ParagraphNode p:
                    x = indent;
                    LayoutParagraph(block, p, width - indent, ref x, ref y, indent);
                    break;
                case BulletListNode bl:
                    LayoutList(block, bl, width - indent, ref x, ref y, indent);
                    break;
                case OrderedListNode ol:
                    LayoutList(block, ol, width - indent, ref x, ref y, indent);
                    break;
                case CodeBlockNode c:
                    LayoutCodeBlock(block, c, width - indent, ref x, ref y);
                    break;
                case MathBlockNode m:
                    LayoutMathBlock(block, m, width - indent, ref x, ref y);
                    break;
            }
        }
        y += _blockquotePadding;
        y += _paraSpacing;
    }

    private void LayoutDefinitionList(LayoutBlock block, DefinitionListNode dl, float width, ref float x, ref float y)
    {
        var lineH = _baseFontSize * _lineSpacing;
        var defIndent = _blockInnerPadding + _definitionListIndent;
        var font = GetFont(false, false, false);
        int charOffset = 0;

        y += _blockInnerPadding;
        foreach (var item in dl.Items)
        {
            var termText = FlattenInlines(item.Term);
            var termLine = new LayoutLine { Y = y, Height = lineH };
            termLine.Runs.Add(new LayoutRun(termText, new SKRect(_blockInnerPadding, y, width - _blockInnerPadding, y + lineH), RunStyle.Bold, block.BlockIndex, charOffset));
            block.Lines.Add(termLine);
            y += lineH;
            charOffset += termText.Length + 1;

            foreach (var def in item.Definitions)
            {
                if (def is ParagraphNode p)
                {
                    var runs = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                    foreach (var n in p.Content)
                        ExpandInlineNodeToRuns(n, false, false, runs);
                    var fullTextSb = new StringBuilder();
                    for (int ri = 0; ri < runs.Count; ri++)
                        fullTextSb.Append(runs[ri].text);
                    var segments = BreakTextWithWrap(fullTextSb.ToString(), width - defIndent - _blockInnerPadding, font);
                    int off = 0;
                    foreach (var seg in segments)
                    {
                        var line = new LayoutLine { Y = y, Height = lineH };
                        line.Runs.Add(new LayoutRun(seg, new SKRect(defIndent, y, width - _blockInnerPadding, y + lineH), RunStyle.Normal, block.BlockIndex, charOffset + off));
                        block.Lines.Add(line);
                        y += lineH;
                        off += seg.Length + 1;
                    }
                    charOffset += fullTextSb.Length + 1;
                }
                else if (def is CodeBlockNode c)
                {
                    LayoutCodeBlock(block, c, width, ref x, ref y);
                }
            }
        }
        y += _blockInnerPadding;
        y += _paraSpacing;
    }

    /// <summary>列表项悬挂缩进：符号/序号单独在左侧，多行内容左侧对齐。</summary>
    private void LayoutList(LayoutBlock block, MarkdownNode node, float width, ref float x, ref float y, float leftMargin = 0f)
    {
        y += _listBlockMarginTop;
        var bullet = node as BulletListNode;
        var ordered = node as OrderedListNode;
        var items = bullet is not null ? bullet.Items : ordered!.Items;
        var lineHeight = _baseFontSize * _lineSpacing;
        var contentLeft = leftMargin + _blockInnerPadding + _listItemIndent;
        var font = GetFont(false, false, false);
        int charOffset = 0;
        int number = ordered?.StartNumber ?? 1;
        for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var item = items[itemIndex];
            // 1) 前缀 run（• / 1. / [ ] 或组合）及宽度，用于悬挂缩进
            var prefixRuns = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
            if (ordered is not null)
            {
                prefixRuns.Add((number.ToString() + ". ", RunStyle.Normal, null, null));
                number++;
            }
            else if (!item.IsTask)
            {
                prefixRuns.Add(("• ", RunStyle.Normal, null, null));
            }
            if (item.IsTask)
            {
                prefixRuns.Add((item.IsChecked ? "[x] " : "[ ] ", RunStyle.Normal, "todo-toggle:", null));
            }

            float prefixW = 0;
            foreach (var pr in prefixRuns)
                prefixW += font.MeasureText(pr.text);
            var contentStart = contentLeft + prefixW;
            var availW = width - contentStart - _blockInnerPadding;
            if (availW <= 0) availW = width - contentLeft;

            // 2) 仅内容 runs（不含前缀），按剩余宽度换行
            var contentRuns = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
            if (item.Content.Count > 0 && item.Content[0] is ParagraphNode p)
            {
                foreach (var n in p.Content)
                    ExpandInlineNodeToRuns(n, false, false, contentRuns);
            }

            if (contentRuns.Count == 0)
                continue;

            var wrappedLines = BreakInlineRunsIntoLines(contentRuns, availW);

            for (int i = 0; i < wrappedLines.Count; i++)
            {
                List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)> lineRuns;
                float lineLeft = contentLeft;
                if (i == 0)
                {
                    // 首行：左侧画前缀，再画内容，整体从左边界 contentLeft 开始
                    lineRuns = new List<(string, RunStyle, string?, string?)>(prefixRuns);
                    lineRuns.AddRange(wrappedLines[i]);
                }
                else
                {
                    // 续行：悬挂缩进，与首行文字左对齐（从 contentStart 开始）
                    lineLeft = contentStart;
                    lineRuns = wrappedLines[i];
                }
                FlushParagraphLine(block, lineRuns, lineHeight, ref y, block.BlockIndex, ref charOffset, lineLeft);
            }
            if (itemIndex < items.Count - 1)
                y += _listItemGap;
        }
        y += _paraSpacing + _listBlockMarginBottom;
    }

    private (string text, RunStyle style, string? linkUrl, string? footnoteRefId) FlattenInline(InlineNode n)
    {
        if (n is FootnoteMarkerNode fm)
        {
            var num = fm.Number.ToString();
            return ("[" + num + "]", RunStyle.FootnoteRef, "footnote:" + num, fm.Id);
        }
        return n switch
        {
            TextNode tn => (tn.Content, RunStyle.Normal, null, null),
            BoldNode bn => (FlattenInlines(bn.Content), RunStyle.Bold, null, null),
            ItalicNode inNode => (FlattenInlines(inNode.Content), RunStyle.Italic, null, null),
            StrikethroughNode sn => (FlattenInlines(sn.Content), RunStyle.Strikethrough, null, null),
            CodeNode cn => (cn.Content, RunStyle.Code, null, null),
            LinkNode ln => (ln.Text, RunStyle.Link, ln.Url, null),
            ImageNode img => (img.Alt, RunStyle.Image, img.Url, null),
            MathInlineNode math => (math.LaTeX ?? "", RunStyle.Math, null, null),
            _ => ("", RunStyle.Normal, null, null)
        };
    }

    public int MeasureTextOffset(string text, float x, RunStyle style)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var font = GetFont(IsRunStyleBold(style), IsRunStyleItalic(style), style == RunStyle.Code);
        if (style is RunStyle.Heading1 or RunStyle.Heading2 or RunStyle.Heading3
            or RunStyle.Heading4 or RunStyle.Heading5 or RunStyle.Heading6)
            font = new SKFont(SKTypeface.FromFamilyName(GetBodyTypeface().FamilyName, SKFontStyle.Bold), style switch
            {
                RunStyle.Heading1 => 28,
                RunStyle.Heading2 => 24,
                RunStyle.Heading3 => 20,
                RunStyle.Heading4 => 18,
                RunStyle.Heading5 => 16,
                RunStyle.Heading6 => 14,
                _ => 20
            });
        for (int i = 1; i <= text.Length; i++)
        {
            if (font.MeasureText(text.AsSpan(0, i)) >= x) return i - 1;
        }
        return text.Length;
    }

    #region ILayoutEnvironment（块级布局统一环境，供 IBlockLayouter 使用）

    float ILayoutEnvironment.BaseFontSize => _baseFontSize;
    float ILayoutEnvironment.LineSpacing => _lineSpacing;
    float ILayoutEnvironment.ParaSpacing => _paraSpacing;
    float ILayoutEnvironment.BlockInnerPadding => _blockInnerPadding;
    float ILayoutEnvironment.CodeBlockPadding => _codeBlockPadding;
    float ILayoutEnvironment.BlockquotePadding => _blockquotePadding;
    float ILayoutEnvironment.BlockquoteIndent => _blockquoteIndent;
    float ILayoutEnvironment.ListItemIndent => _listItemIndent;
    float ILayoutEnvironment.ListBlockMarginTop => _listBlockMarginTop;
    float ILayoutEnvironment.ListBlockMarginBottom => _listBlockMarginBottom;
    float ILayoutEnvironment.ListItemGap => _listItemGap;
    float ILayoutEnvironment.FootnoteTopMargin => _footnoteTopMargin;
    float ILayoutEnvironment.DefinitionListIndent => _definitionListIndent;

    SKTypeface ILayoutEnvironment.GetBodyTypeface() => GetBodyTypeface();
    SKTypeface ILayoutEnvironment.GetMathTypeface() => GetMathTypeface();
    SKFont ILayoutEnvironment.GetFont(bool bold, bool italic, bool code) => GetFont(bold, italic, code);
    SKPaint ILayoutEnvironment.GetMeasurePaint() => _measurePaint;

    (float, float, float) ILayoutEnvironment.MeasureMathMetrics(string latex) => MeasureMathMetrics(latex);
    float ILayoutEnvironment.MeasureMathInline(string latex) => MeasureMathInline(latex);
    (float, float) ILayoutEnvironment.GetImageIntrinsicSize(string url, float maxWidth) => GetImageIntrinsicSize(url, maxWidth);

    IReadOnlyList<string> ILayoutEnvironment.BreakTextWithWrap(string text, float maxWidth, SKFont font)
        => BreakTextWithWrap(text, maxWidth, font);

    string ILayoutEnvironment.FlattenInlines(IReadOnlyList<InlineNode> nodes)
        => FlattenInlines(nodes is List<InlineNode> list ? list : nodes.ToList());

    (string, RunStyle, string?, string?) ILayoutEnvironment.FlattenInline(InlineNode n) => FlattenInline(n);

    int ILayoutEnvironment.MeasureTextOffset(string text, float x, RunStyle style) => MeasureTextOffset(text, x, style);

    #endregion
}

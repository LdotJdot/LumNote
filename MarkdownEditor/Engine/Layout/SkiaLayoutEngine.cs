using System.Text;
using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Render;
using SkiaSharp;
using System.IO;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 基于 Skia 的布局引擎 - 精确测量，CJK 换行，配置字体
/// </summary>
public sealed class SkiaLayoutEngine : ILayoutEngine
{
    private readonly string _bodyFontFamily;
    private readonly string _codeFontFamily;
    private readonly string _mathFontFamily;
    private readonly string? _mathFontFilePath;
    private readonly float _baseFontSize;
    private readonly float _lineSpacing;
    private readonly float _paraSpacing;
    private readonly IImageLoader _imageLoader;
    private SKTypeface? _bodyTypeface;
    private SKFont? _fontCache;
    private SKFont? _boldFontCache;
    private SKFont? _italicFontCache;
    private SKFont? _codeFontCache;
    private SKTypeface? _mathTypeface;
    private IReadOnlyList<string>? _currentFootnoteRefOrder;

    public bool RequiresRelayoutOnWidthChange => true;

    public SkiaLayoutEngine(EngineConfig? config = null, IImageLoader? imageLoader = null)
    {
        config ??= new EngineConfig();
        _imageLoader = imageLoader ?? new DefaultImageLoader();
        _bodyFontFamily = config.BodyFontFamily;
        _codeFontFamily = config.CodeFontFamily;
        _mathFontFamily = config.MathFontFamily;
        _mathFontFilePath = config.MathFontFilePath;
        _baseFontSize = config.BaseFontSize;
        _lineSpacing = config.LineSpacing;
        _paraSpacing = config.ParaSpacing;
    }

    public SkiaLayoutEngine(SKTypeface? typeface, float baseFontSize = 16, float lineSpacing = 1.4f, float paraSpacing = 8f)
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

        // 1) 优先：显式指定的数学字体文件
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

        // 2) 其次：MathFontFamily 中配置的数学字体家族列表
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

        // 3) 回退到正文字体
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

    public LayoutBlock Layout(MarkdownNode node, float width, int blockIndex, int startLine, int endLine, IReadOnlyList<string>? footnoteRefOrder = null)
    {
        _currentFootnoteRefOrder = footnoteRefOrder;
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
            case EmptyLineNode el:
                y += _baseFontSize * _lineSpacing * Math.Max(1, el.LineCount - 1);
                break;
            default:
                block.Bounds = new SKRect(0, 0, width, _baseFontSize * _lineSpacing);
                break;
        }

        block.Bounds = new SKRect(0, 0, width, y);
        return block;
    }

    public LayoutBlock LayoutFootnoteSection(IReadOnlyList<string> refOrder, IReadOnlyDictionary<string, FootnoteDefNode> defs, IReadOnlyDictionary<string, List<(int blockIndex, int charOffset)>> refPositionsById, float width, int blockIndex)
    {
        var block = new LayoutBlock { BlockIndex = blockIndex, Kind = BlockKind.Footnotes };
        if (refOrder.Count == 0)
        {
            block.Bounds = new SKRect(0, 0, width, 0);
            return block;
        }
        var lineH = _baseFontSize * _lineSpacing;
        var font = GetFont(false, false, false);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        var spaceWidth = paint.MeasureText(" ");
        // 顶部留白（分隔线由 DrawFootnotesStyle 在 y=0 绘制），然后开始列表
        float y = lineH * 0.8f;
        int charOffset = 0;
        var fnIndent = BlockInnerPadding + 24f;
        const string backArrow = " \u2191 "; // ↑ 回顶，带间距避免过窄；↩ 在部分字体下为方框故改用 ↑
        for (int i = 0; i < refOrder.Count; i++)
        {
            var id = refOrder[i];
            if (!defs.TryGetValue(id, out var fn))
                continue;
            var num = (i + 1).ToString();
            var prefix = num + ". ";
            int lineCountBefore = block.Lines.Count;
            foreach (var child in fn.Content)
            {
                if (child is ParagraphNode p)
                {
                    var content = FlattenInlinesForFootnote(p.Content);
                    var fullText = prefix + content;
                    prefix = "";
                    var segments = BreakTextWithWrap(fullText, width - fnIndent - BlockInnerPadding, paint);
                    foreach (var seg in segments)
                    {
                        var textW = paint.MeasureText(seg);
                        var line = new LayoutLine { Y = y, Height = lineH };
                        line.Runs.Add(new LayoutRun(seg, new SKRect(fnIndent, y, fnIndent + textW, y + lineH), RunStyle.Normal, blockIndex, charOffset));
                        block.Lines.Add(line);
                        y += lineH;
                        charOffset += seg.Length + 1;
                    }
                }
            }
            if (refPositionsById != null && refPositionsById.TryGetValue(id, out var positions) && positions.Count > 0)
            {
                LayoutLine targetLine;
                float x;
                if (block.Lines.Count > lineCountBefore)
                {
                    targetLine = block.Lines[^1];
                    x = targetLine.Runs.Count > 0
                        ? targetLine.Runs[^1].Bounds.Right + spaceWidth
                        : fnIndent;
                }
                else
                {
                    targetLine = new LayoutLine { Y = y, Height = lineH };
                    block.Lines.Add(targetLine);
                    x = fnIndent;
                    y += lineH;
                }
                var arrowW = paint.MeasureText(backArrow);
                foreach (var (bi, co) in positions)
                {
                    var linkUrl = "footnote-back:" + bi + ":" + co;
                    targetLine.Runs.Add(new LayoutRun(backArrow, new SKRect(x, targetLine.Y, x + arrowW, targetLine.Y + lineH), RunStyle.Link, blockIndex, charOffset, linkUrl));
                    x += arrowW + spaceWidth * 0.5f;
                    charOffset += backArrow.Length;
                }
            }
            y += lineH * 0.6f;
        }
        block.Bounds = new SKRect(0, 0, width, y);
        return block;
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
        }
        return sb.ToString();
    }

    private void LayoutHeading(LayoutBlock block, HeadingNode h, float width, ref float x, ref float y)
    {
        var fontSize = h.Level switch { 1 => 28, 2 => 24, 3 => 20, 4 => 18, 5 => 16, _ => 14 };
        var plain = FlattenInlines(h.Content);
        var font = new SKFont(SKTypeface.FromFamilyName(GetBodyTypeface().FamilyName, SKFontStyle.Bold), fontSize);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        var lineH = fontSize * _lineSpacing;
        var innerW = width - BlockInnerPadding * 2;
        var segments = BreakTextWithWrap(plain, innerW, paint);
        foreach (var seg in segments)
        {
            var line = new LayoutLine { Y = y, Height = lineH };
            var headingStyle = (RunStyle)((int)RunStyle.Heading1 + Math.Clamp(h.Level, 1, 6) - 1);
            line.Runs.Add(new LayoutRun(seg, new SKRect(BlockInnerPadding, y, width - BlockInnerPadding, y + lineH), headingStyle, block.BlockIndex, 0));
            block.Lines.Add(line);
            y += lineH;
        }
        y += _paraSpacing;
    }

    private void LayoutParagraph(LayoutBlock block, ParagraphNode p, float width, ref float x, ref float y, float leftMargin = 0f)
    {
        var innerLeft = leftMargin > 0 ? leftMargin : BlockInnerPadding;
        var innerWidth = width - innerLeft - (leftMargin > 0 ? 0 : BlockInnerPadding);

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
            var (text, style, linkUrl, footnoteRefId) = FlattenInline(n);
            if (!string.IsNullOrEmpty(text))
                runs.Add((text, style, linkUrl, footnoteRefId));
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
        var fullText = string.Concat(runs.Select(r => r.text));
        var font = GetFont(false, false, false);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        var lineSegments = BreakTextIntoLinesWithOffsets(fullText, innerWidth, paint);
        foreach (var (lineStr, lineStart, lineEnd) in lineSegments)
        {
            var lineRuns = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
            int runOffset = 0;
            foreach (var (text, style, linkUrl, footnoteRefId) in runs)
            {
                int runEnd = runOffset + text.Length;
                if (runEnd <= lineStart) { runOffset = runEnd; continue; }
                if (runOffset >= lineEnd) break;
                int oStart = Math.Max(runOffset, lineStart);
                int oEnd = Math.Min(runEnd, lineEnd);
                var seg = text[(oStart - runOffset)..(oEnd - runOffset)];
                lineRuns.Add((seg, style, linkUrl, footnoteRefId));
                runOffset = runEnd;
                if (runOffset >= lineEnd) break;
            }
            int offset = totalCharOffset + lineStart;
            FlushParagraphLine(block, lineRuns, lineH, ref y, block.BlockIndex, ref offset, innerLeft);
            totalCharOffset = offset;
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
            float w;
            var font = GetFont(style == RunStyle.Bold, style == RunStyle.Italic, style == RunStyle.Code);
            using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
            w = paint.MeasureText(text);
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
                using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
                w = Math.Max(120, Math.Min(400, paint.MeasureText(text) + 24));
            }
            else
            {
                var font = GetFont(style == RunStyle.Bold, style == RunStyle.Italic, style == RunStyle.Code);
                using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
                w = paint.MeasureText(text);
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
        var availW = width - (leftMargin > 0 ? leftMargin : BlockInnerPadding) - BlockInnerPadding - 8f;
        var (targetWidth, targetHeight) = GetImageIntrinsicSize(img.Url ?? "", Math.Max(80f, availW));
        var left = leftMargin > 0 ? leftMargin : BlockInnerPadding;
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
        var bmp = _imageLoader.TryGetImage(url);
        if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
        {
            float w = bmp.Width;
            float h = bmp.Height;
            if (w > contentWidth)
            {
                var scale = contentWidth / w;
                w = contentWidth;
                h *= scale;
            }
            return (w, h);
        }
        return (contentWidth, _baseFontSize * 6f);
    }

    /// <summary>
    /// Markdown 换行规则：单个 \n 不换行（当空格）；行尾两空格+换行 "  \n" 为硬换行；空行在块级已处理。
    /// 支持 CJK 的断行，西文优先在空格处断。
    /// 返回 (行内容, 在原文中的起始索引, 在原文中的结束索引)
    /// </summary>
    private static List<(string line, int start, int end)> BreakTextIntoLinesWithOffsets(string text, float maxWidth, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var result = new List<(string, int, int)>();
        var effectiveMaxWidth = maxWidth <= 0 ? float.MaxValue : maxWidth;

        // 按 Markdown 规则切分逻辑行：
        // 1) "  \n" 行尾两空格+换行 = 硬换行  2) "\" + 换行 = 硬换行  3) 单个 \n = 软换行，当空格
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
                sb.Append(' ');
                i++;
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
                int end = FindBreakEnd(content, start, effectiveMaxWidth, paint);
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

    private static List<string> BreakTextWithWrap(string text, float maxWidth, SKPaint paint)
    {
        return BreakTextIntoLinesWithOffsets(text, maxWidth, paint).Select(t => t.line).ToList();
    }

    private static int FindBreakEnd(string text, int start, float maxWidth, SKPaint paint)
    {
        if (start >= text.Length) return start;
        int len = text.Length - start;
        if (len == 1) return start + 1;
        int lastGood = start;
        int lastSpace = -1;
        for (int i = start + 1; i <= text.Length; i++)
        {
            var span = text.AsSpan(start, i - start);
            float w = paint.MeasureText(span);
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
    private const float CodeBlockPadding = 16f;

    private void LayoutCodeBlock(LayoutBlock block, CodeBlockNode c, float width, ref float x, ref float y)
    {
        var font = GetFont(false, false, true);
        var lineHeight = _baseFontSize * 0.95f * _lineSpacing;
        var lines = c.Code.Split('\n');
        int charOffset = 0;

        y += CodeBlockPadding;
        var left = CodeBlockPadding;
        var right = width - CodeBlockPadding;

        foreach (var codeLine in lines)
        {
            var run = new LayoutRun(codeLine, new SKRect(left, y, right, y + lineHeight), RunStyle.Code, block.BlockIndex, charOffset);
            var layoutLine = new LayoutLine { Y = y, Height = lineHeight };
            layoutLine.Runs.Add(run);
            block.Lines.Add(layoutLine);
            y += lineHeight;
            charOffset += codeLine.Length + 1; // +1 for \n
        }

        y += CodeBlockPadding;
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

        y += CodeBlockPadding;
        var left = CodeBlockPadding;
        var right = width - CodeBlockPadding;

        foreach (var codeLine in lines)
        {
            var run = new LayoutRun(codeLine, new SKRect(left, y, right, y + lineHeight), RunStyle.Code, block.BlockIndex, charOffset);
            var layoutLine = new LayoutLine { Y = y, Height = lineHeight };
            layoutLine.Runs.Add(run);
            block.Lines.Add(layoutLine);
            y += lineHeight;
            charOffset += codeLine.Length + 1;
        }

        y += CodeBlockPadding;
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
        }
        return sb.ToString();
    }

    private void LayoutTable(LayoutBlock block, TableNode t, float width, ref float x, ref float y)
    {
        const float cellPaddingV = 4f;
        var lineHeight = _baseFontSize * _lineSpacing;
        var colCount = Math.Max(1, t.Headers.Count);
        var tableWidth = width - BlockInnerPadding * 2;
        var colWidth = (tableWidth - 1f) / colCount;

        var allRows = new List<List<string>> { t.Headers };
        allRows.AddRange(t.Rows);
        var rowCount = allRows.Count;

        var colWidths = new float[colCount];
        for (int c = 0; c < colCount; c++)
            colWidths[c] = colWidth;
        var rowHeights = new float[rowCount];

        // 先根据每一行中是否包含数学公式，预估每行所需的高度，避免表格内公式被裁切。
        for (int r = 0; r < rowCount; r++)
        {
            float rowHeight = lineHeight + cellPaddingV * 2;
            var row = allRows[r];
            foreach (var cell in row)
            {
                if (string.IsNullOrWhiteSpace(cell))
                    continue;
                var inlinesForHeight = MarkdownParser.ParseInline(cell);
                foreach (var n in inlinesForHeight)
                {
                    if (n is MathInlineNode math && !string.IsNullOrWhiteSpace(math.LaTeX))
                    {
                        var (_, h, d) = MeasureMathMetrics(math.LaTeX);
                        var needed = (h + d) + cellPaddingV * 2;
                        if (needed > rowHeight)
                            rowHeight = needed;
                    }
                }
            }
            rowHeights[r] = rowHeight;
        }

        block.TableInfo = new TableLayoutInfo
        {
            ColumnWidths = colWidths,
            RowHeights = rowHeights,
            ColCount = colCount,
            RowCount = rowCount
        };

        const float cellPaddingH = 8f;
        int charOffset = 0;
        var left = BlockInnerPadding;
        var font = GetFont(false, false, false);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };

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
                var innerRight = cellRight - cellPaddingH;
                var innerW = Math.Max(0, innerRight - innerLeft);

                var inlines = MarkdownParser.ParseInline(cellText);
                var runs = new List<(string text, RunStyle style, string? linkUrl)>();
                foreach (var n in inlines)
                {
                    var (text, style, linkUrl, _) = FlattenInline(n);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (style == RunStyle.Image) continue;
                    if (style == RunStyle.Math)
                        runs.Add((text, RunStyle.Math, null));
                    else
                        runs.Add((text, style == RunStyle.Bold ? RunStyle.Bold : style == RunStyle.Italic ? RunStyle.Italic : style == RunStyle.Link ? RunStyle.Link : RunStyle.Normal, linkUrl));
                }
                if (runs.Count == 0)
                {
                    line.Runs.Add(new LayoutRun("", new SKRect(cellLeft, y, cellRight, y + rowHeight), isHeader ? RunStyle.TableHeaderCell : RunStyle.TableCell, block.BlockIndex, charOffset, null, align));
                    charOffset += 1;
                }
                else
                {
                    float runX = innerLeft;
                    float totalW = 0;
                    foreach (var (text, style, linkUrl) in runs)
                    {
                        float w = style == RunStyle.Math ? MeasureMathInline(text) : paint.MeasureText(text);
                        totalW += w;
                    }
                    if (align == TableCellAlign.Center)
                        runX = innerLeft + Math.Max(0, (innerW - totalW) / 2f);
                    else if (align == TableCellAlign.Right)
                        runX = innerRight - totalW;

                    foreach (var (text, style, linkUrl) in runs)
                    {
                        float w = style == RunStyle.Math ? MeasureMathInline(text) : paint.MeasureText(text);
                        var runStyle = isHeader ? RunStyle.TableHeaderCell : RunStyle.TableCell;
                        if (style == RunStyle.Math)
                            runStyle = RunStyle.Math;
                        else if (style == RunStyle.Link)
                            runStyle = RunStyle.Link;
                        else if (style == RunStyle.Bold)
                            runStyle = RunStyle.Bold;
                        else if (style == RunStyle.Italic)
                            runStyle = RunStyle.Italic;
                        var rect = new SKRect(runX, y + cellPaddingV, runX + w, y + rowHeight - cellPaddingV);
                        line.Runs.Add(new LayoutRun(text, rect, runStyle, block.BlockIndex, charOffset, linkUrl, align));
                        runX += w;
                        charOffset += text.Length;
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
        const float BlockquotePadding = 8f;
        const float BlockquoteIndent = 14f;
        y += BlockquotePadding;
        var indent = BlockInnerPadding + BlockquoteIndent;
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
        y += BlockquotePadding;
        y += _paraSpacing;
    }

    private void LayoutDefinitionList(LayoutBlock block, DefinitionListNode dl, float width, ref float x, ref float y)
    {
        var lineH = _baseFontSize * _lineSpacing;
        var defIndent = BlockInnerPadding + 24f;
        var font = GetFont(false, false, false);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        int charOffset = 0;

        y += BlockInnerPadding;
        foreach (var item in dl.Items)
        {
            var termText = FlattenInlines(item.Term);
            var termLine = new LayoutLine { Y = y, Height = lineH };
            termLine.Runs.Add(new LayoutRun(termText, new SKRect(BlockInnerPadding, y, width - BlockInnerPadding, y + lineH), RunStyle.Bold, block.BlockIndex, charOffset));
            block.Lines.Add(termLine);
            y += lineH;
            charOffset += termText.Length + 1;

            foreach (var def in item.Definitions)
            {
                if (def is ParagraphNode p)
                {
                    var runs = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                    foreach (var n in p.Content)
                    {
                        var (text, style, linkUrl, _) = FlattenInline(n);
                        if (!string.IsNullOrEmpty(text))
                            runs.Add((text, style, linkUrl, null));
                    }
                    var fullText = string.Concat(runs.Select(r => r.text));
                    var segments = BreakTextWithWrap(fullText, width - defIndent - BlockInnerPadding, paint);
                    int off = 0;
                    foreach (var seg in segments)
                    {
                        var line = new LayoutLine { Y = y, Height = lineH };
                        line.Runs.Add(new LayoutRun(seg, new SKRect(defIndent, y, width - BlockInnerPadding, y + lineH), RunStyle.Normal, block.BlockIndex, charOffset + off));
                        block.Lines.Add(line);
                        y += lineH;
                        off += seg.Length + 1;
                    }
                    charOffset += fullText.Length + 1;
                }
                else if (def is CodeBlockNode c)
                {
                    LayoutCodeBlock(block, c, width, ref x, ref y);
                }
            }
        }
        y += BlockInnerPadding;
        y += _paraSpacing;
    }

    private void LayoutFootnoteDef(LayoutBlock block, FootnoteDefNode fn, float width, ref float x, ref float y)
    {
        var lineH = _baseFontSize * _lineSpacing;
        var prefix = "[^" + fn.Id + "]: ";
        var font = GetFont(false, false, false);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        int charOffset = 0;

        foreach (var child in fn.Content)
        {
            if (child is ParagraphNode p)
            {
                var content = FlattenInlines(p.Content);
                var fullText = prefix + content;
                prefix = "";
                var segments = BreakTextWithWrap(fullText, width, paint);
                foreach (var seg in segments)
                {
                    var line = new LayoutLine { Y = y, Height = lineH };
                    line.Runs.Add(new LayoutRun(seg, new SKRect(0, y, width, y + lineH), RunStyle.Normal, block.BlockIndex, charOffset));
                    block.Lines.Add(line);
                    y += lineH;
                    charOffset += seg.Length + 1;
                }
            }
        }
        y += _paraSpacing;
    }

    /// <summary>列表项内容相对左侧的缩进（约 2 字符宽），与引用/嵌套区分开。</summary>
    private const float ListItemIndent = 20f;

    /// <summary>所有块内部左右留白，避免内容贴边。</summary>
    private const float BlockInnerPadding = 12f;

    private void LayoutList(LayoutBlock block, MarkdownNode node, float width, ref float x, ref float y, float leftMargin = 0f)
    {
        var items = node is BulletListNode bl ? bl.Items : ((OrderedListNode)node).Items;
        var lineHeight = _baseFontSize * _lineSpacing;
        var contentLeft = leftMargin + BlockInnerPadding + ListItemIndent;
        var availW = width - contentLeft - BlockInnerPadding;
        if (availW <= 0) availW = width - leftMargin;
        var font = GetFont(false, false, false);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        int charOffset = 0;
        foreach (var item in items)
        {
            var runs = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
            runs.Add(("• ", RunStyle.Normal, null, null));
            if (item.Content.Count > 0 && item.Content[0] is ParagraphNode p)
            {
                foreach (var n in p.Content)
                {
                    var (text, style, linkUrl, footnoteRefId) = FlattenInline(n);
                    if (!string.IsNullOrEmpty(text))
                        runs.Add((text, style, linkUrl, footnoteRefId));
                }
            }
            if (runs.Count == 0)
                continue;
            var fullText = string.Concat(runs.Select(r => r.text));
            var lineSegments = BreakTextIntoLinesWithOffsets(fullText, availW, paint);
            foreach (var (_, lineStart, lineEnd) in lineSegments)
            {
                var lineRuns = new List<(string text, RunStyle style, string? linkUrl, string? footnoteRefId)>();
                int runOffset = 0;
                foreach (var (text, style, linkUrl, footnoteRefId) in runs)
                {
                    int runEnd = runOffset + text.Length;
                    if (runEnd <= lineStart) { runOffset = runEnd; continue; }
                    if (runOffset >= lineEnd) break;
                    int oStart = Math.Max(runOffset, lineStart);
                    int oEnd = Math.Min(runEnd, lineEnd);
                    var seg = text[(oStart - runOffset)..(oEnd - runOffset)];
                    lineRuns.Add((seg, style, linkUrl, footnoteRefId));
                    runOffset = runEnd;
                    if (runOffset >= lineEnd) break;
                }
                FlushParagraphLine(block, lineRuns, lineHeight, ref y, block.BlockIndex, ref charOffset, contentLeft);
            }
        }
        y += _paraSpacing;
    }

    private (string text, RunStyle style, string? linkUrl, string? footnoteRefId) FlattenInline(InlineNode n)
    {
        if (n is FootnoteRefNode fn)
        {
            if (_currentFootnoteRefOrder != null)
            {
                int idx = -1;
                for (int i = 0; i < _currentFootnoteRefOrder.Count; i++)
                {
                    if (_currentFootnoteRefOrder[i] == fn.Id) { idx = i; break; }
                }
                var num = (idx >= 0 ? idx + 1 : 0).ToString();
                return ("[" + num + "]", RunStyle.FootnoteRef, "footnote:" + num, fn.Id);
            }
            return ("[^" + fn.Id + "]", RunStyle.Normal, null, null);
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
        var font = GetFont(style == RunStyle.Bold || style == RunStyle.TableHeaderCell, style == RunStyle.Italic || style == RunStyle.Math, style == RunStyle.Code);
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
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        for (int i = 1; i <= text.Length; i++)
        {
            if (paint.MeasureText(text.AsSpan(0, i)) >= x) return i - 1;
        }
        return text.Length;
    }
}

using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;
using MarkdownEditor.Latex;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// Skia 渲染器 - 批量绘制，Font 缓存；实现 ITextMeasurer 供表格布局使用以与绘制宽度一致。
/// </summary>
public sealed class SkiaRenderer : ITextMeasurer
{
    private const string EmbeddedMathFontResourceName = "MarkdownEditor.asserts.otf.latinmodern-math.otf";
    private readonly SKPaint _textPaint;
    private readonly SKPaint _codeBgPaint;
    private readonly SKPaint _selectionPaint;
    private readonly SKPaint _imagePlaceholderPaint;
    private readonly SKPaint _tableBorderPaint;
    private readonly SKPaint _tableHeaderBgPaint;
    private readonly uint _codeKeywordColor;
    private readonly uint _codeStringColor;
    private readonly uint _codeCommentColor;
    private readonly uint _codeNumberColor;
    private readonly uint _codeDefaultColor;
    private readonly uint _linkColor;
    private readonly string _bodyFontFamily;
    private readonly string _codeFontFamily;
    private readonly string _mathFontFamily;
    private readonly string _emojiFontFamily;
    private readonly string? _mathFontFilePath;
    private readonly float _baseFontSize;
    private SKTypeface? _bodyTypeface;
    private SKTypeface? _mathTypeface;
    private SKTypeface? _emojiTypeface;
    private SKFont? _codeCjkFont; // 代码块中含中文等 CJK 时的回退字体，避免等宽字体无字形显示为方块
    private readonly Dictionary<float, SKFont> _emojiFontBySize = new();
    private readonly Dictionary<RunStyle, SKFont> _fontCache = new();
    private readonly IImageLoader? _imageLoader;
    private readonly IBlockPainter[] _blockPainters;
    private readonly bool _enableBlockPictureCache;
    private readonly object _blockPictureCacheGate = new();
    private Dictionary<int, (ulong Sig, SKPicture Pic)>? _blockPictureCache;

    public SkiaRenderer(EngineConfig? config = null, IImageLoader? imageLoader = null)
    {
        config ??= new EngineConfig();
        _bodyFontFamily = config.BodyFontFamily;
        _codeFontFamily = config.CodeFontFamily;
        _mathFontFamily = config.MathFontFamily;
        _emojiFontFamily = config.EmojiFontFamily ?? "Segoe UI Emoji,Noto Color Emoji";
        _mathFontFilePath = config.MathFontFilePath;
        _baseFontSize = config.BaseFontSize;

        static SKColor FromUint(uint c)
        {
            var a = (byte)(c >> 24);
            var r = (byte)(c >> 16);
            var g = (byte)(c >> 8);
            var b = (byte)c;
            return new SKColor(r, g, b, a);
        }

        _textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = FromUint(config.TextColor)
        };
        _codeBgPaint = new SKPaint { Color = FromUint(config.CodeBackground) };
        _selectionPaint = new SKPaint { Color = FromUint(config.SelectionColor) };
        _imagePlaceholderPaint = new SKPaint { Color = FromUint(config.ImagePlaceholderColor) };
        var bc = config.TableBorderColor;
        _tableBorderPaint = new SKPaint
        {
            Color = FromUint(bc),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        var hc = config.TableHeaderBackground;
        _tableHeaderBgPaint = new SKPaint { Color = FromUint(hc), Style = SKPaintStyle.Fill };
        _codeKeywordColor = config.CodeKeywordColor;
        _codeStringColor = config.CodeStringColor;
        _codeCommentColor = config.CodeCommentColor;
        _codeNumberColor = config.CodeNumberColor;
        _codeDefaultColor = config.CodeDefaultColor;
        _linkColor = config.LinkColor;
        _imageLoader = imageLoader ?? new DefaultImageLoader();
        _blockPainters = BlockPainterRegistry.CreateDefault();
        _enableBlockPictureCache = config.EnableBlockPictureCache;
    }

    /// <summary>布局或资源变化时丢弃块级录制缓存，避免陈旧位图（例如图片异步加载完成后调用）。</summary>
    public void InvalidateBlockPictureCache()
    {
        lock (_blockPictureCacheGate)
        {
            if (_blockPictureCache == null)
                return;
            foreach (var kv in _blockPictureCache)
                kv.Value.Pic.Dispose();
            _blockPictureCache.Clear();
        }
    }

    /// <summary>移除指定文档块索引区间 [startInclusive, endExclusive) 的块图缓存条目。</summary>
    public void InvalidateBlockPictureCacheRange(int startInclusive, int endExclusive)
    {
        lock (_blockPictureCacheGate)
        {
            if (_blockPictureCache == null || startInclusive >= endExclusive)
                return;
            for (int k = startInclusive; k < endExclusive; k++)
            {
                if (_blockPictureCache.TryGetValue(k, out var prev))
                {
                    prev.Pic.Dispose();
                    _blockPictureCache.Remove(k);
                }
            }
        }
    }

    /// <summary>块数量变化时移除无效键（如文档变短后避免孤儿条目占内存）。</summary>
    public void PruneBlockPictureCacheBeyondBlockCount(int blockCount)
    {
        lock (_blockPictureCacheGate)
        {
            if (_blockPictureCache == null || blockCount <= 0)
                return;
            var toRemove = new List<int>();
            foreach (var key in _blockPictureCache.Keys)
            {
                if (key < 0 || key >= blockCount)
                    toRemove.Add(key);
            }
            foreach (var k in toRemove)
            {
                _blockPictureCache[k].Pic.Dispose();
                _blockPictureCache.Remove(k);
            }
        }
    }

    /// <summary>块内文本与结构指纹，用于 SKPicture 签名及瓦片失效判断。</summary>
    public static int ComputeBlockLayoutContentHash(LayoutBlock block)
    {
        var hc = new HashCode();
        hc.Add(block.BlockIndex);
        hc.Add((int)block.Kind);
        hc.Add(BitConverter.SingleToInt32Bits(block.ContentWidth));
        if (block.TableInfo is { } ti)
        {
            hc.Add(ti.ColCount);
            hc.Add(ti.RowCount);
            foreach (var w in ti.ColumnWidths)
                hc.Add(BitConverter.SingleToInt32Bits(w));
            foreach (var h in ti.RowHeights)
                hc.Add(BitConverter.SingleToInt32Bits(h));
        }
        foreach (var line in block.Lines)
        {
            hc.Add(BitConverter.SingleToInt32Bits(line.Y));
            hc.Add(BitConverter.SingleToInt32Bits(line.Height));
            foreach (var run in line.Runs)
            {
                hc.Add(run.Text);
                hc.Add((int)run.Style);
                hc.Add(run.LinkUrl);
                hc.Add(run.TableAlign is { } ta ? (int)ta : -1);
                hc.Add(run.FootnoteRefId);
            }
        }
        return hc.ToHashCode();
    }

    private static ulong BlockPictureSignature(LayoutBlock block, float scale)
    {
        var b = block.Bounds;
        int content = ComputeBlockLayoutContentHash(block);
        return (ulong)(uint)HashCode.Combine(
            block.BlockIndex,
            block.Kind,
            block.Lines.Count,
            BitConverter.SingleToInt32Bits(b.Width),
            BitConverter.SingleToInt32Bits(b.Height),
            BitConverter.SingleToInt32Bits(scale),
            content);
    }

    /// <summary>文本是否含 \r 或 \n，用于避免无谓的 Replace 分配。</summary>
    private static bool ContainsCrLf(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\r' || s[i] == '\n') return true;
        return false;
    }

    private static string GetDrawableText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return ContainsCrLf(text) ? text.Replace("\r", " ").Replace("\n", " ") : text;
    }

    private static bool IsCodeBlockHighlightStyle(RunStyle style) =>
        style is RunStyle.CodeKeyword or RunStyle.CodeString or RunStyle.CodeComment or RunStyle.CodeNumber or RunStyle.CodeDefault;

    private SKColor GetCodeTokenColor(RunStyle style)
    {
        uint c = style switch
        {
            RunStyle.CodeKeyword => _codeKeywordColor,
            RunStyle.CodeString => _codeStringColor,
            RunStyle.CodeComment => _codeCommentColor,
            RunStyle.CodeNumber => _codeNumberColor,
            RunStyle.CodeDefault => _codeDefaultColor,
            _ => 0u
        };
        if (c == 0) return _textPaint.Color;
        var a = (byte)(c >> 24);
        var r = (byte)(c >> 16);
        var g = (byte)(c >> 8);
        var b = (byte)c;
        return new SKColor(r, g, b, a);
    }

    private SKFont GetFont(RunStyle style)
    {
        if (_fontCache.TryGetValue(style, out var f))
            return f;
        float size = _baseFontSize;
        if (
            style
            is RunStyle.Heading1
                or RunStyle.Heading2
                or RunStyle.Heading3
                or RunStyle.Heading4
                or RunStyle.Heading5
                or RunStyle.Heading6
        )
            size = style switch
            {
                RunStyle.Heading1 => 28,
                RunStyle.Heading2 => 24,
                RunStyle.Heading3 => 20,
                RunStyle.Heading4 => 18,
                RunStyle.Heading5 => 16,
                RunStyle.Heading6 => 14,
                _ => 20
            };
        SKFont font;
        if (style == RunStyle.Code || IsCodeBlockHighlightStyle(style))
        {
            if (_fontCache.TryGetValue(RunStyle.Code, out var codeFont))
                return codeFont;
            foreach (var name in _codeFontFamily.Split(',', StringSplitOptions.TrimEntries))
            {
                var tf = SKTypeface.FromFamilyName(name.Trim());
                if (tf != null)
                {
                    font = new SKFont(tf, _baseFontSize * 0.9f);
                    _fontCache[RunStyle.Code] = font;
                    return font;
                }
            }
            font = new SKFont(SKTypeface.FromFamilyName("Cascadia Code"), _baseFontSize * 0.9f);
            _fontCache[RunStyle.Code] = font;
        }
        else
        {
            var body = _bodyTypeface ??= ResolveBodyTypeface();
            var fs = style switch
            {
                RunStyle.BoldItalic or RunStyle.LinkBoldItalic => SKFontStyle.BoldItalic,
                RunStyle.Bold
                    or RunStyle.Heading1
                    or RunStyle.Heading2
                    or RunStyle.Heading3
                    or RunStyle.Heading4
                    or RunStyle.Heading5
                    or RunStyle.Heading6
                    or RunStyle.TableHeaderCell
                    or RunStyle.LinkBold => SKFontStyle.Bold,
                RunStyle.Italic or RunStyle.Math or RunStyle.LinkItalic => SKFontStyle.Italic,
                _ => SKFontStyle.Normal
            };
            font = new SKFont(SKTypeface.FromFamilyName(body.FamilyName, fs), size);
        }
        font.Subpixel = true;
        _fontCache[style] = font;
        return font;
    }

    private SKTypeface ResolveBodyTypeface()
    {
        foreach (var name in _bodyFontFamily.Split(',', StringSplitOptions.TrimEntries))
        {
            var tf = SKTypeface.FromFamilyName(name.Trim());
            if (tf != null)
                return tf;
        }
        return SKTypeface.FromFamilyName("Microsoft YaHei UI");
    }

    private SKTypeface ResolveEmojiTypeface()
    {
        if (_emojiTypeface != null)
            return _emojiTypeface;
        foreach (var name in _emojiFontFamily.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var tf = SKTypeface.FromFamilyName(name.Trim());
            if (tf != null)
            {
                _emojiTypeface = tf;
                return tf;
            }
        }
        try
        {
            _emojiTypeface = SKFontManager.Default.MatchCharacter(0x1F600);
        }
        catch
        {
            _emojiTypeface = null;
        }
        _emojiTypeface ??= ResolveBodyTypeface();
        return _emojiTypeface;
    }

    private SKFont GetEmojiFont(float size)
    {
        if (_emojiFontBySize.TryGetValue(size, out var cached))
            return cached;
        var tf = ResolveEmojiTypeface();
        cached = new SKFont(tf, size) { Subpixel = true };
        _emojiFontBySize[size] = cached;
        return cached;
    }

    /// <summary>
    /// 选择用于数学公式的一套字体：
    /// 1. 若 EngineConfig.MathFontFilePath 指定了字体文件且存在，则优先从文件加载；
    /// 2. 否则按 EngineConfig.MathFontFamily 中的家族名顺序尝试；
    /// 3. 最后回退到正文字体。
    /// </summary>
    private SKTypeface GetMathTypeface()
    {
        if (_mathTypeface != null)
            return _mathTypeface;

        // 1) 优先：程序集内嵌字体资源（发布后不依赖外部路径）
        try
        {
            var asm = typeof(SkiaRenderer).Assembly;
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
                // 文件路径无效或加载失败时忽略，继续尝试后续候选
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
                // 某个家族加载失败时继续尝试下一个
            }
        }

        // 4) 最后：回退到正文字体
        _mathTypeface = ResolveBodyTypeface();
        return _mathTypeface;
    }

    /// <summary>文本是否包含 CJK 字符（中文等），用于代码块字体回退，避免等宽字体无字形显示为方块。</summary>
    private static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff')
                return true; // CJK 统一汉字
            if (c >= '\u3000' && c <= '\u303f')
                return true; // CJK 符号和标点
            if (c >= '\uff00' && c <= '\uffef')
                return true; // 全角
        }
        return false;
    }

    private SKFont GetCodeCjkFont()
    {
        if (_codeCjkFont != null)
            return _codeCjkFont;
        var tf = SKFontManager.Default.MatchCharacter('中');
        if (tf == null)
            tf = ResolveBodyTypeface();
        _codeCjkFont = new SKFont(tf, _baseFontSize * 0.9f);
        return _codeCjkFont;
    }

    /// <summary>绘制块列表的指定区间，避免调用方 GetRange 拷贝。</summary>
    public void Render(
        ISkiaRenderContext ctx,
        IReadOnlyList<LayoutBlock> blocks,
        int startIndex,
        int count,
        SelectionRange? selection
    )
    {
        if (blocks == null)
            return;
        var canvas = ctx.Canvas;
        var scale = ctx.Scale;
        int n = blocks.Count;
        if (startIndex >= n)
            return;
        int end = Math.Min(startIndex + count, n);

        bool usePictureCache = _enableBlockPictureCache
            && (selection == null || selection.Value.IsEmpty)
            && !ctx.PreferDirectDraw;

        for (int idx = startIndex; idx < end; idx++)
        {
            var block = blocks[idx];
            if (block == null)
                continue;

            if (usePictureCache && block.Bounds.Width > 0.5f && block.Bounds.Height > 0.5f)
            {
                lock (_blockPictureCacheGate)
                {
                    _blockPictureCache ??= new Dictionary<int, (ulong Sig, SKPicture Pic)>();
                    ulong sig = BlockPictureSignature(block, scale);
                    SKPicture? oldPic = null;
                    if (_blockPictureCache.TryGetValue(block.BlockIndex, out var prev))
                    {
                        if (prev.Sig == sig)
                        {
                            canvas.DrawPicture(prev.Pic, block.Bounds.Left, block.Bounds.Top);
                            continue;
                        }
                        oldPic = prev.Pic;
                    }
                    oldPic?.Dispose();

                    using var recorder = new SKPictureRecorder();
                    var bounds = new SKRect(0, 0, block.Bounds.Width, block.Bounds.Height);
                    var recordCanvas = recorder.BeginRecording(bounds);
                    PaintBlockContent(recordCanvas, block, scale, selection);
                    var picture = recorder.EndRecording();
                    _blockPictureCache[block.BlockIndex] = (sig, picture);
                    canvas.DrawPicture(picture, block.Bounds.Left, block.Bounds.Top);
                    continue;
                }
            }

            canvas.Save();
            canvas.Translate(block.Bounds.Left, block.Bounds.Top);
            PaintBlockContent(canvas, block, scale, selection);
            canvas.Restore();
        }
    }

    /// <summary>在块局部坐标系（原点在块左上角）下绘制单块内容与装饰。</summary>
    private void PaintBlockContent(SKCanvas canvas, LayoutBlock block, float scale, SelectionRange? selection)
    {
        IBlockPainter? painter = null;
        foreach (var p in _blockPainters)
        {
            if (p.Matches(block.Kind))
            {
                painter = p;
                break;
            }
        }
        if (painter == null)
            return;

        var paintCtx = new BlockPaintContext(
            canvas,
            scale,
            block,
            selection,
            run => DrawRun(canvas, run, scale, block),
            line => DrawSelectionForLine(canvas, line, block, selection),
            () => DrawBlockBackground(canvas, block));
        painter.Paint(block, paintCtx);
    }

    /// <summary>按 BlockKind 绘制块级装饰（引用条、代码背景、表格线等），供 BlockPaintContext 回调。</summary>
    private void DrawBlockBackground(SKCanvas canvas, LayoutBlock block)
    {
        if (block.Kind == BlockKind.Blockquote)
            DrawBlockquoteStyle(canvas, block);
        if (block.Kind == BlockKind.CodeBlock || block.Kind == BlockKind.HtmlBlock)
            DrawCodeBlockStyle(canvas, block);
        if (block.Kind == BlockKind.DefinitionList)
            DrawDefinitionListStyle(canvas, block);
        if (block.Kind == BlockKind.Footnotes)
            DrawFootnotesStyle(canvas, block);
        if (block.Kind == BlockKind.HorizontalRule)
            DrawHorizontalRule(canvas, block);
        if (block.TableInfo is { } ti)
            DrawTableGrid(canvas, block, ti);
    }

    /// <summary>绘制一行的选区高亮，供 BlockPaintContext 回调。</summary>
    private void DrawSelectionForLine(SKCanvas canvas, LayoutLine line, LayoutBlock block, SelectionRange? selection)
    {
        if (selection is not { } s || s.IsEmpty || line.Runs.Count == 0)
            return;
        var firstRun = line.Runs[0];
        var (_, selStart, selEnd) = s.ForBlock(firstRun.BlockIndex);
        if (selStart >= selEnd)
            return;
        bool any = false;
        float lineLeft = 0, lineRight = 0;
        float lineTop = float.MaxValue, lineBottom = float.MinValue;
        foreach (var run in line.Runs)
        {
            var runStart = run.CharOffset;
            var runEnd = run.CharOffset + run.Text.Length;
            if (selStart >= runEnd || selEnd <= runStart)
                continue;
            var segStart = Math.Max(selStart, runStart);
            var segEnd = Math.Min(selEnd, runEnd);
            var (segLeft, segWidth) = GetSelectionRectInRun(run, segStart, segEnd);
            if (segWidth <= 0)
                continue;
            var segRight = segLeft + segWidth;
            if (!any)
            {
                any = true;
                lineLeft = segLeft;
                lineRight = segRight;
            }
            else
            {
                lineLeft = Math.Min(lineLeft, segLeft);
                lineRight = Math.Max(lineRight, segRight);
            }
            lineTop = Math.Min(lineTop, run.Bounds.Top);
            lineBottom = Math.Max(lineBottom, run.Bounds.Bottom);
        }
        if (any && lineRight > lineLeft)
            canvas.DrawRect(new SKRect(lineLeft, lineTop, lineRight, lineBottom), _selectionPaint);
    }

    private (float left, float width) GetSelectionRectInRun(LayoutRun run, int selStart, int selEnd)
    {
        var startInRun = System.Math.Max(0, selStart - run.CharOffset);
        var endInRun = System.Math.Min(run.Text.Length, selEnd - run.CharOffset);
        if (startInRun >= endInRun)
            return (0, 0);
        var font =
            (run.Style == RunStyle.Code || IsCodeBlockHighlightStyle(run.Style)) && ContainsCjk(run.Text)
                ? GetCodeCjkFont()
                : GetFont(run.Style);
        float left;
        float width;
        if (
            EmojiAwareTextMetrics.IsEmojiAwareRunStyle(run.Style)
            && EmojiAwareTextMetrics.ContainsLikelyEmoji(run.Text)
        )
        {
            var emojiFont = GetEmojiFont(font.Size);
            EmojiAwareTextMetrics.FillPrefixWidths(run.Text, font, emojiFont, out var widths);
            left = startInRun > 0 ? widths[startInRun] : 0;
            width = widths[endInRun] - widths[startInRun];
        }
        else
        {
            left = startInRun > 0 ? font.MeasureText(run.Text.AsSpan(0, startInRun)) : 0;
            width = font.MeasureText(run.Text.AsSpan(startInRun, endInRun - startInRun));
        }
        // 表格 run.Bounds 已是该片段在单元格内的实际矩形，无需再加内边距
        return (run.Bounds.Left + left, width);
    }

    private void DrawRun(SKCanvas canvas, LayoutRun run, float scale, LayoutBlock? block = null)
    {
        if (run.Style == RunStyle.Math)
        {
            if (block?.TableInfo != null)
            {
                canvas.Save();
                try
                {
                    canvas.ClipRect(run.Bounds, SKClipOperation.Intersect);
                    DrawMathRun(canvas, run);
                }
                finally { canvas.Restore(); }
            }
            else
                DrawMathRun(canvas, run);
            return;
        }
        if (run.Style == RunStyle.Image)
        {
            DrawImageRun(canvas, run);
            return;
        }

        if (run.Style == RunStyle.FootnoteRef)
        {
            DrawFootnoteRefRun(canvas, run);
            return;
        }

        // 待办复选框：渲染为方框+勾选状态，点击由 EngineRenderControl 的 todo-toggle 处理
        if (run.LinkUrl != null && run.LinkUrl.StartsWith("todo-toggle:", StringComparison.Ordinal))
        {
            DrawTodoCheckbox(canvas, run);
            return;
        }

        var font =
            (run.Style == RunStyle.Code || IsCodeBlockHighlightStyle(run.Style)) && ContainsCjk(run.Text)
                ? GetCodeCjkFont()
                : GetFont(run.Style);
        // 仅行内代码（`code`）绘制单行背景；代码块/HTML 块由 DrawCodeBlockStyle 统一绘制整块背景
        if (
            run.Style == RunStyle.Code
            && block?.Kind != BlockKind.CodeBlock
            && block?.Kind != BlockKind.HtmlBlock
        )
            canvas.DrawRect(run.Bounds, _codeBgPaint);

        // 表格单元格：按 run 边界裁剪，避免测量余量导致文本画出单元格右侧
        var isTableCell = run.Style is RunStyle.TableHeaderCell or RunStyle.TableCell;
        if (isTableCell)
            canvas.Save();
        try
        {
            if (isTableCell)
                canvas.ClipRect(run.Bounds, SKClipOperation.Intersect);

            var baseTextColor = _textPaint.Color;
            var isLinkStyle =
                run.Style is RunStyle.Link or RunStyle.LinkBold or RunStyle.LinkItalic or RunStyle.LinkBoldItalic;
            var textColor = isLinkStyle
                ? FromLinkColor()
                : IsCodeBlockHighlightStyle(run.Style)
                    ? GetCodeTokenColor(run.Style)
                    : _textPaint.Color;
            _textPaint.Color = textColor;
            var drawText = GetDrawableText(run.Text);
            float textX = run.Bounds.Left;
            float textY = run.Bounds.Bottom - 4;
            if (isTableCell)
                textY = run.Bounds.Bottom - 4;
            if (EmojiAwareTextMetrics.IsEmojiAwareRunStyle(run.Style) && EmojiAwareTextMetrics.ContainsLikelyEmoji(drawText))
            {
                var emojiFont = GetEmojiFont(font.Size);
                EmojiAwareTextMetrics.Draw(canvas, drawText, textX, textY, font, emojiFont, _textPaint);
            }
            else
                canvas.DrawText(drawText, textX, textY, font, _textPaint);

            if (
                isLinkStyle
                && (
                    run.LinkUrl == null
                    || !run.LinkUrl.StartsWith("footnote-back:", StringComparison.Ordinal)
                )
            )
            {
                using var linePaint = new SKPaint
                {
                    Color = textColor,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1
                };
                var midY = run.Bounds.Top + run.Bounds.Height * 0.85f;
                canvas.DrawLine(run.Bounds.Left, midY, run.Bounds.Right, midY, linePaint);
            }
            else if (run.Style == RunStyle.Strikethrough)
            {
                using var linePaint = new SKPaint
                {
                    Color = _textPaint.Color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1
                };
                var midY = run.Bounds.Top + run.Bounds.Height * 0.5f;
                canvas.DrawLine(run.Bounds.Left, midY, run.Bounds.Right, midY, linePaint);
            }
            _textPaint.Color = baseTextColor;
        }
        finally
        {
            if (isTableCell)
                canvas.Restore();
        }
    }

    private void DrawTodoCheckbox(SKCanvas canvas, LayoutRun run)
    {
        bool isChecked = run.Text.IndexOf('x') >= 0 || run.Text.IndexOf('X') >= 0;
        float h = run.Bounds.Height;
        float size = Math.Min(h * 0.65f, 16f);
        float left = run.Bounds.Left;
        float top = run.Bounds.Top + (h - size) * 0.5f;
        var boxRect = new SKRect(left, top, left + size, top + size);

        using (var strokePaint = new SKPaint
        {
            Color = _textPaint.Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        })
        {
            canvas.DrawRect(boxRect, strokePaint);
        }

        if (isChecked)
        {
            using var checkPaint = new SKPaint
            {
                Color = _textPaint.Color,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.8f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true
            };
            float pad = size * 0.2f;
            var path = new SKPath();
            path.MoveTo(boxRect.Left + pad, boxRect.MidY);
            path.LineTo(boxRect.Left + size * 0.4f, boxRect.Bottom - pad);
            path.LineTo(boxRect.Right - pad, boxRect.Top + pad);
            canvas.DrawPath(path, checkPaint);
        }
    }

    private void DrawFootnoteRefRun(SKCanvas canvas, LayoutRun run)
    {
        var font = GetFont(RunStyle.Normal);
        var size = _baseFontSize * 0.75f;
        var smallFont = new SKFont(font.Typeface, size);
        smallFont.Subpixel = true;
        var drawText = GetDrawableText(run.Text);
        float textX = run.Bounds.Left;
        float baselineY = run.Bounds.Bottom - 4;
        float raise = _baseFontSize * 0.35f;
        var baseColor = _textPaint.Color;
        _textPaint.Color = FromLinkColor();
        if (EmojiAwareTextMetrics.ContainsLikelyEmoji(drawText))
        {
            var emojiSmall = GetEmojiFont(smallFont.Size);
            EmojiAwareTextMetrics.Draw(canvas, drawText, textX, baselineY - raise, smallFont, emojiSmall, _textPaint);
        }
        else
            canvas.DrawText(drawText, textX, baselineY - raise, smallFont, _textPaint);
        _textPaint.Color = baseColor;
    }

    private SKColor FromLinkColor()
    {
        var c = _linkColor;
        return new SKColor((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
    }

    private void DrawImageRun(SKCanvas canvas, LayoutRun run)
    {
        var url = run.LinkUrl;
        var alt = run.Text;
        var rect = run.Bounds;
        if (string.IsNullOrEmpty(url) || _imageLoader == null)
        {
            DrawImagePlaceholder(canvas, rect, alt);
            return;
        }

        // 在 WithImage 锁内绘制：缓存中为缩小后的预览图，不再每帧 Copy，降低内存峰值。
        _imageLoader.WithImage(url, bmp =>
        {
            if (bmp != null && bmp.Width > 0 && bmp.Height > 0)
            {
                var srcRect = new SKRectI(0, 0, bmp.Width, bmp.Height);
                float destW = rect.Width;
                float destH = rect.Height;
                var scale = Math.Min(rect.Width / bmp.Width, rect.Height / bmp.Height);
                if (scale < 1f)
                {
                    destW = bmp.Width * scale;
                    destH = bmp.Height * scale;
                }
                else
                {
                    destW = bmp.Width;
                    destH = bmp.Height;
                }
                var destRect = new SKRect(rect.Left, rect.Top, rect.Left + destW, rect.Top + destH);
                canvas.DrawBitmap(bmp, srcRect, destRect);
            }
            else
                DrawImagePlaceholder(canvas, rect, alt);
        });
    }

    private void DrawImagePlaceholder(SKCanvas canvas, SKRect rect, string? alt)
    {
        canvas.DrawRect(rect, _imagePlaceholderPaint);
        var altDraw = GetDrawableText(string.IsNullOrEmpty(alt) ? "[图]" : alt);
        var altFont = GetFont(RunStyle.Normal);
        if (EmojiAwareTextMetrics.ContainsLikelyEmoji(altDraw))
            EmojiAwareTextMetrics.Draw(canvas, altDraw, rect.Left + 4, rect.Bottom - 6, altFont, GetEmojiFont(altFont.Size), _textPaint);
        else
            canvas.DrawText(
                altDraw,
                rect.Left + 4,
                rect.Bottom - 6,
                altFont,
                _textPaint
            );
    }

    private void DrawMathRun(SKCanvas canvas, LayoutRun run)
    {
        if (string.IsNullOrEmpty(run.Text))
            return;
        var bodyTf = _bodyTypeface ??= ResolveBodyTypeface();
        var mathTf = GetMathTypeface();
        var mathFontSize = Math.Max(16, _baseFontSize * 1.15f);
        MathSkiaRenderer.DrawFormula(canvas, run.Bounds, run.Text, bodyTf, mathTf, mathFontSize, _textPaint.Color);
    }

    /// <summary>
    /// 与布局/绘制一致：测量公式盒模型（宽、基线上方高度、基线下方深度）。
    /// 供导出等按真实排版尺寸裁图。
    /// </summary>
    public (float width, float height, float depth) MeasureMathFormula(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
            return (0, 0, 0);
        var bodyTf = _bodyTypeface ??= ResolveBodyTypeface();
        var mathTf = GetMathTypeface();
        float fontSize = Math.Max(16, _baseFontSize * 1.15f);
        return MathSkiaRenderer.MeasureFormula(latex, bodyTf, mathTf, fontSize);
    }

    /// <summary>
    /// 在给定矩形内绘制公式（与 <see cref="DrawMathRun"/> 使用相同字体与颜色）。
    /// </summary>
    public void DrawMathFormula(SKCanvas canvas, SKRect bounds, string latex)
    {
        if (string.IsNullOrWhiteSpace(latex) || canvas == null)
            return;
        var bodyTf = _bodyTypeface ??= ResolveBodyTypeface();
        var mathTf = GetMathTypeface();
        float fontSize = Math.Max(16, _baseFontSize * 1.15f);
        MathSkiaRenderer.DrawFormula(canvas, bounds, latex, bodyTf, mathTf, fontSize, _textPaint.Color);
    }

    /// <inheritdoc />
    public float MeasureText(string text, RunStyle style)
    {
        if (string.IsNullOrEmpty(text))
            return 0f;
        if (style == RunStyle.Math)
        {
            var bodyTf = _bodyTypeface ?? ResolveBodyTypeface();
            var mathTf = GetMathTypeface();
            var mathFontSize = Math.Max(16, _baseFontSize * 1.15f);
            var (w, _, _) = MathSkiaRenderer.MeasureFormula(text, bodyTf, mathTf, mathFontSize);
            return w + 8f;
        }
        var font = GetFont(style);
        if (EmojiAwareTextMetrics.IsEmojiAwareRunStyle(style) && EmojiAwareTextMetrics.ContainsLikelyEmoji(text))
        {
            var emojiFont = GetEmojiFont(font.Size);
            return EmojiAwareTextMetrics.Measure(text, font, emojiFont) + 1f;
        }
        // 与绘制相同字体；+1 像素安全余量，吸收舍入或个别测量偏差
        return font.MeasureText(text) + 1f;
    }

    /// <summary>与布局中表格起始边距一致，按列宽绘制网格和表头背景，不依赖 run 边界。</summary>
    private const float TableLeftInBlock = 12f;
    /// <summary>表格单元格水平内边距，与布局中 cellPaddingH 一致，文字与竖线留出间隙。</summary>
    private const float TableCellPaddingH = 8f;
    /// <summary>表格单元格垂直内边距，与布局中 cellPaddingV 一致。</summary>
    private const float TableCellPaddingV = 4f;

    private void DrawTableGrid(SKCanvas canvas, LayoutBlock block, TableLayoutInfo ti)
    {
        float tableWidth = 0;
        for (int i = 0; i < ti.ColCount; i++)
            tableWidth += ti.ColumnWidths[i];
        float tableLeft = TableLeftInBlock;
        float tableRight = tableLeft + tableWidth;

        // 通过第一行第一个 run 的 Bounds 推算出整个表格的顶部，
        // 再结合 RowHeights 按行高连续绘制，避免行与行之间出现间隙。
        if (block.Lines.Count == 0 || block.Lines[0].Runs.Count == 0)
            return;
        var firstRun = block.Lines[0].Runs[0];
        float tableTop = firstRun.Bounds.Top - TableCellPaddingV;
        float rowTop = tableTop;

        for (int r = 0; r < ti.RowCount; r++)
        {
            var line = r < block.Lines.Count ? block.Lines[r] : null;
            if (line == null || line.Runs.Count == 0)
                continue;

            float rowHeight = (ti.RowHeights != null && r < ti.RowHeights.Length)
                ? ti.RowHeights[r]
                : line.Height;
            float rowBottom = rowTop + rowHeight;

            if (r == 0)
                canvas.DrawRect(new SKRect(tableLeft, rowTop, tableRight, rowBottom), _tableHeaderBgPaint);

            canvas.DrawLine(tableLeft, rowTop, tableRight, rowTop, _tableBorderPaint);
            canvas.DrawLine(tableLeft, rowBottom, tableRight, rowBottom, _tableBorderPaint);
            float cx = tableLeft;
            for (int c = 0; c < ti.ColCount; c++)
            {
                canvas.DrawLine(cx, rowTop, cx, rowBottom, _tableBorderPaint);
                cx += ti.ColumnWidths[c];
            }
            canvas.DrawLine(cx, rowTop, cx, rowBottom, _tableBorderPaint);

            rowTop = rowBottom;
        }
    }

    /// <summary>引用块：背景 + 左侧竖条；颜色来源于 Markdown 样式（代码块背景 + 引用边框色）。</summary>
    private void DrawBlockquoteStyle(SKCanvas canvas, LayoutBlock block)
    {
        const float barWidth = 4f;
        var r = block.Bounds;
        // 背景使用与代码块相同的浅灰/深灰底色
        canvas.DrawRect(new SKRect(0, 0, r.Width, r.Height), _codeBgPaint);
        using var barPaint = new SKPaint
        {
            // 竖条颜色使用表格/引用边框色（与表格边框一致）
            Color = _tableBorderPaint.Color,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(0, 0, barWidth, r.Height), barPaint);
    }

    /// <summary>代码块：整块背景（与布局中的 CodeBlockPadding 配合，文字与边缘留出边距）。</summary>
    private void DrawCodeBlockStyle(SKCanvas canvas, LayoutBlock block)
    {
        var r = block.Bounds;
        canvas.DrawRect(new SKRect(0, 0, r.Width, r.Height), _codeBgPaint);
    }

    /// <summary>定义列表：背景 + 左侧竖条，复用代码块背景与边框色。</summary>
    private void DrawDefinitionListStyle(SKCanvas canvas, LayoutBlock block)
    {
        const float barWidth = 3f;
        var r = block.Bounds;
        canvas.DrawRect(new SKRect(0, 0, r.Width, r.Height), _codeBgPaint);
        using var barPaint = new SKPaint
        {
            Color = _tableBorderPaint.Color,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(0, 0, barWidth, r.Height), barPaint);
    }

    /// <summary>分割线（---）：在块内绘制一条横线。</summary>
    private void DrawHorizontalRule(SKCanvas canvas, LayoutBlock block)
    {
        var r = block.Bounds;
        const float padX = 24f;
        const float lineY = 6f; // 与 LayoutHorizontalRule 行高 12 居中
        float left = Math.Min(padX, r.Width * 0.2f);
        float right = Math.Max(r.Width - padX, r.Width * 0.8f);
        if (right > left)
            canvas.DrawLine(left, lineY, right, lineY, _tableBorderPaint);
    }

    /// <summary>脚注区：顶部分隔线。</summary>
    private void DrawFootnotesStyle(SKCanvas canvas, LayoutBlock block)
    {
        using var linePaint = new SKPaint
        {
            Color = new SKColor(0x55, 0x55, 0x55),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1
        };
        canvas.DrawLine(0, 0, block.Bounds.Width, 0, linePaint);
    }

    private static SKRect ScaleRect(SKRect r, float scale) =>
        new(r.Left * scale, r.Top * scale, r.Right * scale, r.Bottom * scale);
}

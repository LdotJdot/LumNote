using MarkdownEditor.Engine.Document;
using MarkdownEditor.Engine.Layout;
using MarkdownEditor.Latex;
using SkiaSharp;
using System.IO;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// Skia 渲染器 - 批量绘制，Font 缓存
/// </summary>
public sealed class SkiaRenderer
{
    private readonly SKPaint _textPaint;
    private readonly SKPaint _codeBgPaint;
    private readonly SKPaint _mathBgPaint;
    private readonly SKPaint _selectionPaint;
    private readonly SKPaint _imagePlaceholderPaint;
    private readonly SKPaint _tableBorderPaint;
    private readonly SKPaint _tableHeaderBgPaint;
    private readonly string _bodyFontFamily;
    private readonly string _codeFontFamily;
    private readonly string _mathFontFamily;
    private readonly string? _mathFontFilePath;
    private readonly float _baseFontSize;
    private SKTypeface? _bodyTypeface;
    private SKTypeface? _mathTypeface;
    private SKFont? _codeCjkFont; // 代码块中含中文等 CJK 时的回退字体，避免等宽字体无字形显示为方块
    private readonly Dictionary<RunStyle, SKFont> _fontCache = new();
    private readonly IImageLoader? _imageLoader;

    public SkiaRenderer(EngineConfig? config = null, IImageLoader? imageLoader = null)
    {
        config ??= new EngineConfig();
        _bodyFontFamily = config.BodyFontFamily;
        _codeFontFamily = config.CodeFontFamily;
        _mathFontFamily = config.MathFontFamily;
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
            SubpixelText = true,
            Color = FromUint(config.TextColor)
        };
        _codeBgPaint = new SKPaint { Color = FromUint(config.CodeBackground) };
        _mathBgPaint = new SKPaint { Color = FromUint(config.MathBackground) };
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
        _imageLoader = imageLoader ?? new DefaultImageLoader();
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
        if (style == RunStyle.Code)
        {
            foreach (var name in _codeFontFamily.Split(',', StringSplitOptions.TrimEntries))
            {
                var tf = SKTypeface.FromFamilyName(name.Trim());
                if (tf != null)
                {
                    font = new SKFont(tf, _baseFontSize * 0.9f);
                    _fontCache[style] = font;
                    return font;
                }
            }
            font = new SKFont(SKTypeface.FromFamilyName("Cascadia Code"), _baseFontSize * 0.9f);
        }
        else
        {
            var body = _bodyTypeface ??= ResolveBodyTypeface();
            var fs = style
                is RunStyle.Bold
                    or RunStyle.Heading1
                    or RunStyle.Heading2
                    or RunStyle.Heading3
                    or RunStyle.Heading4
                    or RunStyle.Heading5
                    or RunStyle.Heading6
                    or RunStyle.TableHeaderCell
                ? SKFontStyle.Bold
                : style is RunStyle.Italic or RunStyle.Math
                    ? SKFontStyle.Italic
                    : SKFontStyle.Normal;
            font = new SKFont(SKTypeface.FromFamilyName(body.FamilyName, fs), size);
        }
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
                // 文件路径无效或加载失败时忽略，继续尝试后续候选
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
                // 某个家族加载失败时继续尝试下一个
            }
        }

        // 3) 最后：回退到正文字体
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

    public void Render(
        ISkiaRenderContext ctx,
        IReadOnlyList<LayoutBlock> blocks,
        SelectionRange? selection
    )
    {
        var canvas = ctx.Canvas;
        var scale = ctx.Scale;

        foreach (var block in blocks)
        {
            canvas.Save();
            canvas.Translate(block.Bounds.Left, block.Bounds.Top);

            if (block.Kind == BlockKind.Blockquote)
            {
                DrawBlockquoteStyle(canvas, block);
            }
            if (block.Kind == BlockKind.CodeBlock || block.Kind == BlockKind.HtmlBlock)
            {
                DrawCodeBlockStyle(canvas, block);
            }
            if (block.Kind == BlockKind.DefinitionList)
            {
                DrawDefinitionListStyle(canvas, block);
            }
            if (block.Kind == BlockKind.Footnotes)
            {
                DrawFootnotesStyle(canvas, block);
            }

            if (block.TableInfo is { } ti)
            {
                DrawTableGrid(canvas, block, ti);
            }

            foreach (var line in block.Lines)
            {
                // 先绘制该行所有文字/元素
                foreach (var run in line.Runs)
                    DrawRun(canvas, run, scale, block);

                // 再按“整行大矩形+首尾精确位置”绘制选区，高亮行为与记事本/Word 一致
                if (selection is { } s && !s.IsEmpty && line.Runs.Count > 0)
                {
                    var firstRun = line.Runs[0];
                    var (_, selStart, selEnd) = s.ForBlock(firstRun.BlockIndex);
                    if (selStart < selEnd)
                    {
                        bool any = false;
                        float lineLeft = 0,
                            lineRight = 0;
                        float lineTop = float.MaxValue,
                            lineBottom = float.MinValue;

                        foreach (var run in line.Runs)
                        {
                            var runStart = run.CharOffset;
                            var runEnd = run.CharOffset + run.Text.Length;
                            if (selStart >= runEnd || selEnd <= runStart)
                                continue;

                            var segStart = System.Math.Max(selStart, runStart);
                            var segEnd = System.Math.Min(selEnd, runEnd);
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
                                lineLeft = System.Math.Min(lineLeft, segLeft);
                                lineRight = System.Math.Max(lineRight, segRight);
                            }
                            lineTop = System.Math.Min(lineTop, run.Bounds.Top);
                            lineBottom = System.Math.Max(lineBottom, run.Bounds.Bottom);
                        }

                        if (any && lineRight > lineLeft)
                        {
                            canvas.DrawRect(
                                new SKRect(lineLeft, lineTop, lineRight, lineBottom),
                                _selectionPaint
                            );
                        }
                    }
                }
            }

            canvas.Restore();
        }
    }

    private (float left, float width) GetSelectionRectInRun(LayoutRun run, int selStart, int selEnd)
    {
        var startInRun = System.Math.Max(0, selStart - run.CharOffset);
        var endInRun = System.Math.Min(run.Text.Length, selEnd - run.CharOffset);
        if (startInRun >= endInRun)
            return (0, 0);
        var font =
            run.Style == RunStyle.Code && ContainsCjk(run.Text)
                ? GetCodeCjkFont()
                : GetFont(run.Style);
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        var left = startInRun > 0 ? paint.MeasureText(run.Text.AsSpan(0, startInRun)) : 0;
        var width = paint.MeasureText(run.Text.AsSpan(startInRun, endInRun - startInRun));
        // 表格单元格内文字有 8px 左内边距，高亮也需要对齐
        if (run.Style is RunStyle.TableHeaderCell or RunStyle.TableCell)
            left += 8;
        return (run.Bounds.Left + left, width);
    }

    private void DrawRun(SKCanvas canvas, LayoutRun run, float scale, LayoutBlock? block = null)
    {
        if (run.Style == RunStyle.Math)
        {
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

        var font =
            run.Style == RunStyle.Code && ContainsCjk(run.Text)
                ? GetCodeCjkFont()
                : GetFont(run.Style);
        // 仅行内代码（`code`）绘制单行背景；代码块/HTML 块由 DrawCodeBlockStyle 统一绘制整块背景
        if (
            run.Style == RunStyle.Code
            && block?.Kind != BlockKind.CodeBlock
            && block?.Kind != BlockKind.HtmlBlock
        )
            canvas.DrawRect(run.Bounds, _codeBgPaint);

        var baseTextColor = _textPaint.Color;
        var textColor = run.Style == RunStyle.Link ? FromLinkColor() : _textPaint.Color;
        _textPaint.Color = textColor;
        _textPaint.TextSize = font.Size;
        var drawText = run.Text.Replace("\r", " ").Replace("\n", " "); // 避免控制符渲染为小方块
        float textX = run.Bounds.Left;
        float textY = run.Bounds.Bottom - 4;
        if (run.Style is RunStyle.TableHeaderCell or RunStyle.TableCell)
        {
            textY = run.Bounds.Bottom - 8;
            const float cellPaddingH = 8f;
            var align = run.TableAlign ?? TableCellAlign.Left;
            var textW = _textPaint.MeasureText(drawText);
            var cellW = run.Bounds.Width;
            textX = align switch
            {
                TableCellAlign.Center => run.Bounds.Left + (cellW - textW) / 2f,
                TableCellAlign.Right => run.Bounds.Right - cellPaddingH - textW,
                _ => run.Bounds.Left + cellPaddingH
            };
        }
        canvas.DrawText(drawText, textX, textY, font, _textPaint);

        if (
            run.Style == RunStyle.Link
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

    private void DrawFootnoteRefRun(SKCanvas canvas, LayoutRun run)
    {
        var font = GetFont(RunStyle.Normal);
        var size = _baseFontSize * 0.75f;
        var smallFont = new SKFont(font.Typeface, size);
        var drawText = run.Text.Replace("\r", " ").Replace("\n", " ");
        float textX = run.Bounds.Left;
        float baselineY = run.Bounds.Bottom - 4;
        float raise = _baseFontSize * 0.35f;
        var baseColor = _textPaint.Color;
        _textPaint.Color = FromLinkColor();
        _textPaint.TextSize = size;
        canvas.DrawText(drawText, textX, baselineY - raise, smallFont, _textPaint);
        _textPaint.Color = baseColor;
    }

    private SKColor FromLinkColor()
    {
        // 链接颜色目前直接使用 VSCode 风格蓝色，后续如需根据配置细分可在此扩展
        return new SKColor(0x37, 0x94, 0xff);
    }

    private void DrawImageRun(SKCanvas canvas, LayoutRun run)
    {
        var url = run.LinkUrl;
        var alt = run.Text;
        var rect = run.Bounds;
        var bmp = url != null && _imageLoader != null ? _imageLoader.TryGetImage(url) : null;

        if (bmp != null && !bmp.IsEmpty)
        {
            var srcRect = new SKRectI(0, 0, bmp.Width, bmp.Height);
            // 按本身尺寸/宽高比绘制，不拉伸；若分配区域小于图片则等比缩小放入
            float destW = rect.Width;
            float destH = rect.Height;
            if (bmp.Width > 0 && bmp.Height > 0)
            {
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
            }
            var destRect = new SKRect(rect.Left, rect.Top, rect.Left + destW, rect.Top + destH);
            canvas.DrawBitmap(bmp, srcRect, destRect);
        }
        else
        {
            canvas.DrawRect(rect, _imagePlaceholderPaint);
            _textPaint.TextSize = _baseFontSize * 0.9f;
            var altDraw = (string.IsNullOrEmpty(alt) ? "[图]" : alt)
                .Replace("\r", " ")
                .Replace("\n", " ");
            canvas.DrawText(
                altDraw,
                rect.Left + 4,
                rect.Bottom - 6,
                GetFont(RunStyle.Normal),
                _textPaint
            );
        }
    }

    private void DrawMathRun(SKCanvas canvas, LayoutRun run)
    {
        if (string.IsNullOrEmpty(run.Text))
            return;
        canvas.DrawRect(run.Bounds, _mathBgPaint);
        var bodyTf = _bodyTypeface ??= ResolveBodyTypeface();
        var mathTf = GetMathTypeface();
        var mathFontSize = Math.Max(16, _baseFontSize * 1.15f);
        MathSkiaRenderer.DrawFormula(canvas, run.Bounds, run.Text, bodyTf, mathTf, mathFontSize);
    }

    /// <summary>与布局中表格起始边距一致，按列宽绘制网格和表头背景，不依赖 run 边界。</summary>
    private const float TableLeftInBlock = 12f;
    /// <summary>表格单元格垂直内边距，应与布局中的 cellPaddingV 保持一致。</summary>
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

    /// <summary>引用块：背景 + 左侧竖条；代码块/公式块不加竖线。</summary>
    private void DrawBlockquoteStyle(SKCanvas canvas, LayoutBlock block)
    {
        const float barWidth = 4f;
        var r = block.Bounds;
        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0x2d, 0x2d, 0x30),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(0, 0, r.Width, r.Height), bgPaint);
        using var barPaint = new SKPaint
        {
            Color = new SKColor(0x60, 0x8b, 0x4e),
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

    /// <summary>定义列表：背景 + 左侧竖条。</summary>
    private void DrawDefinitionListStyle(SKCanvas canvas, LayoutBlock block)
    {
        const float barWidth = 3f;
        var r = block.Bounds;
        using var bgPaint = new SKPaint
        {
            Color = new SKColor(0x28, 0x28, 0x2c),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(0, 0, r.Width, r.Height), bgPaint);
        using var barPaint = new SKPaint
        {
            Color = new SKColor(0x4a, 0x7a, 0x9a),
            Style = SKPaintStyle.Fill
        };
        canvas.DrawRect(new SKRect(0, 0, barWidth, r.Height), barPaint);
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

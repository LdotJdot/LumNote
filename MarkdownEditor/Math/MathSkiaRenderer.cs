using System;
using System.Collections.Generic;
using System.IO;
using MarkdownEditor.Latex;
using SkiaSharp;

namespace MarkdownEditor.Latex;

/// <summary>
/// 使用 SkiaSharp 将 LaTeX 数学公式（通过 MathParser 解析后的 AST）绘制到画布上。
/// 这是一个简化版的 TeX 布局引擎：支持顺序、上下标、分数、根号以及 eqnarray 多行环境。
/// </summary>
public static partial class MathSkiaRenderer
{
    private static readonly object _parseCacheLock = new();
    private static readonly Dictionary<string, MathNode> _parseCache = new(StringComparer.Ordinal);
    private static readonly List<string> _parseCacheOrder = new();
    /// <summary>公式解析缓存条数上限，超出时 FIFO 淘汰，控制长期内存。</summary>
    private const int MaxParseCacheEntries = 256;

    /// <summary>
    /// 将 LaTeX 源码做轻量归一化：
    /// - 始终去掉首尾空白、统一换行符；
    /// - 若仅包含一行非空内容（如 "$\n+123\n$"），则将内部换行压缩为空格，按单行公式处理；
    /// - 若包含多行非空内容（eqnarray、cases 等），则保留内部换行，不再压缩为单行。
    /// </summary>
    private static string NormalizeFormulaSource(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
            return string.Empty;

        var s = latex.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (!s.Contains('\n'))
            return s;

        var lines = s.Split('\n');
        int nonEmpty = 0;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                nonEmpty++;
        }

        // 仅一行非空内容时，按单行公式处理，压缩所有空白为单个空格。
        if (nonEmpty <= 1)
        {
            var sbSingle = new System.Text.StringBuilder(s.Length);
            bool lastIsSpaceSingle = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastIsSpaceSingle)
                    {
                        sbSingle.Append(' ');
                        lastIsSpaceSingle = true;
                    }
                }
                else
                {
                    sbSingle.Append(ch);
                    lastIsSpaceSingle = false;
                }
            }
            return sbSingle.ToString().Trim();
        }

        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(lines[i].TrimEnd());
        }
        return sb.ToString().Trim();
    }

    /// <summary>取字符串第一个标量码位（含 UTF-16 代理对）。</summary>
    private static int GetFirstCodePoint(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (text.Length >= 2 && char.IsSurrogatePair(text[0], text[1]))
            return char.ConvertToUtf32(text, 0);
        return text[0];
    }

    /// <summary>Unicode Mathematical Alphanumeric Symbols：字形本身已是斜体/粗体等样式，勿再对字体请求 Italic。</summary>
    private static bool IsMathematicalAlphanumericCodePoint(int cp) => cp is >= 0x1D400 and <= 0x1D7FF;

    /// <summary>
    /// 将单字符 ASCII 拉丁字母映射为数学斜体码位（与 TeX 默认变量样式一致）。
    /// OpenType Math 字体通常在 Regular 面提供这些字形，而传统 SKFontStyle.Italic 子字体往往不存在。
    /// </summary>
    private static bool TryMapLatinLetterToMathematicalItalic(string text, out string mapped)
    {
        mapped = text;
        if (string.IsNullOrEmpty(text) || text.Length != 1)
            return false;
        var c = text[0];
        if (c > 0x007F)
            return false;
        if (c >= 'A' && c <= 'Z')
        {
            mapped = char.ConvertFromUtf32(0x1D434 + (c - 'A'));
            return true;
        }
        if (c >= 'a' && c <= 'z')
        {
            mapped = char.ConvertFromUtf32(0x1D44E + (c - 'a'));
            return true;
        }
        return false;
    }

    private static MathNode GetOrParseRoot(string latex)
    {
        lock (_parseCacheLock)
        {
            if (_parseCache.TryGetValue(latex, out var cached))
                return cached;
            while (_parseCache.Count >= MaxParseCacheEntries && _parseCacheOrder.Count > 0)
            {
                var oldKey = _parseCacheOrder[0];
                _parseCacheOrder.RemoveAt(0);
                _parseCache.Remove(oldKey);
            }
            var formula = MathParser.Parse(latex);
            var root = formula.Root;
            _parseCache[latex] = root;
            _parseCacheOrder.Add(latex);
            return root;
        }
    }

    /// <summary>
    /// 在给定矩形区域内绘制 LaTeX 数学公式。
    /// bodyTypeface：用于 \text{} 等普通文本和 CJK 回退；
    /// mathTypeface：用于数学符号/运算符等（如 Latin Modern Math）。
    /// </summary>
    /// <param name="textColor">公式文字与线条颜色，由引擎根据 Markdown 样式（深色/浅色）传入，避免浅色主题下公式不可见。</param>
    public static void DrawFormula(
        SKCanvas canvas,
        SKRect bounds,
        string latex,
        SKTypeface bodyTypeface,
        SKTypeface mathTypeface,
        float baseFontSize,
        SKColor textColor
    )
    {
        if (canvas == null || string.IsNullOrWhiteSpace(latex))
            return;

        var normalized = NormalizeFormulaSource(latex);
        if (string.IsNullOrEmpty(normalized))
            return;

        var root = GetOrParseRoot(normalized);

        var ctx = new RenderContext(bodyTypeface, mathTypeface, baseFontSize, textColor);
        // 块级数学统一按 Display 样式处理；后续可根据内联/块级具体区分
        var box = BuildBox(root, ctx, MathStyle.Display, 1.0f);

        // 计算基线位置，使整体在 bounds 内垂直居中
        var totalHeight = box.Height + box.Depth;
        if (totalHeight <= 0)
            totalHeight = baseFontSize;

        var baseline = bounds.Top + box.Height + Math.Max(0, (bounds.Height - totalHeight) / 2);
        // 水平居中：在左右各预留 4 像素内边距的基础上，将整个公式盒子居中放置。
        float horizontalPadding = 4f;
        float innerWidth = Math.Max(0, bounds.Width - horizontalPadding * 2);
        float startX;
        if (box.Width >= innerWidth)
        {
            // 公式比可用宽度更宽时，退化为左对齐但仍保留左侧 padding。
            startX = bounds.Left + horizontalPadding;
        }
        else
        {
            startX =
                bounds.Left
                + horizontalPadding
                + (innerWidth - box.Width) / 2f;
        }

        box.Draw(canvas, startX, baseline);
    }

    /// <summary>
    /// 测量给定 LaTeX 数学公式的盒模型尺寸（宽度、基线上方高度、基线下方深度）。
    /// 可用于布局阶段精确设置数学块高度，而无需真正绘制。
    /// bodyTypeface：用于普通文本/CJK；mathTypeface：用于数学符号。
    /// </summary>
    public static (float width, float height, float depth) MeasureFormula(
        string latex,
        SKTypeface bodyTypeface,
        SKTypeface mathTypeface,
        float baseFontSize
    )
    {
        if (string.IsNullOrWhiteSpace(latex))
            return (0, 0, 0);

        var normalized = NormalizeFormulaSource(latex);
        if (string.IsNullOrEmpty(normalized))
            return (0, 0, 0);

        var root = GetOrParseRoot(normalized);
        // 测量不绘制，使用默认深色文字色即可
        var ctx = new RenderContext(bodyTypeface, mathTypeface, baseFontSize, new SKColor(0xd4, 0xd4, 0xd4));
        var box = BuildBox(root, ctx, MathStyle.Display, 1.0f);

        return (box.Width, box.Height, box.Depth);
    }

    #region Box 模型

    private sealed class RenderContext
    {
        public SKTypeface BodyTypeface { get; }
        public SKTypeface MathTypeface { get; }
        public float BaseFontSize { get; }
        /// <summary>公式文字与线条颜色，随 Markdown 样式（深色/浅色）变化。</summary>
        public SKColor TextColor { get; }

        public RenderContext(SKTypeface bodyTypeface, SKTypeface mathTypeface, float baseFontSize, SKColor textColor)
        {
            BodyTypeface = bodyTypeface;
            MathTypeface = mathTypeface ?? bodyTypeface;
            BaseFontSize = baseFontSize;
            TextColor = textColor;
        }

        /// <summary>
        /// 创建数学符号用字体：基于数学字体家族，支持粗体/斜体。
        /// </summary>
        public SKFont CreateMathFont(float scale = 1.0f, bool italic = false, bool bold = false)
        {
            // 优先尝试同族的 Bold/Italic 面；多数 OpenType Math 仅为单 Regular 面，此时依赖 Unicode 数学字形或下方 SkewX。
            var style = bold && italic
                ? SKFontStyle.BoldItalic
                : bold
                    ? SKFontStyle.Bold
                    : italic
                        ? SKFontStyle.Italic
                        : SKFontStyle.Normal;
            SKTypeface tf = MathTypeface;
            if (style != SKFontStyle.Normal)
            {
                var family = MathTypeface.FamilyName;
                if (!string.IsNullOrWhiteSpace(family))
                {
                    var resolved = SKTypeface.FromFamilyName(family, style);
                    if (resolved != null)
                        tf = resolved;
                }
            }

            var font = new SKFont(tf, BaseFontSize * scale);
            // 仍无斜体面时（常见于 .otf 单面数学字体），对变量用轻微倾斜，避免与正体数字/运算符混淆。
            if (italic && !bold && tf.FontStyle.Slant == SKFontStyleSlant.Upright)
                font.SkewX = -0.22f;
            return font;
        }

        /// <summary>
        /// 创建正文文本用字体（用于 \text{} 中的中文说明等），始终使用正文字体正体。
        /// </summary>
        public SKFont CreateTextFont(float scale = 1.0f)
        {
            var tf =
                SKTypeface.FromFamilyName(BodyTypeface.FamilyName, SKFontStyle.Normal)
                ?? BodyTypeface;
            return new SKFont(tf, BaseFontSize * scale);
        }

        /// <summary>
        /// 兼容旧代码的通用 CreateFont 接口：等价于使用数学字体家族。
        /// </summary>
        public SKFont CreateFont(float scale = 1.0f, bool italic = false, bool bold = false) =>
            CreateMathFont(scale, italic, bold);

        /// <summary>
        /// 为给定符号选择合适字体：公式内尽量统一用衬线数学字体，变量用斜体。
        /// - 拉丁字母/数字/希腊字母：统一用数学字体（保证同一公式内同一变量字体一致）
        /// - CJK 等：MatchCharacter 回退
        /// </summary>
        public SKFont CreateSymbolFont(
            string text,
            float scale = 1.0f,
            bool italicPreferred = false
        )
        {
            if (string.IsNullOrEmpty(text))
                return CreateMathFont(scale, italic: italicPreferred);

            int cp = GetFirstCodePoint(text);
            if (IsMathematicalAlphanumericCodePoint(cp))
                return CreateMathFont(scale, italic: false, bold: false);

            var c = (char)cp;
            if (cp <= 0x007F && (char.IsLetterOrDigit(c) || c == ' '))
                return CreateMathFont(scale, italic: italicPreferred);

            if (IsGreekOrMathCodePoint(cp))
            {
                if (CanRenderWithMathTypeface(text))
                    return CreateMathFont(scale, italic: italicPreferred);
                var greekFontManager = SKFontManager.Default;
                var fallbackGreek = cp <= char.MaxValue ? greekFontManager.MatchCharacter((char)cp) : null;
                if (fallbackGreek != null)
                    return new SKFont(fallbackGreek, BaseFontSize * scale);
                return CreateMathFont(scale, italic: italicPreferred);
            }

            var fm = SKFontManager.Default;
            var fallbackTf = cp <= char.MaxValue ? fm.MatchCharacter((char)cp) : null;
            if (fallbackTf != null)
                return new SKFont(fallbackTf, BaseFontSize * scale);

            return CreateMathFont(scale, italic: italicPreferred);
        }

        /// <summary>当前数学字体是否可渲染给定文本，避免映射到无字形码位后显示为方框。</summary>
        public bool CanRenderWithMathTypeface(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            using var paint = new SKPaint
            {
                Typeface = MathTypeface,
                TextSize = BaseFontSize
            };
            try
            {
                return paint.ContainsGlyphs(text);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsGreekOrMathCodePoint(int cp) =>
            (cp >= 0x0370 && cp <= 0x03FF) || (cp >= 0x1F00 && cp <= 0x1FFF);
    }

    private abstract class Box
    {
        /// <summary>基线以上高度。</summary>
        public float Height { get; protected set; }

        /// <summary>基线以下深度。</summary>
        public float Depth { get; protected set; }

        /// <summary>总宽度。</summary>
        public float Width { get; protected set; }

        /// <summary>
        /// 在给定基线位置 (baselineY) 上从 xStart 开始绘制。
        /// baselineY 为文本基线的 Y 坐标。
        /// </summary>
        public abstract void Draw(SKCanvas canvas, float xStart, float baselineY);
    }

    private sealed class SymbolBox : Box
    {
        private readonly string _text;
        private readonly SKFont _font;
        private readonly SKPaint _paint;

        public SymbolBox(string text, SKFont font, SKPaint paint)
        {
            _text = text;
            _font = font;
            _paint = paint;

            using var measurePaint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
            Width = measurePaint.MeasureText(_text);

            // SkiaSharp 的 SKFont 暴露 Metrics 属性，而不是 GetMetrics 方法
            var m = font.Metrics;
            if (!float.IsNaN(m.Ascent) && !float.IsNaN(m.Descent))
            {
                Height = Math.Abs(m.Ascent);
                Depth = Math.Abs(m.Descent);
            }
            else
            {
                Height = font.Size * 0.8f;
                Depth = font.Size * 0.2f;
            }
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            canvas.DrawText(_text, xStart, baselineY, _font, _paint);
        }
    }

    private sealed class HorizontalBox : Box
    {
        private readonly List<Box> _children;
        private readonly float _spacing;

        public HorizontalBox(List<Box> children, float spacing)
        {
            _children = children;
            _spacing = spacing;

            float w = 0,
                h = 0,
                d = 0;
            for (int i = 0; i < _children.Count; i++)
            {
                var c = _children[i];
                w += c.Width;
                if (i + 1 < _children.Count)
                    w += _spacing;
                h = Math.Max(h, c.Height);
                d = Math.Max(d, c.Depth);
            }
            Width = w;
            Height = h;
            Depth = d;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            var x = xStart;
            foreach (var c in _children)
            {
                c.Draw(canvas, x, baselineY);
                x += c.Width + _spacing;
            }
        }
    }

    private sealed class SupSubBox : Box
    {
        private readonly Box _base;
        private readonly Box? _sup;
        private readonly Box? _sub;
        private readonly float _gap;
        private readonly float _em;
        private readonly float _supRaise;
        private readonly float _subLower;

        public SupSubBox(Box @base, Box? sup, Box? sub, float gap, float em)
        {
            _base = @base;
            _sup = sup;
            _sub = sub;
            _gap = gap;
            _em = em;

            // 宽度：取 base 与上下标右端的最大值
            float w = _base.Width;
            if (_sup != null)
                w = Math.Max(w, _base.Width + _gap + _sup.Width);
            if (_sub != null)
                w = Math.Max(w, _base.Width + _gap + _sub.Width);
            Width = w;

            // 上标/下标相对基线的位移不再是固定常数，
            // 而是基于“基底盒子自身的高度/深度”动态调整：
            // - 若基底本身已经有上标（如 x^2 的整体），它会更高，
            //   新的 ^2 应该再抬高一些，形成一串逐级向上的“阶梯”。
            // - 若基底比较矮（单个字母/数字），则使用 em 的缺省偏移。
            float minSupRaise = _em * 0.55f;
            float idealSupRaise = _base.Height * 0.6f;
            _supRaise = _sup != null ? Math.Max(minSupRaise, idealSupRaise) : 0f;

            float minSubLower = _em * 0.35f;
            float idealSubLower = _base.Depth * 0.6f;
            _subLower = _sub != null ? Math.Max(minSubLower, idealSubLower) : 0f;

            float h = _base.Height;
            if (_sup != null)
                h = Math.Max(h, _supRaise + _sup.Height);

            float d = _base.Depth;
            if (_sub != null)
                d = Math.Max(d, _subLower + _sub.Depth);

            Height = h;
            Depth = d;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            // 画 base
            _base.Draw(canvas, xStart, baselineY);

            var attachX = xStart + _base.Width + _gap;

            if (_sup != null)
            {
                var supBaseline = baselineY - _supRaise;
                _sup.Draw(canvas, attachX, supBaseline);
            }

            if (_sub != null)
            {
                var subBaseline = baselineY + _subLower;
                _sub.Draw(canvas, attachX, subBaseline);
            }
        }
    }

    /// <summary>Display 模式下大算子（∑ ∫ ∏ ∮）：上下标在主体正上/正下方、居中。</summary>
    private sealed class BigOpBox : Box
    {
        private readonly Box _op;
        private readonly Box? _sup;
        private readonly Box? _sub;
        private readonly float _gap;

        public BigOpBox(Box op, Box? sup, Box? sub, float gap)
        {
            _op = op;
            _sup = sup;
            _sub = sub;
            _gap = gap;

            float w = op.Width;
            if (_sup != null)
                w = Math.Max(w, _sup.Width);
            if (_sub != null)
                w = Math.Max(w, _sub.Width);
            Width = w + _gap * 2;

            float h = _op.Height;
            if (_sup != null)
                h += _gap + _sup.Height + _sup.Depth;
            float d = _op.Depth;
            if (_sub != null)
                d += _gap + _sub.Height + _sub.Depth;
            Height = h;
            Depth = d;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            float centerX = xStart + Width / 2;
            float opX = centerX - _op.Width / 2;
            _op.Draw(canvas, opX, baselineY);

            float y = baselineY - _op.Height - _gap;
            if (_sup != null)
            {
                float supBaseline = y - _sup.Depth;
                float supX = centerX - _sup.Width / 2;
                _sup.Draw(canvas, supX, supBaseline);
            }

            y = baselineY + _op.Depth + _gap;
            if (_sub != null)
            {
                float subBaseline = y + _sub.Height;
                float subX = centerX - _sub.Width / 2;
                _sub.Draw(canvas, subX, subBaseline);
            }
        }
    }

    private sealed class FractionBox : Box
    {
        private readonly Box _num;
        private readonly Box _den;
        private readonly float _ruleThickness;
        private readonly float _gap;
        private readonly SKPaint _linePaint;
        private readonly float _numShiftUp;
        private readonly float _denShiftDown;

        public FractionBox(
            Box numerator,
            Box denominator,
            float ruleThickness,
            float gap,
            SKPaint linePaint
        )
        {
            _num = numerator;
            _den = denominator;
            _ruleThickness = ruleThickness;
            _gap = gap;
            _linePaint = linePaint;

            Width = Math.Max(_num.Width, _den.Width) + 4;

            // 以分子自身的 depth 和分母自身的 height 为基准计算上下偏移，
            // 避免像之前那样使用整体 Depth 导致上下间距被放大。
            _numShiftUp = _num.Depth + _gap + _ruleThickness;
            _denShiftDown = _den.Height + _gap + _ruleThickness;

            Height = _num.Height + _numShiftUp;
            Depth = _den.Depth + _denShiftDown;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            var center = xStart + Width / 2;

            // 分子
            var numBaseline = baselineY - _numShiftUp;
            var numX = center - _num.Width / 2;
            _num.Draw(canvas, numX, numBaseline);

            // 分数线
            var lineY = baselineY - _ruleThickness / 2;
            canvas.DrawLine(xStart + 2, lineY, xStart + Width - 2, lineY, _linePaint);

            // 分母
            var denBaseline = baselineY + _denShiftDown;
            var denX = center - _den.Width / 2;
            _den.Draw(canvas, denX, denBaseline);
        }
    }

    private sealed class EnvironmentBox : Box
    {
        private readonly List<List<Box>> _rows;
        private readonly float[] _colWidths;
        private readonly float _colGap;
        private readonly float _rowGap;
        private readonly HashSet<int> _verticalRuleBeforeColumn;
        private readonly SKColor _color;

        public EnvironmentBox(List<List<Box>> rows, float[] colWidths, float colGap, float rowGap, int[]? verticalRuleBeforeColumn, SKColor lineColor)
        {
            _rows = rows;
            _color = lineColor;
            _colWidths = colWidths;
            _colGap = colGap;
            _rowGap = rowGap;
            _verticalRuleBeforeColumn = verticalRuleBeforeColumn != null ? new HashSet<int>(verticalRuleBeforeColumn) : [];

            Width = 0;
            foreach (var w in _colWidths)
                Width += w;
            Width += _colGap * Math.Max(0, _colWidths.Length - 1);

            float totalAbove = 0,
                totalBelow = 0;
            foreach (var row in _rows)
            {
                float h = 0,
                    d = 0;
                foreach (var cell in row)
                {
                    h = Math.Max(h, cell.Height);
                    d = Math.Max(d, cell.Depth);
                }
                totalAbove += h + _rowGap;
                totalBelow += d + _rowGap;
            }

            Height = totalAbove;
            Depth = totalBelow;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            using var linePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                IsAntialias = true,
                Color = _color
            };

            float matrixTop = baselineY - Height;
            float matrixBottom = baselineY + Depth;
            var currentBaseline = baselineY - Height + _rowGap;

            foreach (var row in _rows)
            {
                float rowHeight = 0,
                    rowDepth = 0;
                foreach (var cell in row)
                {
                    rowHeight = Math.Max(rowHeight, cell.Height);
                    rowDepth = Math.Max(rowDepth, cell.Depth);
                }

                float rowBaseline = currentBaseline + rowHeight;
                float x = xStart;
                for (int c = 0; c < _colWidths.Length; c++)
                {
                    Box? cell = c < row.Count ? row[c] : null;
                    if (cell != null)
                    {
                        var cellX = x;
                        cell.Draw(canvas, cellX, rowBaseline);
                    }
                    x += _colWidths[c];
                    x += _colGap;
                }

                currentBaseline = rowBaseline + rowDepth + _rowGap;
            }

            // 增广矩阵：竖线贯穿整矩阵高度，在指定列右侧画一整条竖线
            if (_verticalRuleBeforeColumn.Count > 0)
            {
                float x = xStart;
                for (int c = 0; c < _colWidths.Length; c++)
                {
                    x += _colWidths[c];
                    if (_verticalRuleBeforeColumn.Contains(c + 1))
                    {
                        float lineX = x + _colGap * 0.5f;
                        canvas.DrawLine(lineX, matrixTop, lineX, matrixBottom, linePaint);
                    }
                    x += _colGap;
                }
            }
        }
    }

    /// <summary>
    /// 近似 TeX 的可伸缩括号/竖线，用简单的直线和短横线绘制，使其高度与内部内容匹配。
    /// 用于 pmatrix / bmatrix / vmatrix / Vmatrix 等环境两侧的定界符。
    /// </summary>
    private sealed class DelimiterBox : Box
    {
        private readonly string _kind;
        private readonly float _strokeWidth;
        private readonly SKColor _color;

        public DelimiterBox(string kind, float above, float below, float em, SKColor color)
        {
            _kind = kind;
            _color = color;
            // 宽度按 em 的固定比例，保证既不太窄也不过宽
            _strokeWidth = Math.Max(1f, em * 0.06f);
            Width = em * 0.45f;
            Height = above;
            Depth = below;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            float top = baselineY - Height;
            float bottom = baselineY + Depth;
            float x0 = xStart;
            float x1 = xStart + Width;
            // 花括号用更粗线宽，使轮廓清晰（Bmatrix、cases 等）
            float strokeW = (_kind is "{" or "}") ? Math.Max(_strokeWidth * 2.2f, 1.8f) : _strokeWidth;
            using var paint = new SKPaint
            {
                Color = _color,
                StrokeWidth = strokeW,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            switch (_kind)
            {
                case "(":
                {
                    // 使用简单的贝塞尔曲线勾勒左圆括号轮廓，使其在视觉上更接近真实圆括号。
                    using var path = new SKPath();
                    float midY = (top + bottom) / 2f;
                    float ctrlX = x0 + Width * 0.25f;
                    path.MoveTo(x1, top);
                    path.CubicTo(ctrlX, top, ctrlX, midY, x0 + _strokeWidth, midY);
                    path.CubicTo(ctrlX, midY, ctrlX, bottom, x1, bottom);
                    canvas.DrawPath(path, paint);
                    break;
                }
                case ")":
                {
                    using var path = new SKPath();
                    float midY = (top + bottom) / 2f;
                    float ctrlX = x1 - Width * 0.25f;
                    path.MoveTo(x0, top);
                    path.CubicTo(ctrlX, top, ctrlX, midY, x1 - _strokeWidth, midY);
                    path.CubicTo(ctrlX, midY, ctrlX, bottom, x0, bottom);
                    canvas.DrawPath(path, paint);
                    break;
                }
                case "[":
                {
                    // 左方括号：竖线在左，水平线向右伸出
                    canvas.DrawLine(x0, top, x0, bottom, paint);
                    canvas.DrawLine(x0, top, x1, top, paint);
                    canvas.DrawLine(x0, bottom, x1, bottom, paint);
                    break;
                }
                case "]":
                {
                    // 右方括号：竖线在右，水平线向左伸出
                    canvas.DrawLine(x1, top, x1, bottom, paint);
                    canvas.DrawLine(x1, top, x0, top, paint);
                    canvas.DrawLine(x1, bottom, x0, bottom, paint);
                    break;
                }
                case "|":
                {
                    float mid = (x0 + x1) / 2f;
                    canvas.DrawLine(mid, top, mid, bottom, paint);
                    break;
                }
                case "{":
                {
                    // 左花括号：参考 SVG 五段贝塞尔，尖头在 x0 指向内容侧，无渐变/点缀
                    using var path = new SKPath();
                    float midY = (top + bottom) / 2f;
                    float h = bottom - top;
                    float xBulge = x0 + Width / 3f;
                    float y1 = top + 60f / 260f * h;
                    float y2 = midY + 40f / 260f * h;
                    float y3 = bottom - 40f / 260f * h;
                    path.MoveTo(x1, top);
                    path.CubicTo(xBulge, top, xBulge, top + 40f / 260f * h, xBulge, y1);
                    path.CubicTo(xBulge, top + 100f / 260f * h, xBulge, top + 110f / 260f * h, x0, midY);
                    path.CubicTo(xBulge, midY + 10f / 260f * h, xBulge, midY + 20f / 260f * h, xBulge, y2);
                    path.CubicTo(xBulge, bottom - 60f / 260f * h, xBulge, bottom - 40f / 260f * h, xBulge, y3);
                    path.CubicTo(xBulge, bottom - 20f / 260f * h, xBulge, bottom, x1, bottom);
                    canvas.DrawPath(path, paint);
                    break;
                }
                case "}":
                {
                    // 右花括号：与 { 对称，尖头在 x1 指向内容侧
                    using var path = new SKPath();
                    float midY = (top + bottom) / 2f;
                    float h = bottom - top;
                    float xBulge = x1 - Width / 3f;
                    float y1 = top + 60f / 260f * h;
                    float y2 = midY + 40f / 260f * h;
                    float y3 = bottom - 40f / 260f * h;
                    path.MoveTo(x0, top);
                    path.CubicTo(xBulge, top, xBulge, top + 40f / 260f * h, xBulge, y1);
                    path.CubicTo(xBulge, top + 100f / 260f * h, xBulge, top + 110f / 260f * h, x1, midY);
                    path.CubicTo(xBulge, midY + 10f / 260f * h, xBulge, midY + 20f / 260f * h, xBulge, y2);
                    path.CubicTo(xBulge, bottom - 60f / 260f * h, xBulge, bottom - 40f / 260f * h, xBulge, y3);
                    path.CubicTo(xBulge, bottom - 20f / 260f * h, xBulge, bottom, x0, bottom);
                    canvas.DrawPath(path, paint);
                    break;
                }
                default:
                {
                    // "‖" 或其他：画双竖线
                    float mid = (x0 + x1) / 2f;
                    float offset = _strokeWidth * 2;
                    canvas.DrawLine(mid - offset, top, mid - offset, bottom, paint);
                    canvas.DrawLine(mid + offset, top, mid + offset, bottom, paint);
                    break;
                }
            }
        }
    }

    #endregion

    #region AST -> Box

    private static Box BuildBox(MathNode node, RenderContext ctx, MathStyle style, float scale)
    {
        var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = ctx.TextColor,
            Style = SKPaintStyle.Fill
        };

        switch (node)
        {
            case MathSymbol sym:
            {
                var italic = ShouldItalicize(sym.Text);
                var drawText = sym.Text;
                // 单字母拉丁变量：用 Unicode 数学斜体区段（OpenType Math 的 Regular 面即含这些字形）
                if (italic && TryMapLatinLetterToMathematicalItalic(sym.Text, out var mathItalic))
                {
                    // 仅当当前数学字体可渲染该码位时才映射；否则保留原字符并走斜体/倾斜回退。
                    if (ctx.CanRenderWithMathTypeface(mathItalic))
                    {
                        drawText = mathItalic;
                        italic = false;
                    }
                }
                var font = ctx.CreateSymbolFont(drawText, scale, italicPreferred: italic);
                return new SymbolBox(drawText, font, textPaint);
            }
            case MathText txt:
            {
                // 文本节点：始终使用正文字体正体（不斜体），并允许 CJK 回退字体
                var font = ctx.CreateTextFont(scale);
                return new SymbolBox(txt.Text, font, textPaint);
            }
            case MathSequence seq:
            {
                // \left / \right 解析为空 MathSequence，故实际为 [ 空, "[", array, 空, "]" ]。找序列中首次 "["、末次 "]" 且中间恰有一个带 | 的 array 时只画 array，避免最外侧多两个符号。
                var arrWithBar = seq.Children.OfType<MathEnvironment>()
                    .FirstOrDefault(e => e.Name == "array" && !string.IsNullOrEmpty(e.ColumnSpec) && e.ColumnSpec.Contains('|'));
                if (arrWithBar != null)
                {
                    int firstBracket = -1, lastBracket = -1;
                    for (int i = 0; i < seq.Children.Count; i++)
                    {
                        if (seq.Children[i] is MathSymbol s && s.Text == "[") { firstBracket = i; break; }
                    }
                    for (int i = seq.Children.Count - 1; i >= 0; i--)
                    {
                        if (seq.Children[i] is MathSymbol s && s.Text == "]") { lastBracket = i; break; }
                    }
                    if (firstBracket >= 0 && lastBracket > firstBracket)
                    {
                        return BuildBox(arrWithBar, ctx, style, scale);
                    }
                }
                var children = new List<Box>();
                foreach (var child in seq.Children)
                {
                    children.Add(BuildBox(child, ctx, style, scale));
                }
                var spacing = ctx.BaseFontSize * scale * 0.15f;
                return new HorizontalBox(children, spacing);
            }
            case MathSupSub ss:
            {
                var baseBox = BuildBox(ss.Base, ctx, style, scale);
                var scriptStyle = NextStyle(style);
                var scriptScale = style is MathStyle.Display or MathStyle.Text ? 0.75f : 0.7f;
                Box? supBox =
                    ss.Sup != null ? BuildBox(ss.Sup, ctx, scriptStyle, scale * scriptScale) : null;
                Box? subBox =
                    ss.Sub != null ? BuildBox(ss.Sub, ctx, scriptStyle, scale * scriptScale) : null;
                var gap = ctx.BaseFontSize * scale * 0.1f;
                var em = ctx.BaseFontSize * scale;
                // Display 模式下大算子（∑ ∫ ∏ ∮ 等）上下标置于主体正上/正下方
                if (style == MathStyle.Display && ss.Base is MathSymbol sym && IsBigOp(sym.Text))
                    return new BigOpBox(baseBox, supBox, subBox, ctx.BaseFontSize * scale * 0.2f);
                return new SupSubBox(baseBox, supBox, subBox, gap, em);
            }
            case MathFraction frac:
            {
                var scriptStyle = style == MathStyle.Display ? MathStyle.Text : MathStyle.Script;
                var scriptScale = 0.8f;
                var num = BuildBox(frac.Numerator, ctx, scriptStyle, scale * scriptScale);
                var den = BuildBox(frac.Denominator, ctx, scriptStyle, scale * scriptScale);
                // 适当加粗分数线并放大上下间距，使在高分辨率屏幕和数学字体下更清晰。
                var ruleThickness = Math.Max(1, ctx.BaseFontSize * scale * 0.075f);
                var gap = ctx.BaseFontSize * scale * 0.25f;
                var linePaint = new SKPaint
                {
                    Color = ctx.TextColor,
                    StrokeWidth = ruleThickness,
                    IsAntialias = true
                };
                return new FractionBox(num, den, ruleThickness, gap, linePaint);
            }
            case MathRoot root:
            {
                var innerStyle = style == MathStyle.Display ? MathStyle.Text : style;
                var degreeStyle = NextStyle(innerStyle);
                var degreeScale = 0.7f;
                var degree =
                    root.Degree != null
                        ? BuildBox(root.Degree, ctx, degreeStyle, scale * degreeScale)
                        : null;
                var radicand = BuildBox(root.Radicand, ctx, innerStyle, scale);
                var radicalFont = ctx.CreateFont(scale, italic: false);
                return new RootBox(degree, radicand, radicalFont, textPaint);
            }
            case MathAccent accent:
            {
                var baseBox = BuildBox(accent.Base, ctx, style, scale);
                var accentGlyph = GetAccentGlyph(accent.AccentName);
                var accentFont = ctx.CreateSymbolFont(
                    accentGlyph,
                    scale * 0.8f,
                    italicPreferred: false
                );
                var accentBox = new SymbolBox(accentGlyph, accentFont, textPaint);
                var gap = ctx.BaseFontSize * scale * 0.08f;
                return new AccentBox(baseBox, accentBox, gap);
            }
            case MathEnvironment env:
            {
                // 多行环境：eqnarray / align / matrix / pmatrix 等，统一按列对齐
                var allRows = new List<List<Box>>();
                int maxCols = 0;
                foreach (var row in env.Rows)
                {
                    var cellBoxes = new List<Box>();
                    foreach (var cell in row.Cells)
                    {
                        cellBoxes.Add(BuildBox(cell, ctx, style, scale));
                    }
                    allRows.Add(cellBoxes);
                    if (cellBoxes.Count > maxCols)
                        maxCols = cellBoxes.Count;
                }

                if (maxCols == 0)
                    return new HorizontalBox(new List<Box>(), 0);

                var colWidths = new float[maxCols];
                foreach (var row in allRows)
                {
                    for (int i = 0; i < row.Count; i++)
                    {
                        colWidths[i] = Math.Max(colWidths[i], row[i].Width);
                    }
                }

                var colGap = ctx.BaseFontSize * scale * 1.0f;
                var rowGap = ctx.BaseFontSize * scale * 0.4f;
                int[]? verticalRuleBeforeColumn = null;
                if (env.Name == "array" && !string.IsNullOrEmpty(env.ColumnSpec) && env.ColumnSpec.Contains('|'))
                {
                    var list = new List<int>();
                    int col = 0;
                    foreach (var part in env.ColumnSpec.Split('|'))
                    {
                        if (col > 0)
                            list.Add(col);
                        col += part.Length;
                    }
                    verticalRuleBeforeColumn = list.ToArray();
                }
                var core = new EnvironmentBox(allRows, colWidths, colGap, rowGap, verticalRuleBeforeColumn, ctx.TextColor);

                // 矩阵类环境：在左右加上括号/中括号/竖线
                string? leftDelim = null,
                    rightDelim = null;
                switch (env.Name)
                {
                    case "pmatrix":
                        leftDelim = "(";
                        rightDelim = ")";
                        break;
                    case "bmatrix":
                        leftDelim = "[";
                        rightDelim = "]";
                        break;
                    case "Bmatrix":
                        leftDelim = "{";
                        rightDelim = "}";
                        break;
                    case "vmatrix":
                        leftDelim = "|";
                        rightDelim = "|";
                        break;
                    case "Vmatrix":
                        leftDelim = "‖";
                        rightDelim = "‖";
                        break;
                    case "cases":
                        leftDelim = "{";
                        rightDelim = ""; // 仅左侧花括号
                        break;
                    case "array":
                        // 增广矩阵：列格式含 | 时与 bmatrix 一致，用同一套 DelimiterBox 画两侧方括号
                        if (!string.IsNullOrEmpty(env.ColumnSpec) && env.ColumnSpec.Contains('|'))
                        {
                            leftDelim = "[";
                            rightDelim = "]";
                        }
                        break;
                }

                if (leftDelim is null)
                    return core;

                var boxes = new List<Box>();
                var em = ctx.BaseFontSize * scale;
                // 定界符的高度直接复用核心环境的高度/深度，以达到“伸缩括号”的效果。
                if (!string.IsNullOrEmpty(leftDelim))
                    boxes.Add(new DelimiterBox(leftDelim, core.Height, core.Depth, em, textPaint.Color));
                boxes.Add(core);
                if (!string.IsNullOrEmpty(rightDelim))
                    boxes.Add(new DelimiterBox(rightDelim, core.Height, core.Depth, em, textPaint.Color));

                var outerGap = ctx.BaseFontSize * scale * 0.2f;
                return new HorizontalBox(boxes, outerGap);
            }
            default:
            {
                // 未识别节点：退化为一个空盒
                var font = ctx.CreateFont(scale);
                return new SymbolBox("?", font, textPaint);
            }
        }
    }

    private static bool IsBigOp(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        return text is "∑" or "∫" or "∏" or "∮" or "∐" or "⋂" or "⋃";
    }

    /// <summary>根据当前样式推导子脚本/更小级别的样式。</summary>
    private static MathStyle NextStyle(MathStyle style) =>
        style switch
        {
            MathStyle.Display => MathStyle.Text,
            MathStyle.Text => MathStyle.Script,
            MathStyle.Script => MathStyle.ScriptScript,
            _ => MathStyle.ScriptScript
        };

    #endregion

    /// <summary>
    /// 决定某个符号是否使用斜体（主要针对拉丁/希腊字母）：
    /// - 单个拉丁/希腊字母：斜体（变量）
    /// - 多字符函数名/算子（sin/log/grad/div 等）：正体
    /// </summary>
    private static bool ShouldItalicize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        if (text.Length == 1)
        {
            var c = text[0];
            // 拉丁字母使用斜体，数字/符号保持正体
            return char.IsLetter(c);
        }

        // 常见函数/算子名保持正体，以贴近 TeX 习惯
        var lower = text.ToLowerInvariant();
        return lower switch
        {
            "sin"
            or "cos"
            or "tan"
            or "cot"
            or "sec"
            or "csc"
            or "log"
            or "ln"
            or "exp"
            or "lim"
            or "max"
            or "min"
            or "sup"
            or "inf"
            or "det"
            or "dim"
            or "ker"
            or "arg"
            or "grad"
            or "div"
            or "curl"
                => false,
            _ => false
        };
    }

    /// <summary>将 LaTeX 重音命令名映射为实际绘制的符号。</summary>
    private static string GetAccentGlyph(string accentName) =>
        accentName switch
        {
            "hat" or "widehat" => "ˆ", // 抑扬符
            "tilde" or "widetilde" => "̃", // 波浪
            "bar" => "‾", // 上划线
            // 使用更轻、更平的箭头作为向量符号，避免与基底竖直方向错位感太强
            "vec" => "⟶",
            _ => "ˆ"
        };

    /// <summary>重音盒子：在基底之上绘制一个小符号（hat/tilde/vec 等）。</summary>
    private sealed class AccentBox : Box
    {
        private readonly Box _base;
        private readonly Box _accent;
        private readonly float _gap;

        public AccentBox(Box @base, Box accent, float gap)
        {
            _base = @base;
            _accent = accent;
            _gap = gap;

            Width = Math.Max(_base.Width, _accent.Width);
            // 行高：基底高度 + 小间隙 + 重音高度，使帽子几乎贴着基底但仍保留固定空隙
            Height = _base.Height + _gap + _accent.Height;
            Depth = _base.Depth;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            var center = xStart + Width / 2;

            // 基底水平居中
            var baseX = center - _base.Width / 2;
            _base.Draw(canvas, baseX, baselineY);

            // 重音符号画在基底上方：
            // 令重音“下缘”位于基底上缘之上一小段固定间隙（_gap），
            // 对所有重音命令（hat/vec/bar/…）采用统一规则，避免按命令名硬编码。
            // 注意：这里不再叠加 accent 的高度，否则会把基线推入主体内部，导致重音穿透符号。
            var baseTop = baselineY - _base.Height;
            var accentBaseline = baseTop - _gap;
            var accentX = center - _accent.Width / 2;
            _accent.Draw(canvas, accentX, accentBaseline);
        }
    }
}

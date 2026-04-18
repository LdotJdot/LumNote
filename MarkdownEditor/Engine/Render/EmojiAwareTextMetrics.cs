using System.Globalization;
using System.Text;
using MarkdownEditor.Engine.Layout;
using SkiaSharp;

namespace MarkdownEditor.Engine.Render;

/// <summary>
/// 正文与彩色 Emoji 字体分段测量/绘制/命中，使 Skia 在混排时宽度与 <see cref="SKCanvas.DrawText"/> 一致。
/// </summary>
public static class EmojiAwareTextMetrics
{
    /// <summary>是否应对该样式使用正文+Emoji 分段（代码/公式/图片等保持单字体）。</summary>
    public static bool IsEmojiAwareRunStyle(RunStyle style) =>
        style
            is RunStyle.Normal
                or RunStyle.Bold
                or RunStyle.Italic
                or RunStyle.BoldItalic
                or RunStyle.Strikethrough
                or RunStyle.Link
                or RunStyle.LinkBold
                or RunStyle.LinkItalic
                or RunStyle.LinkBoldItalic
                or RunStyle.Heading1
                or RunStyle.Heading2
                or RunStyle.Heading3
                or RunStyle.Heading4
                or RunStyle.Heading5
                or RunStyle.Heading6
                or RunStyle.TableCell
                or RunStyle.TableHeaderCell
                or RunStyle.FootnoteRef;

    public static bool ContainsLikelyEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var te = StringInfo.GetTextElementEnumerator(text);
        while (te.MoveNext())
        {
            if (IsEmojiTextElement(te.GetTextElement()))
                return true;
        }
        return false;
    }

    public static float Measure(string text, SKFont bodyFont, SKFont emojiFont)
    {
        if (string.IsNullOrEmpty(text))
            return 0f;
        float w = 0;
        foreach (var (segment, useEmoji) in EnumerateSegments(text))
        {
            var f = useEmoji ? emojiFont : bodyFont;
            w += f.MeasureText(segment);
        }
        return w;
    }

    public static void Draw(
        SKCanvas canvas,
        string text,
        float x,
        float baselineY,
        SKFont bodyFont,
        SKFont emojiFont,
        SKPaint paint
    )
    {
        if (string.IsNullOrEmpty(text))
            return;
        float curX = x;
        foreach (var (segment, useEmoji) in EnumerateSegments(text))
        {
            var f = useEmoji ? emojiFont : bodyFont;
            canvas.DrawText(segment, curX, baselineY, f, paint);
            curX += f.MeasureText(segment);
        }
    }

    /// <summary>与 <see cref="Measure"/> 一致的 UTF-16 列索引命中（用于选区）。</summary>
    public static int MeasureTextOffset(string text, float xHit, SKFont bodyFont, SKFont emojiFont)
    {
        if (string.IsNullOrEmpty(text) || xHit <= 0)
            return 0;
        FillPrefixWidths(text, bodyFont, emojiFont, out var widths);
        float total = widths[text.Length];
        if (xHit >= total)
            return text.Length;
        for (int i = 1; i <= text.Length; i++)
        {
            if (widths[i] >= xHit)
                return i - 1;
        }
        return text.Length;
    }

    /// <summary>构建 <c>widths[i] = 前 i 个 UTF-16 码元的宽度</c>，长度 <c>text.Length + 1</c>。</summary>
    public static void FillPrefixWidths(
        string text,
        SKFont bodyFont,
        SKFont emojiFont,
        out float[] widths
    )
    {
        widths = new float[text.Length + 1];
        if (text.Length == 0)
            return;
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        for (int bi = 0; bi < boundaries.Length; bi++)
        {
            int s = boundaries[bi];
            int e = bi + 1 < boundaries.Length ? boundaries[bi + 1] : text.Length;
            int len = e - s;
            if (len <= 0)
                continue;
            var el = text.AsSpan(s, len);
            bool em = IsEmojiTextElement(el.ToString());
            var font = em ? emojiFont : bodyFont;
            for (int j = 0; j < len; j++)
                widths[s + j + 1] = widths[s] + font.MeasureText(text.AsSpan(s, j + 1));
        }
    }

    private static IEnumerable<(string segment, bool emojiFont)> EnumerateSegments(string text)
    {
        var sb = new StringBuilder();
        bool? lastEmoji = null;
        var te = StringInfo.GetTextElementEnumerator(text);
        while (te.MoveNext())
        {
            var el = te.GetTextElement();
            bool em = IsEmojiTextElement(el);
            if (sb.Length == 0)
            {
                sb.Append(el);
                lastEmoji = em;
            }
            else if (em == lastEmoji)
            {
                sb.Append(el);
            }
            else
            {
                yield return (sb.ToString(), lastEmoji!.Value);
                sb.Clear();
                sb.Append(el);
                lastEmoji = em;
            }
        }
        if (sb.Length > 0 && lastEmoji.HasValue)
            yield return (sb.ToString(), lastEmoji.Value);
    }

    internal static bool IsEmojiTextElement(string element)
    {
        if (string.IsNullOrEmpty(element))
            return false;
        bool anyPrimary = false;
        foreach (var r in element.EnumerateRunes())
        {
            uint v = (uint)r.Value;
            if (v == 0x200D || v == 0xFE0F || (v >= 0xFE00 && v <= 0xFE0E))
                continue;
            if (v >= 0x1F3FB && v <= 0x1F3FF)
                return true;
            if (IsPrimaryEmojiCodepoint(v))
                anyPrimary = true;
        }
        return anyPrimary;
    }

    private static bool IsPrimaryEmojiCodepoint(uint v)
    {
        if (v >= 0x1F300 && v <= 0x1FAFF)
            return true;
        if (v >= 0x1F000 && v <= 0x1F2FF)
            return true;
        if (v >= 0x2600 && v <= 0x26FF)
            return true;
        if (v >= 0x2700 && v <= 0x27BF)
            return true;
        if (v >= 0x2300 && v <= 0x23FF)
            return true;
        if (v >= 0x2B50 && v <= 0x2B55)
            return true;
        if (v >= 0x1F1E6 && v <= 0x1F1FF)
            return true;
        if (v == 0x24C2 || v == 0x3297 || v == 0x3299)
            return true;
        if (v >= 0x3030 && v <= 0x303D)
            return true;
        if (v >= 0x2049 && v <= 0x2049)
            return true;
        return false;
    }
}

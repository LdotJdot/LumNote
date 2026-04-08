using System;
using System.Collections.Generic;
using SkiaSharp;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 为同一 (样式, 文本) 缓存 UTF-16 前缀宽度数组 <c>widths[i] = MeasureText(text.AsSpan(0,i))</c>，
/// 在仅改变行宽（resize）而文本不变时，折行可用二分 + 回溯找断点，避免重复 O(n) 次测量。
/// </summary>
internal sealed class SkiaPrefixWidthCache
{
    private const int MinTextLength = 24;
    private const int MaxEntries = 384;

    private readonly Dictionary<(RunStyle Style, string Text), float[]> _map = new();
    private readonly List<(RunStyle Style, string Text)> _insertOrder = new();

    public void Clear()
    {
        _map.Clear();
        _insertOrder.Clear();
    }

    /// <summary>文本过短时不缓存，返回 null，调用方走原有逐前缀测量循环。</summary>
    public float[]? GetOrBuildPrefixWidths(
        string text,
        RunStyle style,
        SKFont font)
    {
        if (string.IsNullOrEmpty(text) || text.Length < MinTextLength)
            return null;

        var key = (style, text);
        if (_map.TryGetValue(key, out var cached))
            return cached;

        if (_map.Count >= MaxEntries)
            EvictOldest();

        var widths = new float[text.Length + 1];
        for (int i = 1; i <= text.Length; i++)
        {
            widths[i] = font.MeasureText(text.AsSpan(0, i));
            LayoutDiagnostics.OnSkiaMeasureText();
        }

        _map[key] = widths;
        _insertOrder.Add(key);
        return widths;
    }

    private void EvictOldest()
    {
        int removeCount = MaxEntries / 4;
        removeCount = Math.Max(1, Math.Min(removeCount, _insertOrder.Count));
        for (int r = 0; r < removeCount && _insertOrder.Count > 0; r++)
        {
            var k = _insertOrder[0];
            _insertOrder.RemoveAt(0);
            _map.Remove(k);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using MarkdownEditor.Models;

namespace MarkdownEditor.Services;

/// <summary>
/// 比对时在文档中插入/移除“虚拟行”（已删除行占位），实现错开显示、无重叠。
/// 虚拟行以特殊前缀标记，退出比对时移除，不影响保存内容。
/// </summary>
public static class DiffVirtualLineHelper
{
    /// <summary>虚拟行行首标记（两字符，不可见），用于退出比对时识别并移除。</summary>
    public const string VirtualLinePrefix = "\u200B\u200B";

    private static readonly string[] LineSplits = new[] { "\r\n", "\r", "\n" };

    /// <summary>
    /// 在真实内容中按 DeletedLines 插入虚拟行，并生成扩展行映射（显示文档行号 → 差异类型）。
    /// 虚拟行在扩展 map 中为 Deleted；真实行类型按原 map 平移。
    /// </summary>
    public static (string displayContent, GitDiffLineMap extendedMap) BuildDisplayWithVirtualLines(string realContent, GitDiffLineMap? originalMap)
    {
        if (originalMap == null || originalMap.DeletedLines.Count == 0)
            return (realContent ?? "", originalMap ?? new GitDiffLineMap());

        var realLines = (realContent ?? "").Split(LineSplits, StringSplitOptions.None);
        var deletedByAnchor = originalMap.DeletedLines
            .GroupBy(d => d.ShowBeforeNewLine <= 0 ? 1 : d.ShowBeforeNewLine)
            .ToDictionary(g => g.Key, g => g.ToList());

        var displayLines = new List<string>();
        var extendedMap = new GitDiffLineMap();
        int displayLineNumber = 0;

        for (var i = 0; i < realLines.Length; i++)
        {
            var realLineNumber = i + 1;
            if (deletedByAnchor.TryGetValue(realLineNumber, out var list))
            {
                foreach (var d in list)
                {
                    displayLineNumber++;
                    displayLines.Add(VirtualLinePrefix + "- " + (d.Content ?? "").Replace("\r", "").Replace("\n", " "));
                    extendedMap.Set(displayLineNumber, GitDiffLineKind.Removed);
                }
            }
            displayLineNumber++;
            displayLines.Add(realLines[i]);
            extendedMap.Set(displayLineNumber, originalMap.Get(realLineNumber));
        }

        var displayContent = string.Join(Environment.NewLine, displayLines);
        return (displayContent, extendedMap);
    }

    /// <summary>从当前文档中移除所有虚拟行（以 <see cref="VirtualLinePrefix"/> 开头的行），得到真实内容。</summary>
    public static string RemoveVirtualLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var lines = content.Split(LineSplits, StringSplitOptions.None);
        var kept = lines.Where(line => !line.StartsWith(VirtualLinePrefix, StringComparison.Ordinal)).ToArray();
        return string.Join(Environment.NewLine, kept);
    }

    /// <summary>当前内容是否包含虚拟行（即处于“带虚拟行的比对”状态）。</summary>
    public static bool ContainsVirtualLines(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var lines = content.Split(LineSplits, StringSplitOptions.None);
        return lines.Any(line => line.StartsWith(VirtualLinePrefix, StringComparison.Ordinal));
    }
}

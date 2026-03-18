using System.Text;

namespace MarkdownEditor.Core;

/// <summary>
/// 去除从 Word/浏览器等复制路径时带入的不可见字符（如 U+202A），否则 Path.IsPathRooted 误判、图片无法加载、Process.Start 报 Win32Exception。
/// </summary>
public static class PathSanitizer
{
    public static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return s ?? "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (IsStripChar(c))
                continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static bool IsStripChar(char c)
    {
        if (c == '\uFEFF')
            return true;
        if (c is >= '\u200B' and <= '\u200F')
            return true;
        if (c is >= '\u202A' and <= '\u202E')
            return true;
        if (c is >= '\u2066' and <= '\u2069')
            return true;
        return false;
    }
}

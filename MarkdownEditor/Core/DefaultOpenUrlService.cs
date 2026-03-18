using System.Diagnostics;
using System.IO;

namespace MarkdownEditor.Core;

/// <summary>打开 URL（浏览器）或本地路径（关联程序）；绝不把 Windows 绝对路径拼成 https://（会触发 Win32Exception）。</summary>
public sealed class DefaultOpenUrlService : IOpenUrlService
{
    public void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var t = url.Trim();
        try
        {
            if (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var p = new Uri(t).LocalPath;
                    if (File.Exists(p) || Directory.Exists(p))
                        ShellOpen(p);
                }
                catch { /* 忽略 */ }
                return;
            }

            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ShellOpen(t);
                return;
            }

            if (t.Length > 2 && t[0] == '/' && t[1] == '/' && t[2] != '/')
            {
                ShellOpen("https:" + t);
                return;
            }

            var local = t.Replace('/', Path.DirectorySeparatorChar);
            if (LooksLikeFileSystemPath(local))
            {
                try
                {
                    local = Path.GetFullPath(local);
                }
                catch
                {
                    return;
                }
                if (File.Exists(local) || Directory.Exists(local))
                    ShellOpen(local);
                return;
            }

            ShellOpen("https://" + t);
        }
        catch
        {
            // 忽略
        }
    }

    private static void ShellOpen(string pathOrUrl)
    {
        Process.Start(
            new ProcessStartInfo
            {
                FileName = pathOrUrl,
                UseShellExecute = true,
            }
        );
    }

    /// <summary>是否为 Windows 盘符路径、UNC 或类 Unix 绝对路径（非 URL）。</summary>
    private static bool LooksLikeFileSystemPath(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Length >= 2 && s[1] == ':' && char.IsLetter(s[0])) return true;
        if (s.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        if (s.StartsWith('/') && !s.StartsWith("//", StringComparison.Ordinal)) return true;
        return false;
    }
}

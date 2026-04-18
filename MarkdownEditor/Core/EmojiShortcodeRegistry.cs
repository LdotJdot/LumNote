using System.Collections.Frozen;
using System.Text.Json;

namespace MarkdownEditor.Core;

/// <summary>
/// 从 gemoji <c>db/emoji.json</c> 构建的 <c>:name:</c> → Unicode 字符映射（含 aliases 与 tags）。
/// </summary>
public static class EmojiShortcodeRegistry
{
    private const string EmbeddedResourceName = "MarkdownEditor.Core.Data.emoji.json";
    private static FrozenDictionary<string, string>? _map;
    private static readonly object Gate = new();

    /// <summary>若尚未加载或资源缺失则为 false。</summary>
    public static bool IsLoaded => _map != null && _map.Count > 0;

    public static FrozenDictionary<string, string> GetMap()
    {
        EnsureLoaded();
        return _map ?? FrozenDictionary<string, string>.Empty;
    }

    /// <summary>尝试将短名解析为 emoji 字符串（可能为 ZWJ 序列）。</summary>
    public static bool TryGet(string name, out string emoji)
    {
        EnsureLoaded();
        if (_map == null || _map.Count == 0)
        {
            emoji = "";
            return false;
        }
        return _map.TryGetValue(name, out emoji!);
    }

    private static void EnsureLoaded()
    {
        if (_map != null)
            return;
        lock (Gate)
        {
            if (_map != null)
                return;
            _map = LoadFromEmbeddedResource();
        }
    }

    private static FrozenDictionary<string, string> LoadFromEmbeddedResource()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var asm = typeof(EmojiShortcodeRegistry).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null)
                return dict.ToFrozenDictionary(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return dict.ToFrozenDictionary(StringComparer.Ordinal);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("emoji", out var emojiProp))
                    continue;
                var emoji = emojiProp.GetString();
                if (string.IsNullOrEmpty(emoji))
                    continue;
                void AddKey(string? key)
                {
                    if (string.IsNullOrEmpty(key))
                        return;
                    dict[key] = emoji;
                }
                if (el.TryGetProperty("aliases", out var aliases))
                {
                    foreach (var a in aliases.EnumerateArray())
                        AddKey(a.GetString());
                }
                if (el.TryGetProperty("tags", out var tags))
                {
                    foreach (var t in tags.EnumerateArray())
                        AddKey(t.GetString());
                }
            }
        }
        catch
        {
            // 资源损坏或解析失败时保持空表，短码不展开
        }
        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

using System.IO;
using System.Text.Json;

namespace MarkdownEditor;

// #region agent log
internal static class DebugLog
{
    private static string GetLogPath()
    {
        var baseDir = System.AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "debug-1d2609.log"));
    }

    public static void Write(string hypothesisId, string location, string message, object? data = null)
    {
        try
        {
            var payload = new
            {
                sessionId = "1d2609",
                hypothesisId,
                location,
                message,
                data,
                timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var line = JsonSerializer.Serialize(payload) + "\n";
            File.AppendAllText(GetLogPath(), line);
        }
        catch { }
    }
}
// #endregion

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkdownEditor.Core;

/// <summary>
/// 应用配置 - 界面与 Markdown 风格
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("ui")]
    public UiConfig Ui { get; set; } = new();

    [JsonPropertyName("markdown")]
    public MarkdownStyleConfig Markdown { get; set; } = new();

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".markdown-editor", "config.json");
}

public sealed class UiConfig
{
    [JsonPropertyName("documentListWidth")]
    public double DocumentListWidth { get; set; } = 280;

    [JsonPropertyName("editorWidth")]
    public double EditorWidth { get; set; } = 1; // 比例

    [JsonPropertyName("backgroundColor")]
    public string BackgroundColor { get; set; } = "#f6f8fa";

    [JsonPropertyName("sidebarBackground")]
    public string SidebarBackground { get; set; } = "#ffffff";

    [JsonPropertyName("headerBackground")]
    public string HeaderBackground { get; set; } = "#24292f";

    [JsonPropertyName("linkColor")]
    public string LinkColor { get; set; } = "#0566d2";
}

public sealed class MarkdownStyleConfig
{
    [JsonPropertyName("codeBlockBackground")]
    public string CodeBlockBackground { get; set; } = "#252526";

    [JsonPropertyName("codeBlockTextColor")]
    public string CodeBlockTextColor { get; set; } = "#dcdcdc";

    [JsonPropertyName("blockquoteBorderColor")]
    public string BlockquoteBorderColor { get; set; } = "#3f3f46";

    [JsonPropertyName("tableBorderColor")]
    public string TableBorderColor { get; set; } = "#3f3f46";

    [JsonPropertyName("tableHeaderBackground")]
    public string TableHeaderBackground { get; set; } = "#2a2d2e";

    [JsonPropertyName("heading1Size")]
    public int Heading1Size { get; set; } = 28;

    [JsonPropertyName("heading2Size")]
    public int Heading2Size { get; set; } = 24;

    [JsonPropertyName("codeFontFamily")]
    public string CodeFontFamily { get; set; } = "Cascadia Code,Consolas,Microsoft YaHei Mono,monospace";

    [JsonPropertyName("bodyFontFamily")]
    public string BodyFontFamily { get; set; } = "Microsoft YaHei UI,Segoe UI,PingFang SC,sans-serif";

    [JsonPropertyName("linkColor")]
    public string LinkColor { get; set; } = "#3794ff";

    // 普通文本颜色
    [JsonPropertyName("textColor")]
    public string TextColor { get; set; } = "#d4d4d4";

    // 预览区整体背景（目前右侧为 #1e1e1e，可用作对比参考）
    [JsonPropertyName("backgroundColor")]
    public string BackgroundColor { get; set; } = "#1e1e1e";

    // 数学块背景
    [JsonPropertyName("mathBackground")]
    public string MathBackground { get; set; } = "#252526";

    // 选中文本高亮颜色（含 alpha，AARRGGBB）
    [JsonPropertyName("selectionColor")]
    public string SelectionColor { get; set; } = "#503399ff";

    // 图片占位符背景
    [JsonPropertyName("imagePlaceholderColor")]
    public string ImagePlaceholderColor { get; set; } = "#404040";
}

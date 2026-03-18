using LumConfig;
using System.IO;
using System.Linq;

namespace MarkdownEditor.Core;

/// <summary>命名的一套 Markdown 样式，用于多套预设。</summary>
public sealed class MarkdownStylePreset
{
    public string Name { get; set; } = "";
    public MarkdownStyleConfig Style { get; set; } = new();
}

/// <summary>
/// 应用配置 - 界面与 Markdown 风格（使用 LumConfig 持久化，AOT 友好）
/// </summary>
public sealed class AppConfig
{
    public UiConfig Ui { get; set; } = new();
    public MarkdownStyleConfig Markdown { get; set; } = new();
    /// <summary>当前生效的预设名称，对应 StylePresets 中某一项的 Name。</summary>
    public string ActivePresetName { get; set; } = "深色";
    /// <summary>多套样式预设，可在此存多套后切换。</summary>
    public List<MarkdownStylePreset> StylePresets { get; set; } = new();

    public static AppConfig Load(string path)
    {
        var result = new AppConfig();
        var createdNew = !File.Exists(path);
        if (!createdNew)
        {
            try
            {
                var cfg = new LumConfigManager(path);

                // Ui
                result.Ui.DocumentListWidth = cfg.GetDouble("ui:documentListWidth") ?? result.Ui.DocumentListWidth;
                var sidebarCollapsed = cfg.GetString("ui:sidebarCollapsed");
                if (bool.TryParse(sidebarCollapsed, out var sc))
                    result.Ui.SidebarCollapsed = sc;
                result.Ui.EditorWidth = cfg.GetDouble("ui:editorWidth") ?? result.Ui.EditorWidth;
                // 不再恢复窗口尺寸和状态，避免最大化/还原时尺寸异常
                result.Ui.LayoutMode = cfg.GetString("ui:layoutMode") ?? result.Ui.LayoutMode;
                result.Ui.AutoSaveIntervalSeconds =
                    cfg.GetInt("ui:autoSaveIntervalSeconds") ?? result.Ui.AutoSaveIntervalSeconds;
                result.Ui.Theme = cfg.GetString("ui:theme") ?? result.Ui.Theme;
                result.Ui.BackgroundColor = cfg.GetString("ui:backgroundColor") ?? result.Ui.BackgroundColor;
                result.Ui.SidebarBackground = cfg.GetString("ui:sidebarBackground") ?? result.Ui.SidebarBackground;
                result.Ui.HeaderBackground = cfg.GetString("ui:headerBackground") ?? result.Ui.HeaderBackground;
                result.Ui.LinkColor = cfg.GetString("ui:linkColor") ?? result.Ui.LinkColor;
                LoadUiStyleFromCfg(cfg, "ui:style:dark", result.Ui.DarkStyle);
                LoadUiStyleFromCfg(cfg, "ui:style:light", result.Ui.LightStyle);

                // Markdown
                result.Markdown.CodeBlockBackground = cfg.GetString("markdown:codeBlockBackground") ?? result.Markdown.CodeBlockBackground;
                result.Markdown.CodeBlockTextColor = cfg.GetString("markdown:codeBlockTextColor") ?? result.Markdown.CodeBlockTextColor;
                result.Markdown.CodeKeywordColor = cfg.GetString("markdown:codeKeywordColor") ?? result.Markdown.CodeKeywordColor;
                result.Markdown.CodeStringColor = cfg.GetString("markdown:codeStringColor") ?? result.Markdown.CodeStringColor;
                result.Markdown.CodeCommentColor = cfg.GetString("markdown:codeCommentColor") ?? result.Markdown.CodeCommentColor;
                result.Markdown.CodeNumberColor = cfg.GetString("markdown:codeNumberColor") ?? result.Markdown.CodeNumberColor;
                result.Markdown.CodeDefaultColor = cfg.GetString("markdown:codeDefaultColor") ?? result.Markdown.CodeDefaultColor;
                result.Markdown.BlockquoteBorderColor = cfg.GetString("markdown:blockquoteBorderColor") ?? result.Markdown.BlockquoteBorderColor;
                result.Markdown.TableBorderColor = cfg.GetString("markdown:tableBorderColor") ?? result.Markdown.TableBorderColor;
                result.Markdown.TableHeaderBackground = cfg.GetString("markdown:tableHeaderBackground") ?? result.Markdown.TableHeaderBackground;
                result.Markdown.Heading1Size = cfg.GetInt("markdown:heading1Size") ?? result.Markdown.Heading1Size;
                result.Markdown.Heading2Size = cfg.GetInt("markdown:heading2Size") ?? result.Markdown.Heading2Size;
                result.Markdown.CodeFontFamily = cfg.GetString("markdown:codeFontFamily") ?? result.Markdown.CodeFontFamily;
                result.Markdown.BodyFontFamily = cfg.GetString("markdown:bodyFontFamily") ?? result.Markdown.BodyFontFamily;
                result.Markdown.LinkColor = cfg.GetString("markdown:linkColor") ?? result.Markdown.LinkColor;
                result.Markdown.TextColor = cfg.GetString("markdown:textColor") ?? result.Markdown.TextColor;
                result.Markdown.BackgroundColor = cfg.GetString("markdown:backgroundColor") ?? result.Markdown.BackgroundColor;
                result.Markdown.MathBackground = cfg.GetString("markdown:mathBackground") ?? result.Markdown.MathBackground;
                result.Markdown.SelectionColor = cfg.GetString("markdown:selectionColor") ?? result.Markdown.SelectionColor;
                result.Markdown.ImagePlaceholderColor = cfg.GetString("markdown:imagePlaceholderColor") ?? result.Markdown.ImagePlaceholderColor;
                result.Markdown.ZoomLevel = cfg.GetDouble("markdown:zoomLevel") ?? result.Markdown.ZoomLevel;

                result.ActivePresetName = cfg.GetString("stylePresets:active") ?? result.ActivePresetName;
                var presetCount = cfg.GetInt("stylePresets:count") ?? 0;
                for (var i = 0; i < presetCount; i++)
                {
                    var name = cfg.GetString($"stylePresets:{i}:name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var style = LoadMarkdownFromCfg(cfg, $"stylePresets:{i}:markdown");
                    result.StylePresets.Add(new MarkdownStylePreset { Name = name, Style = style });
                }
                if (result.StylePresets.Count == 0)
                {
                    result.StylePresets.Add(new MarkdownStylePreset { Name = "深色", Style = CloneMarkdownStyle(result.Markdown) });
                    result.StylePresets.Add(new MarkdownStylePreset { Name = "浅色", Style = CreateLightMarkdownStyle() });
                    result.ActivePresetName = "深色";
                }
            }
            catch
            {
                // 若旧配置损坏，则回退到内置默认配置并在稍后覆盖写回
                createdNew = true;
                result = new AppConfig();
            }
        }
        if (result.StylePresets.Count == 0)
        {
            result.StylePresets.Add(new MarkdownStylePreset { Name = "深色", Style = CloneMarkdownStyle(result.Markdown) });
            result.StylePresets.Add(new MarkdownStylePreset { Name = "浅色", Style = CreateLightMarkdownStyle() });
            result.ActivePresetName = "深色";
        }

        ApplyPresetToMarkdown(result);

        // 若本次是首次创建或自动修复配置，则把内存中的默认/修复后的配置写回磁盘，
        // 这样即使只有 exe 一个文件，第一次启动后也会在同目录生成一套默认 config.json。
        if (createdNew)
        {
            try
            {
                result.Save(path);
            }
            catch
            {
                // 忽略首次写入失败，避免影响应用启动
            }
        }

        return result;
    }

    private static void ApplyPresetToMarkdown(AppConfig config)
    {
        var preset = config.StylePresets.FirstOrDefault(p => string.Equals(p.Name, config.ActivePresetName, StringComparison.OrdinalIgnoreCase));
        if (preset != null)
            CopyMarkdownStyle(preset.Style, config.Markdown);
    }

    private static MarkdownStyleConfig CreateDarkMarkdownStyle()
    {
        return new MarkdownStyleConfig
        {
            CodeBlockBackground = "#252526",
            CodeBlockTextColor = "#dcdcdc",
            CodeKeywordColor = "#569cd6",
            CodeStringColor = "#ce9178",
            CodeCommentColor = "#6a9955",
            CodeNumberColor = "#b5cea8",
            CodeDefaultColor = "#d4d4d4",
            BlockquoteBorderColor = "#3f3f46",
            TableBorderColor = "#3f3f46",
            TableHeaderBackground = "#2a2d2e",
            Heading1Size = 28,
            Heading2Size = 24,
            CodeFontFamily = "Cascadia Code,Consolas,Microsoft YaHei Mono,monospace",
            BodyFontFamily = "Microsoft YaHei UI,Segoe UI,PingFang SC,sans-serif",
            LinkColor = "#3794ff",
            TextColor = "#d4d4d4",
            BackgroundColor = "#1e1e1e",
            MathBackground = "#252526",
            SelectionColor = "#503399ff",
            ImagePlaceholderColor = "#404040",
            ZoomLevel = 1.0
        };
    }

    private static MarkdownStyleConfig CreateLightMarkdownStyle()
    {
        return new MarkdownStyleConfig
        {
            CodeBlockBackground = "#f6f8fa",
            CodeBlockTextColor = "#24292f",
            CodeKeywordColor = "#0550ae",
            CodeStringColor = "#0a3069",
            CodeCommentColor = "#6e7781",
            CodeNumberColor = "#0550ae",
            CodeDefaultColor = "#24292f",
            BlockquoteBorderColor = "#d0d7de",
            TableBorderColor = "#d0d7de",
            TableHeaderBackground = "#eaeef2",
            Heading1Size = 28,
            Heading2Size = 24,
            CodeFontFamily = "Cascadia Code,Consolas,Microsoft YaHei Mono,monospace",
            BodyFontFamily = "Microsoft YaHei UI,Segoe UI,PingFang SC,sans-serif",
            LinkColor = "#0566d2",
            TextColor = "#24292f",
            BackgroundColor = "#ffffff",
            MathBackground = "#f6f8fa",
            SelectionColor = "#b6d7ff",
            ImagePlaceholderColor = "#e1e4e8",
            ZoomLevel = 1.0
        };
    }

    private static MarkdownStyleConfig LoadMarkdownFromCfg(LumConfigManager cfg, string prefix)
    {
        var m = new MarkdownStyleConfig();
        m.CodeBlockBackground = cfg.GetString($"{prefix}:codeBlockBackground") ?? m.CodeBlockBackground;
        m.CodeBlockTextColor = cfg.GetString($"{prefix}:codeBlockTextColor") ?? m.CodeBlockTextColor;
        m.CodeKeywordColor = cfg.GetString($"{prefix}:codeKeywordColor") ?? m.CodeKeywordColor;
        m.CodeStringColor = cfg.GetString($"{prefix}:codeStringColor") ?? m.CodeStringColor;
        m.CodeCommentColor = cfg.GetString($"{prefix}:codeCommentColor") ?? m.CodeCommentColor;
        m.CodeNumberColor = cfg.GetString($"{prefix}:codeNumberColor") ?? m.CodeNumberColor;
        m.CodeDefaultColor = cfg.GetString($"{prefix}:codeDefaultColor") ?? m.CodeDefaultColor;
        m.BlockquoteBorderColor = cfg.GetString($"{prefix}:blockquoteBorderColor") ?? m.BlockquoteBorderColor;
        m.TableBorderColor = cfg.GetString($"{prefix}:tableBorderColor") ?? m.TableBorderColor;
        m.TableHeaderBackground = cfg.GetString($"{prefix}:tableHeaderBackground") ?? m.TableHeaderBackground;
        m.Heading1Size = cfg.GetInt($"{prefix}:heading1Size") ?? m.Heading1Size;
        m.Heading2Size = cfg.GetInt($"{prefix}:heading2Size") ?? m.Heading2Size;
        m.CodeFontFamily = cfg.GetString($"{prefix}:codeFontFamily") ?? m.CodeFontFamily;
        m.BodyFontFamily = cfg.GetString($"{prefix}:bodyFontFamily") ?? m.BodyFontFamily;
        m.LinkColor = cfg.GetString($"{prefix}:linkColor") ?? m.LinkColor;
        m.TextColor = cfg.GetString($"{prefix}:textColor") ?? m.TextColor;
        m.BackgroundColor = cfg.GetString($"{prefix}:backgroundColor") ?? m.BackgroundColor;
        m.MathBackground = cfg.GetString($"{prefix}:mathBackground") ?? m.MathBackground;
        m.SelectionColor = cfg.GetString($"{prefix}:selectionColor") ?? m.SelectionColor;
        m.ImagePlaceholderColor = cfg.GetString($"{prefix}:imagePlaceholderColor") ?? m.ImagePlaceholderColor;
        m.ZoomLevel = cfg.GetDouble($"{prefix}:zoomLevel") ?? m.ZoomLevel;
        return m;
    }

    private static MarkdownStyleConfig CloneMarkdownStyle(MarkdownStyleConfig from)
    {
        var to = new MarkdownStyleConfig();
        CopyMarkdownStyle(from, to);
        return to;
    }

    private static void CopyMarkdownStyle(MarkdownStyleConfig from, MarkdownStyleConfig to)
    {
        to.CodeBlockBackground = from.CodeBlockBackground;
        to.CodeBlockTextColor = from.CodeBlockTextColor;
        to.CodeKeywordColor = from.CodeKeywordColor;
        to.CodeStringColor = from.CodeStringColor;
        to.CodeCommentColor = from.CodeCommentColor;
        to.CodeNumberColor = from.CodeNumberColor;
        to.CodeDefaultColor = from.CodeDefaultColor;
        to.BlockquoteBorderColor = from.BlockquoteBorderColor;
        to.TableBorderColor = from.TableBorderColor;
        to.TableHeaderBackground = from.TableHeaderBackground;
        to.Heading1Size = from.Heading1Size;
        to.Heading2Size = from.Heading2Size;
        to.CodeFontFamily = from.CodeFontFamily;
        to.BodyFontFamily = from.BodyFontFamily;
        to.LinkColor = from.LinkColor;
        to.TextColor = from.TextColor;
        to.BackgroundColor = from.BackgroundColor;
        to.MathBackground = from.MathBackground;
        to.SelectionColor = from.SelectionColor;
        to.ImagePlaceholderColor = from.ImagePlaceholderColor;
        to.ZoomLevel = from.ZoomLevel;
    }

    private static void SaveMarkdownToCfg(LumConfigManager cfg, string prefix, MarkdownStyleConfig m)
    {
        cfg.Set($"{prefix}:codeBlockBackground", m.CodeBlockBackground);
        cfg.Set($"{prefix}:codeBlockTextColor", m.CodeBlockTextColor);
        cfg.Set($"{prefix}:codeKeywordColor", m.CodeKeywordColor);
        cfg.Set($"{prefix}:codeStringColor", m.CodeStringColor);
        cfg.Set($"{prefix}:codeCommentColor", m.CodeCommentColor);
        cfg.Set($"{prefix}:codeNumberColor", m.CodeNumberColor);
        cfg.Set($"{prefix}:codeDefaultColor", m.CodeDefaultColor);
        cfg.Set($"{prefix}:blockquoteBorderColor", m.BlockquoteBorderColor);
        cfg.Set($"{prefix}:tableBorderColor", m.TableBorderColor);
        cfg.Set($"{prefix}:tableHeaderBackground", m.TableHeaderBackground);
        cfg.Set($"{prefix}:heading1Size", m.Heading1Size);
        cfg.Set($"{prefix}:heading2Size", m.Heading2Size);
        cfg.Set($"{prefix}:codeFontFamily", m.CodeFontFamily);
        cfg.Set($"{prefix}:bodyFontFamily", m.BodyFontFamily);
        cfg.Set($"{prefix}:linkColor", m.LinkColor);
        cfg.Set($"{prefix}:textColor", m.TextColor);
        cfg.Set($"{prefix}:backgroundColor", m.BackgroundColor);
        cfg.Set($"{prefix}:mathBackground", m.MathBackground);
        cfg.Set($"{prefix}:selectionColor", m.SelectionColor);
        cfg.Set($"{prefix}:imagePlaceholderColor", m.ImagePlaceholderColor);
        cfg.Set($"{prefix}:zoomLevel", m.ZoomLevel);
    }

    private static void LoadUiStyleFromCfg(LumConfigManager cfg, string prefix, UiStyleColors style)
    {
        style.WindowBackground = cfg.GetString($"{prefix}:windowBackground") ?? style.WindowBackground;
        style.TitleBarBackground = cfg.GetString($"{prefix}:titleBarBackground") ?? style.TitleBarBackground;
        style.TitleBarForeground = cfg.GetString($"{prefix}:titleBarForeground") ?? style.TitleBarForeground;
        style.SidebarBackground = cfg.GetString($"{prefix}:sidebarBackground") ?? style.SidebarBackground;
        style.SidebarBorderBrush = cfg.GetString($"{prefix}:sidebarBorderBrush") ?? style.SidebarBorderBrush;
        style.SidebarHeaderBackground = cfg.GetString($"{prefix}:sidebarHeaderBackground") ?? style.SidebarHeaderBackground;
        style.SidebarTextForeground = cfg.GetString($"{prefix}:sidebarTextForeground") ?? style.SidebarTextForeground;
        style.ExplorerHeaderBackground = cfg.GetString($"{prefix}:explorerHeaderBackground") ?? style.ExplorerHeaderBackground;
        style.ExplorerHeaderForeground = cfg.GetString($"{prefix}:explorerHeaderForeground") ?? style.ExplorerHeaderForeground;
        style.WelcomePageBackground = cfg.GetString($"{prefix}:welcomePageBackground") ?? style.WelcomePageBackground;
        style.WelcomePageTextForeground = cfg.GetString($"{prefix}:welcomePageTextForeground") ?? style.WelcomePageTextForeground;
        style.WelcomePageLinkForeground = cfg.GetString($"{prefix}:welcomePageLinkForeground") ?? style.WelcomePageLinkForeground;
        style.TabBarBackground = cfg.GetString($"{prefix}:tabBarBackground") ?? style.TabBarBackground;
        style.TabBarBorderBrush = cfg.GetString($"{prefix}:tabBarBorderBrush") ?? style.TabBarBorderBrush;
        style.ContentBackground = cfg.GetString($"{prefix}:contentBackground") ?? style.ContentBackground;
        style.EditorForeground = cfg.GetString($"{prefix}:editorForeground") ?? style.EditorForeground;
        style.StatusBarBackground = cfg.GetString($"{prefix}:statusBarBackground") ?? style.StatusBarBackground;
        style.StatusBarForeground = cfg.GetString($"{prefix}:statusBarForeground") ?? style.StatusBarForeground;
        style.FileChangedBarBackground = cfg.GetString($"{prefix}:fileChangedBarBackground") ?? style.FileChangedBarBackground;
        style.FileChangedBarForeground = cfg.GetString($"{prefix}:fileChangedBarForeground") ?? style.FileChangedBarForeground;
        style.ButtonForeground = cfg.GetString($"{prefix}:buttonForeground") ?? style.ButtonForeground;
        style.ButtonHoverBackground = cfg.GetString($"{prefix}:buttonHoverBackground") ?? style.ButtonHoverBackground;
        style.ButtonPressedBackground = cfg.GetString($"{prefix}:buttonPressedBackground") ?? style.ButtonPressedBackground;
        style.TabItemForeground = cfg.GetString($"{prefix}:tabItemForeground") ?? style.TabItemForeground;
        style.TabItemSelectedForeground = cfg.GetString($"{prefix}:tabItemSelectedForeground") ?? style.TabItemSelectedForeground;
        style.TabItemHoverBackground = cfg.GetString($"{prefix}:tabItemHoverBackground") ?? style.TabItemHoverBackground;
        style.TabItemSelectedBackground = cfg.GetString($"{prefix}:tabItemSelectedBackground") ?? style.TabItemSelectedBackground;
        style.InputBackground = cfg.GetString($"{prefix}:inputBackground") ?? style.InputBackground;
        style.InputForeground = cfg.GetString($"{prefix}:inputForeground") ?? style.InputForeground;
        style.InputBorderBrush = cfg.GetString($"{prefix}:inputBorderBrush") ?? style.InputBorderBrush;
        style.SplitterBorderBrush = cfg.GetString($"{prefix}:splitterBorderBrush") ?? style.SplitterBorderBrush;
        style.ListBoxItemSelectedBackground = cfg.GetString($"{prefix}:listBoxItemSelectedBackground") ?? style.ListBoxItemSelectedBackground;
        style.ListBoxItemSelectedForeground = cfg.GetString($"{prefix}:listBoxItemSelectedForeground") ?? style.ListBoxItemSelectedForeground;
    }

    private static void SaveUiStyleToCfg(LumConfigManager cfg, string prefix, UiStyleColors style)
    {
        cfg.Set($"{prefix}:windowBackground", style.WindowBackground);
        cfg.Set($"{prefix}:titleBarBackground", style.TitleBarBackground);
        cfg.Set($"{prefix}:titleBarForeground", style.TitleBarForeground);
        cfg.Set($"{prefix}:sidebarBackground", style.SidebarBackground);
        cfg.Set($"{prefix}:sidebarBorderBrush", style.SidebarBorderBrush);
        cfg.Set($"{prefix}:sidebarHeaderBackground", style.SidebarHeaderBackground);
        cfg.Set($"{prefix}:sidebarTextForeground", style.SidebarTextForeground);
        cfg.Set($"{prefix}:explorerHeaderBackground", style.ExplorerHeaderBackground);
        cfg.Set($"{prefix}:explorerHeaderForeground", style.ExplorerHeaderForeground);
        cfg.Set($"{prefix}:welcomePageBackground", style.WelcomePageBackground);
        cfg.Set($"{prefix}:welcomePageTextForeground", style.WelcomePageTextForeground);
        cfg.Set($"{prefix}:welcomePageLinkForeground", style.WelcomePageLinkForeground);
        cfg.Set($"{prefix}:tabBarBackground", style.TabBarBackground);
        cfg.Set($"{prefix}:tabBarBorderBrush", style.TabBarBorderBrush);
        cfg.Set($"{prefix}:contentBackground", style.ContentBackground);
        cfg.Set($"{prefix}:editorForeground", style.EditorForeground);
        cfg.Set($"{prefix}:statusBarBackground", style.StatusBarBackground);
        cfg.Set($"{prefix}:statusBarForeground", style.StatusBarForeground);
        cfg.Set($"{prefix}:fileChangedBarBackground", style.FileChangedBarBackground);
        cfg.Set($"{prefix}:fileChangedBarForeground", style.FileChangedBarForeground);
        cfg.Set($"{prefix}:buttonForeground", style.ButtonForeground);
        cfg.Set($"{prefix}:buttonHoverBackground", style.ButtonHoverBackground);
        cfg.Set($"{prefix}:buttonPressedBackground", style.ButtonPressedBackground);
        cfg.Set($"{prefix}:tabItemForeground", style.TabItemForeground);
        cfg.Set($"{prefix}:tabItemSelectedForeground", style.TabItemSelectedForeground);
        cfg.Set($"{prefix}:tabItemHoverBackground", style.TabItemHoverBackground);
        cfg.Set($"{prefix}:tabItemSelectedBackground", style.TabItemSelectedBackground);
        cfg.Set($"{prefix}:inputBackground", style.InputBackground);
        cfg.Set($"{prefix}:inputForeground", style.InputForeground);
        cfg.Set($"{prefix}:inputBorderBrush", style.InputBorderBrush);
        cfg.Set($"{prefix}:splitterBorderBrush", style.SplitterBorderBrush);
        cfg.Set($"{prefix}:listBoxItemSelectedBackground", style.ListBoxItemSelectedBackground);
        cfg.Set($"{prefix}:listBoxItemSelectedForeground", style.ListBoxItemSelectedForeground);
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var cfg = new LumConfigManager();

            // Ui
            cfg.Set("ui:documentListWidth", Ui.DocumentListWidth);
            cfg.Set("ui:sidebarCollapsed", Ui.SidebarCollapsed);
            cfg.Set("ui:editorWidth", Ui.EditorWidth);
            // 不再持久化窗口尺寸和状态，统一交由系统窗口管理负责
            cfg.Set("ui:layoutMode", Ui.LayoutMode);
            cfg.Set("ui:autoSaveIntervalSeconds", Ui.AutoSaveIntervalSeconds);
            cfg.Set("ui:theme", Ui.Theme);
            cfg.Set("ui:backgroundColor", Ui.BackgroundColor);
            cfg.Set("ui:sidebarBackground", Ui.SidebarBackground);
            cfg.Set("ui:headerBackground", Ui.HeaderBackground);
            cfg.Set("ui:linkColor", Ui.LinkColor);
            SaveUiStyleToCfg(cfg, "ui:style:dark", Ui.DarkStyle);
            SaveUiStyleToCfg(cfg, "ui:style:light", Ui.LightStyle);

            // Markdown
            cfg.Set("markdown:codeBlockBackground", Markdown.CodeBlockBackground);
            cfg.Set("markdown:codeBlockTextColor", Markdown.CodeBlockTextColor);
            cfg.Set("markdown:codeKeywordColor", Markdown.CodeKeywordColor);
            cfg.Set("markdown:codeStringColor", Markdown.CodeStringColor);
            cfg.Set("markdown:codeCommentColor", Markdown.CodeCommentColor);
            cfg.Set("markdown:codeNumberColor", Markdown.CodeNumberColor);
            cfg.Set("markdown:codeDefaultColor", Markdown.CodeDefaultColor);
            cfg.Set("markdown:blockquoteBorderColor", Markdown.BlockquoteBorderColor);
            cfg.Set("markdown:tableBorderColor", Markdown.TableBorderColor);
            cfg.Set("markdown:tableHeaderBackground", Markdown.TableHeaderBackground);
            cfg.Set("markdown:heading1Size", Markdown.Heading1Size);
            cfg.Set("markdown:heading2Size", Markdown.Heading2Size);
            cfg.Set("markdown:codeFontFamily", Markdown.CodeFontFamily);
            cfg.Set("markdown:bodyFontFamily", Markdown.BodyFontFamily);
            cfg.Set("markdown:linkColor", Markdown.LinkColor);
            cfg.Set("markdown:textColor", Markdown.TextColor);
            cfg.Set("markdown:backgroundColor", Markdown.BackgroundColor);
            cfg.Set("markdown:mathBackground", Markdown.MathBackground);
            cfg.Set("markdown:selectionColor", Markdown.SelectionColor);
            cfg.Set("markdown:imagePlaceholderColor", Markdown.ImagePlaceholderColor);
            cfg.Set("markdown:zoomLevel", Markdown.ZoomLevel);

            cfg.Set("stylePresets:active", ActivePresetName);
            cfg.Set("stylePresets:count", StylePresets.Count);
            for (var i = 0; i < StylePresets.Count; i++)
            {
                var p = StylePresets[i];
                cfg.Set($"stylePresets:{i}:name", p.Name);
                SaveMarkdownToCfg(cfg, $"stylePresets:{i}:markdown", p.Style);
            }

            cfg.Save(path);
        }
        catch { }
    }

    private static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>配置文件路径：与可执行文件同目录，方便便携分发。</summary>
    public static string DefaultConfigPath => Path.Combine(BaseDirectory, "config.json");

    /// <summary>最近打开文件列表持久化路径（与 config 同目录）。</summary>
    public static string RecentFilesPath => Path.Combine(BaseDirectory, "recent-files.json");

    /// <summary>最近打开文件夹列表持久化路径。</summary>
    public static string RecentFoldersPath => Path.Combine(BaseDirectory, "recent-folders.json");

    /// <summary>应用指定名称的预设到 Markdown，并设为当前预设。替换 Markdown 引用以便绑定刷新预览。</summary>
    public bool ApplyPreset(string presetName)
    {
        var preset = StylePresets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (preset == null) return false;
        ActivePresetName = preset.Name;
        Markdown = CloneMarkdownStyle(preset.Style);
        return true;
    }

    /// <summary>将当前 Markdown 写回当前预设（保存当前修改到该预设）。</summary>
    public void SaveCurrentToActivePreset()
    {
        var preset = StylePresets.FirstOrDefault(p => string.Equals(p.Name, ActivePresetName, StringComparison.OrdinalIgnoreCase));
        if (preset != null)
            CopyMarkdownStyle(Markdown, preset.Style);
    }
}

/// <summary>界面样式颜色（一套完整主题色）。样式文件即与 exe 同目录的 config.json：深色键 ui:style:dark:windowBackground 等，浅色键 ui:style:light:windowBackground 等，保存配置时会一并写入。</summary>
public sealed class UiStyleColors
{
    public string WindowBackground { get; set; } = "#1e1e1e";
    public string TitleBarBackground { get; set; } = "#252526";
    public string TitleBarForeground { get; set; } = "#f3f3f3";
    public string SidebarBackground { get; set; } = "#252526";
    public string SidebarBorderBrush { get; set; } = "#3f3f46";
    public string SidebarHeaderBackground { get; set; } = "#333333";
    public string SidebarTextForeground { get; set; } = "#cccccc";
    public string ExplorerHeaderBackground { get; set; } = "#2d2d30";
    public string ExplorerHeaderForeground { get; set; } = "#cccccc";
    public string WelcomePageBackground { get; set; } = "#1e1e1e";
    public string WelcomePageTextForeground { get; set; } = "#cccccc";
    public string WelcomePageLinkForeground { get; set; } = "#3794ff";
    public string TabBarBackground { get; set; } = "#252526";
    public string TabBarBorderBrush { get; set; } = "#3f3f46";
    public string ContentBackground { get; set; } = "#1e1e1e";
    public string EditorForeground { get; set; } = "#d4d4d4";
    public string StatusBarBackground { get; set; } = "#007acc";
    public string StatusBarForeground { get; set; } = "White";
    public string FileChangedBarBackground { get; set; } = "#3e3e3e";
    public string FileChangedBarForeground { get; set; } = "#d4d4d4";
    public string ButtonForeground { get; set; } = "#f3f3f3";
    public string ButtonHoverBackground { get; set; } = "#3e3e3e";
    public string ButtonPressedBackground { get; set; } = "#2d2d2d";
    public string TabItemForeground { get; set; } = "#cccccc";
    public string TabItemSelectedForeground { get; set; } = "#ffffff";
    public string TabItemHoverBackground { get; set; } = "#3a3d41";
    public string TabItemSelectedBackground { get; set; } = "#333333";
    public string InputBackground { get; set; } = "#3c3c3c";
    public string InputForeground { get; set; } = "#f3f3f3";
    public string InputBorderBrush { get; set; } = "#3f3f46";
    public string SplitterBorderBrush { get; set; } = "#3f3f46";
    public string ListBoxItemSelectedBackground { get; set; } = "#094771";
    public string ListBoxItemSelectedForeground { get; set; } = "White";
}

public sealed class UiConfig
{
    public double DocumentListWidth { get; set; } = 280;
    /// <summary>左侧文件栏是否已折叠为侧边窄条，点击可再展开。</summary>
    public bool SidebarCollapsed { get; set; } = false;
    public double EditorWidth { get; set; } = 1;
    public string LayoutMode { get; set; } = "Both";
    /// <summary>自动保存间隔（秒）：0=禁用，60/300/600/1200 等。</summary>
    public int AutoSaveIntervalSeconds { get; set; } = 0;
    /// <summary>界面主题：Dark（深色）/ Light（浅色）。</summary>
    public string Theme { get; set; } = "Dark";
    public string BackgroundColor { get; set; } = "#f6f8fa";
    public string SidebarBackground { get; set; } = "#ffffff";
    public string HeaderBackground { get; set; } = "#24292f";
    public string LinkColor { get; set; } = "#0566d2";

    /// <summary>深色主题下的界面样式颜色，可配置到样式文件。</summary>
    public UiStyleColors DarkStyle { get; set; } = CreateDefaultDarkStyle();
    /// <summary>浅色主题下的界面样式颜色，可配置到样式文件。</summary>
    public UiStyleColors LightStyle { get; set; } = CreateDefaultLightStyle();

    private static UiStyleColors CreateDefaultDarkStyle()
    {
        return new UiStyleColors
        {
            WindowBackground = "#1e1e1e",
            TitleBarBackground = "#252526",
            TitleBarForeground = "#f3f3f3",
            SidebarBackground = "#252526",
            SidebarBorderBrush = "#3f3f46",
            SidebarHeaderBackground = "#333333",
            SidebarTextForeground = "#cccccc",
            ExplorerHeaderBackground = "#2d2d30",
            ExplorerHeaderForeground = "#cccccc",
            WelcomePageBackground = "#1e1e1e",
            WelcomePageTextForeground = "#cccccc",
            WelcomePageLinkForeground = "#3794ff",
            TabBarBackground = "#252526",
            TabBarBorderBrush = "#3f3f46",
            ContentBackground = "#1e1e1e",
            EditorForeground = "#d4d4d4",
            StatusBarBackground = "#007acc",
            StatusBarForeground = "White",
            FileChangedBarBackground = "#3e3e3e",
            FileChangedBarForeground = "#d4d4d4",
            ButtonForeground = "#f3f3f3",
            ButtonHoverBackground = "#3e3e3e",
            ButtonPressedBackground = "#2d2d2d",
            TabItemForeground = "#cccccc",
            TabItemSelectedForeground = "#ffffff",
            TabItemHoverBackground = "#3a3d41",
            TabItemSelectedBackground = "#333333",
            InputBackground = "#3c3c3c",
            InputForeground = "#f3f3f3",
            InputBorderBrush = "#3f3f46",
            SplitterBorderBrush = "#3f3f46",
            ListBoxItemSelectedBackground = "#094771",
            ListBoxItemSelectedForeground = "White",
        };
    }

    private static UiStyleColors CreateDefaultLightStyle()
    {
        return new UiStyleColors
        {
            WindowBackground = "#ffffff",
            TitleBarBackground = "#ffffff",
            TitleBarForeground = "#24292f",
            SidebarBackground = "#eaeef2",
            SidebarBorderBrush = "#d0d7de",
            SidebarHeaderBackground = "#e1e4e8",
            SidebarTextForeground = "#24292f",
            ExplorerHeaderBackground = "#e1e4e8",
            ExplorerHeaderForeground = "#24292f",
            WelcomePageBackground = "#ffffff",
            WelcomePageTextForeground = "#24292f",
            WelcomePageLinkForeground = "#0566d2",
            TabBarBackground = "#ffffff",
            TabBarBorderBrush = "#d0d7de",
            ContentBackground = "#ffffff",
            EditorForeground = "#24292f",
            StatusBarBackground = "#eaeef2",
            StatusBarForeground = "#24292f",
            FileChangedBarBackground = "#fff8e6",
            FileChangedBarForeground = "#24292f",
            ButtonForeground = "#24292f",
            ButtonHoverBackground = "#eaeef2",
            ButtonPressedBackground = "#d0d7de",
            TabItemForeground = "#57606a",
            TabItemSelectedForeground = "#24292f",
            TabItemHoverBackground = "#eaeef2",
            TabItemSelectedBackground = "#ffffff",
            InputBackground = "#ffffff",
            InputForeground = "#24292f",
            InputBorderBrush = "#d0d7de",
            SplitterBorderBrush = "#d0d7de",
            ListBoxItemSelectedBackground = "#b6d7ff",
            ListBoxItemSelectedForeground = "#24292f",
        };
    }
}

public sealed class MarkdownStyleConfig
{
    public string CodeBlockBackground { get; set; } = "#252526";
    public string CodeBlockTextColor { get; set; } = "#dcdcdc";
    public string CodeKeywordColor { get; set; } = "#569cd6";
    public string CodeStringColor { get; set; } = "#ce9178";
    public string CodeCommentColor { get; set; } = "#6a9955";
    public string CodeNumberColor { get; set; } = "#b5cea8";
    public string CodeDefaultColor { get; set; } = "#d4d4d4";
    public string BlockquoteBorderColor { get; set; } = "#3f3f46";
    public string TableBorderColor { get; set; } = "#3f3f46";
    public string TableHeaderBackground { get; set; } = "#2a2d2e";
    public int Heading1Size { get; set; } = 28;
    public int Heading2Size { get; set; } = 24;
    public string CodeFontFamily { get; set; } = "Cascadia Code,Consolas,Microsoft YaHei Mono,monospace";
    public string BodyFontFamily { get; set; } = "Microsoft YaHei UI,Segoe UI,PingFang SC,sans-serif";
    public string LinkColor { get; set; } = "#3794ff";
    public string TextColor { get; set; } = "#d4d4d4";
    public string BackgroundColor { get; set; } = "#1e1e1e";
    public string MathBackground { get; set; } = "#252526";
    public string SelectionColor { get; set; } = "#503399ff";
    public string ImagePlaceholderColor { get; set; } = "#404040";
    public double ZoomLevel { get; set; } = 1.0;
}

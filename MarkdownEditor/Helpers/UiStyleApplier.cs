using Avalonia.Controls;
using Avalonia.Media;
using MarkdownEditor.Core;

namespace MarkdownEditor.Helpers;

/// <summary>将配置中的界面样式颜色注入到应用资源，供 DynamicResource 使用。</summary>
public static class UiStyleApplier
{
    /// <summary>将 <paramref name="style"/> 写入 <paramref name="resources"/>，键与 XAML 中 DynamicResource 一致。</summary>
    public static void Apply(IResourceDictionary resources, UiStyleColors style)
    {
        if (resources == null || style == null) return;

        Set(resources, "WindowBackground", style.WindowBackground);
        Set(resources, "TitleBarBackground", style.TitleBarBackground);
        Set(resources, "TitleBarForeground", style.TitleBarForeground);
        Set(resources, "SidebarBackground", style.SidebarBackground);
        Set(resources, "SidebarBorderBrush", style.SidebarBorderBrush);
        Set(resources, "SidebarHeaderBackground", style.SidebarHeaderBackground);
        Set(resources, "SidebarTextForeground", style.SidebarTextForeground);
        Set(resources, "ExplorerHeaderBackground", style.ExplorerHeaderBackground);
        Set(resources, "ExplorerHeaderForeground", style.ExplorerHeaderForeground);
        Set(resources, "WelcomePageBackground", style.WelcomePageBackground);
        Set(resources, "WelcomePageTextForeground", style.WelcomePageTextForeground);
        Set(resources, "WelcomePageLinkForeground", style.WelcomePageLinkForeground);
        Set(resources, "TabBarBackground", style.TabBarBackground);
        Set(resources, "TabBarBorderBrush", style.TabBarBorderBrush);
        Set(resources, "ContentBackground", style.ContentBackground);
        Set(resources, "EditorForeground", style.EditorForeground);
        Set(resources, "StatusBarBackground", style.StatusBarBackground);
        Set(resources, "StatusBarForeground", style.StatusBarForeground);
        Set(resources, "FileChangedBarBackground", style.FileChangedBarBackground);
        Set(resources, "FileChangedBarForeground", style.FileChangedBarForeground);
        Set(resources, "ButtonForeground", style.ButtonForeground);
        Set(resources, "ButtonHoverBackground", style.ButtonHoverBackground);
        Set(resources, "ButtonPressedBackground", style.ButtonPressedBackground);
        Set(resources, "TabItemForeground", style.TabItemForeground);
        Set(resources, "TabItemSelectedForeground", style.TabItemSelectedForeground);
        Set(resources, "TabItemHoverBackground", style.TabItemHoverBackground);
        Set(resources, "TabItemSelectedBackground", style.TabItemSelectedBackground);
        Set(resources, "TabItemSelectedUnderlineBrush", style.TabItemSelectedUnderline);
        Set(resources, "InputBackground", style.InputBackground);
        Set(resources, "InputForeground", style.InputForeground);
        Set(resources, "InputBorderBrush", style.InputBorderBrush);
        Set(resources, "SplitterBorderBrush", style.SplitterBorderBrush);
        Set(resources, "ListBoxItemSelectedBackground", style.ListBoxItemSelectedBackground);
        Set(resources, "ListBoxItemSelectedForeground", style.ListBoxItemSelectedForeground);
        Set(resources, "DiffAddedBackground", style.DiffAddedBackground);
        Set(resources, "DiffRemovedBackground", style.DiffRemovedBackground);
    }

    private static void Set(IResourceDictionary resources, string key, string colorString)
    {
        var brush = ParseBrush(colorString);
        if (brush != null)
            resources[key] = brush;
    }

    private static SolidColorBrush? ParseBrush(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s!.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal))
        {
            var argb = ColorUtils.ParseHexColor(s);
            return new SolidColorBrush(Color.FromUInt32(argb));
        }
        if (KnownColors.TryGetValue(s, out var color))
            return new SolidColorBrush(color);
        return null;
    }

    private static readonly Dictionary<string, Color> KnownColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["White"] = Color.FromUInt32(0xFFFFFFFF),
        ["Black"] = Color.FromUInt32(0xFF000000),
        ["Transparent"] = Color.FromUInt32(0x00FFFFFF),
    };
}

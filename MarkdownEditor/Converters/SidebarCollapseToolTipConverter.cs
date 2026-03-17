using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MarkdownEditor.Converters;

/// <summary>侧栏折叠条 ToolTip：true（已折叠）→ 点击展开，false → 点击收起。</summary>
public sealed class SidebarCollapseToolTipConverter : IValueConverter
{
    public static readonly SidebarCollapseToolTipConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "点击展开" : "点击收起";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

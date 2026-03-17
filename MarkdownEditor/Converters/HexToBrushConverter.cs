using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MarkdownEditor.Converters;

/// <summary>将 #RRGGBB / #AARRGGBB 字符串转换为 SolidColorBrush，用于从配置驱动背景色。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return Avalonia.Media.Brushes.Transparent;
        try
        {
            var color = Color.Parse(s);
            return new SolidColorBrush(color);
        }
        catch
        {
            return Avalonia.Media.Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}


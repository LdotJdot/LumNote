using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MarkdownEditor.Converters;

/// <summary>对象非 null 且字符串非空时为 true，用于控制 IsVisible。</summary>
public sealed class IsNotNullConverter : IValueConverter
{
    public static readonly IsNotNullConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? !string.IsNullOrEmpty(s) : value != null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

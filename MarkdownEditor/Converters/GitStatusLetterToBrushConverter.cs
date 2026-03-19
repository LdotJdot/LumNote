using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MarkdownEditor.Converters;

/// <summary>Git 状态字母 → 前景画刷（M=琥珀 A=绿 D=红 ?=灰）。</summary>
public sealed class GitStatusLetterToBrushConverter : IValueConverter
{
    public static readonly GitStatusLetterToBrushConverter Instance = new();

    private static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.FromRgb(204, 160, 0));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(114, 156, 82));
    private static readonly IBrush DeletedBrush = new SolidColorBrush(Color.FromRgb(189, 83, 74));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.FromRgb(128, 128, 128));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        return s switch
        {
            "M" => ModifiedBrush,
            "A" => AddedBrush,
            "D" => DeletedBrush,
            "?" => DefaultBrush,
            _ => DefaultBrush
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

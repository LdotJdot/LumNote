using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace MarkdownEditor.Converters;

/// <summary>将完整路径转换为最后一段（文件名或目录名），用于欢迎页两行显示的第一行。</summary>
public sealed class PathToFileNameConverter : IValueConverter
{
    public static readonly PathToFileNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return "";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

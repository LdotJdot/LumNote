using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace MarkdownEditor.Converters;

/// <summary>将本地图片文件路径转换为 IBitmap，用于右侧图片预览。路径为空或文件不存在时返回 null。内部只保留当前一张图并释放前一张，避免切换预览时内存持续上升。</summary>
public sealed class PathToImageConverter : IValueConverter
{
    public static readonly PathToImageConverter Instance = new();

    private string? _lastPath;
    private Bitmap? _lastBitmap;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            DisposeLast();
            return null;
        }
        if (path == _lastPath && _lastBitmap != null)
            return _lastBitmap;
        if (!System.IO.File.Exists(path))
        {
            DisposeLast();
            return null;
        }
        try
        {
            DisposeLast();
            _lastPath = path;
            _lastBitmap = new Bitmap(path);
            return _lastBitmap;
        }
        catch
        {
            DisposeLast();
            return null;
        }
    }

    private void DisposeLast()
    {
        if (_lastBitmap != null)
        {
            try { _lastBitmap.Dispose(); } catch { }
            _lastBitmap = null;
        }
        _lastPath = null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

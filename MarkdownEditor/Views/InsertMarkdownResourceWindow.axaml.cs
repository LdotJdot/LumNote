using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MarkdownEditor.Views;

public partial class InsertMarkdownResourceWindow : Window
{
    private bool _isImage;
    private string? _currentDocumentPath;
    private IStorageProvider _storage = null!;

    public string? InsertedMarkdown { get; private set; }

    public InsertMarkdownResourceWindow()
    {
        InitializeComponent();
    }

    private void Apply(bool isImage, string? currentDocumentPath, IStorageProvider storage)
    {
        _isImage = isImage;
        _currentDocumentPath = currentDocumentPath;
        _storage = storage;
        Title = isImage ? "插入图片" : "插入链接";
        BrowseButton.Content = isImage ? "浏览图片…" : "浏览文件…";
        PathTextBox.Text = "";
        InsertedMarkdown = null;

        BrowseButton.Click -= BrowseButton_OnClick;
        BrowseButton.Click += BrowseButton_OnClick;
        OkButton.Click -= OkButton_OnClick;
        OkButton.Click += OkButton_OnClick;
        CancelButton.Click -= CancelButton_OnClick;
        CancelButton.Click += CancelButton_OnClick;
    }

    private async void BrowseButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var options = new FilePickerOpenOptions
        {
            Title = _isImage ? "选择图片" : "选择文件",
            AllowMultiple = false,
            FileTypeFilter = _isImage
                ?
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.svg"]
                    },
                    new FilePickerFileType("所有文件") { Patterns = ["*"] }
                ]
                : [new FilePickerFileType("所有文件") { Patterns = ["*"] }]
        };
        var files = await _storage.OpenFilePickerAsync(options);
        if (files.Count == 0) return;
        var local = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(local)) return;
        PathTextBox.Text = ToDisplayPath(_currentDocumentPath, local);
    }

    private void OkButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OnOk();

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>有文档磁盘路径时用相对路径，否则绝对路径；正斜杠。</summary>
    public static string ToDisplayPath(string? currentDocumentPath, string pickedPath)
    {
        pickedPath = Path.GetFullPath(pickedPath);
        if (string.IsNullOrWhiteSpace(currentDocumentPath))
            return pickedPath.Replace('\\', '/');
        try
        {
            var docFull = Path.GetFullPath(currentDocumentPath);
            var docDir = Path.GetDirectoryName(docFull);
            if (string.IsNullOrEmpty(docDir))
                return pickedPath.Replace('\\', '/');
            return Path.GetRelativePath(docDir, pickedPath).Replace('\\', '/');
        }
        catch
        {
            return pickedPath.Replace('\\', '/');
        }
    }

    /// <summary>本地路径含空格或括号时用尖括号；http(s) 原样。</summary>
    public static string EscapeMarkdownUrl(string pathOrUrl)
    {
        var s = pathOrUrl.Trim();
        if (string.IsNullOrEmpty(s)) return s;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        var normalized = s.Replace('\\', '/');
        if (normalized.Contains(' ') || normalized.Contains('(') || normalized.Contains(')'))
            return "<" + normalized + ">";
        return normalized;
    }

    private void OnOk()
    {
        var raw = PathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw))
        {
            InsertedMarkdown = null;
            Close();
            return;
        }
        var url = EscapeMarkdownUrl(raw);
        InsertedMarkdown = _isImage ? $"![图片]({url})" : $"[链接]({url})";
        Close();
    }

    public static async Task<string?> ShowInsertAsync(Window owner, bool isImage, string? currentDocumentPath, IStorageProvider storage)
    {
        var w = new InsertMarkdownResourceWindow();
        w.Apply(isImage, currentDocumentPath, storage);
        await w.ShowDialog(owner);
        return w.InsertedMarkdown;
    }
}

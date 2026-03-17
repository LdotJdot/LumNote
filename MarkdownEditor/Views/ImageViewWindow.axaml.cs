using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MarkdownEditor.Views;

/// <summary>仅显示图片的独立窗口，无编辑区。</summary>
public partial class ImageViewWindow : Window
{
    public ImageViewWindow()
    {
        InitializeComponent();
        Icon = MainWindow.GetAppIcon();
        Closed += (_, _) =>
        {
            if (ImageControl.Source is IDisposable d)
            {
                try { d.Dispose(); } catch { }
            }
            ImageControl.Source = null;
        };
    }

    /// <summary>设置要显示的本地图片路径并更新标题。会释放之前显示的 Bitmap 以控制内存。</summary>
    public void SetImagePath(string? path)
    {
        Title = string.IsNullOrEmpty(path) ? "图片" : Path.GetFileName(path);
        if (ImageControl.Source is IDisposable old)
        {
            try { old.Dispose(); } catch { }
        }
        ImageControl.Source = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            ImageControl.Source = new Bitmap(path);
        }
        catch
        {
            ImageControl.Source = null;
        }
    }

    /// <summary>设置父窗口并显示（Owner 为 protected，由本类内部设置）。</summary>
    public void ShowWithOwner(Window? owner)
    {
        if (owner != null)
            Owner = owner;
        Show();
    }
}

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Views;

/// <summary>仅展示渲染区内容的独立全屏窗口；主窗口保持不变。Esc 或右键关闭。</summary>
public partial class PreviewFullscreenWindow : Window
{
    private Visual? _screenAnchor;

    public PreviewFullscreenWindow()
    {
        InitializeComponent();
        Icon = MainWindow.GetAppIcon();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnWindowPointerReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>在包含 <paramref name="screenAnchor"/> 的显示器上全屏显示（便于拖到扩展屏后从该屏演示）。</summary>
    public void ShowFullscreen(Window owner, Visual screenAnchor, MainViewModel vm)
    {
        _screenAnchor = screenAnchor;
        DataContext = vm;
        Owner = owner;
        Opened += OnOpenedOnce;
        Show();
    }

    private void OnOpenedOnce(object? sender, EventArgs e)
    {
        Opened -= OnOpenedOnce;
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    ApplyTargetScreen();
                    Focus();
                }
                catch
                {
                    // 全屏失败时仍保留窗口，用户可手动调整
                }
            },
            DispatcherPriority.Loaded
        );
    }

    private void ApplyTargetScreen()
    {
        var anchor = _screenAnchor ?? Owner as Visual ?? this;
        PixelPoint center;
        try
        {
            var b = anchor.Bounds;
            center = b.Width > 0 && b.Height > 0
                ? anchor.PointToScreen(new Point(b.Width * 0.5, b.Height * 0.5))
                : this.PointToScreen(new Point(1, 1));
        }
        catch
        {
            center = this.PointToScreen(new Point(1, 1));
        }

        var screen = Screens.ScreenFromPoint(center) ?? Screens.Primary;
        if (screen == null)
            return;

        WindowState = WindowState.Normal;
        Position = screen.Bounds.Position;
        WindowState = WindowState.FullScreen;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            e.Handled = true;
            Close();
        }
    }
}

using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace MarkdownEditor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 在创建主窗口前加载（或创建）配置并设置主题：
        // 即使只有 exe 一个文件，首次启动也会自动在同目录生成默认 config.json。
        try
        {
            var path = Core.AppConfig.DefaultConfigPath;
            var config = Core.AppConfig.Load(path);
            RequestedThemeVariant = string.Equals(config.Ui.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
        catch
        {
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

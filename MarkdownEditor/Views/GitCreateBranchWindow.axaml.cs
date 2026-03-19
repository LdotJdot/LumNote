using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MarkdownEditor.Views;

public partial class GitCreateBranchWindow : Window
{
    public string? BranchName => BranchNameBox?.Text?.Trim();

    public GitCreateBranchWindow()
    {
        InitializeComponent();
        Icon = MainWindow.GetAppIcon();
        OkButton!.Click += OnOk;
        CancelButton!.Click += (_, _) => Close();
        BranchNameBox!.KeyDown += (s, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) OnOk(s, e);
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(BranchName))
            Close();
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;

namespace MarkdownEditor.Views;

public partial class TimelineViewWindow : Window
{
    public string? CommitSha { get; private set; }
    public string? FilePath { get; private set; }

    public TimelineViewWindow()
    {
        InitializeComponent();
        Icon = MainWindow.GetAppIcon();
    }

    public void SetContent(string? content, string? commitSha, string? filePath, string? messageShort)
    {
        CommitSha = commitSha;
        FilePath = filePath;
        ContentEditor!.Document = new TextDocument(content ?? "");
        TitleText!.Text = string.IsNullOrEmpty(messageShort) ? "历史版本内容" : $"历史版本：{messageShort}";
    }

    public void SetCompareWithCurrentAction(Action? action)
    {
        if (CompareWithCurrentButton != null)
            CompareWithCurrentButton.Click += (_, _) => action?.Invoke();
    }
}

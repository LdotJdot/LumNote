using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MarkdownEditor.Views;

public enum ConfirmCloseResult { Cancel, Save, Discard }

public partial class ConfirmCloseWindow : Window
{
    public ConfirmCloseResult Result { get; private set; } = ConfirmCloseResult.Cancel;

    public ConfirmCloseWindow()
    {
        InitializeComponent();
        Icon = MainWindow.GetAppIcon();
        SaveButton.Click += (_, _) => { Result = ConfirmCloseResult.Save; Close(); };
        DiscardButton.Click += (_, _) => { Result = ConfirmCloseResult.Discard; Close(); };
        CancelButton.Click += (_, _) => { Result = ConfirmCloseResult.Cancel; Close(); };
    }

    /// <summary>根据文档路径更新标题文本，便于用户判断当前关闭的是哪个文件。</summary>
    public void SetDocumentPath(string? path)
    {
        if (this.FindControl<TextBlock>("TitleTextBlock") is { } tb)
        {
            tb.Text = string.IsNullOrEmpty(path)
                ? "当前文档有未保存的更改"
                : $"“{System.IO.Path.GetFileName(path)}” 有未保存的更改";
        }
    }
}

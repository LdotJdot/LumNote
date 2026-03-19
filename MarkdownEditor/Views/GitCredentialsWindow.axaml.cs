using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MarkdownEditor.Views;

public partial class GitCredentialsWindow : Window
{
    public bool DidConfirm { get; private set; }
    public string? Username => UsernameBox?.Text?.Trim();
    public string? Password => PasswordBox?.Text;

    public GitCredentialsWindow()
    {
        InitializeComponent();
        Icon = MainWindow.GetAppIcon();
        OkButton!.Click += (_, _) => { DidConfirm = true; Close(); };
        CancelButton!.Click += (_, _) => Close();
    }

    public void SetMessage(string? message)
    {
        if (MessageText != null)
            MessageText.Text = message ?? "请输入用户名和密码（或 Personal Access Token）";
    }

    public void SetUsernameFromUrl(string? usernameFromUrl)
    {
        if (!string.IsNullOrEmpty(usernameFromUrl))
            UsernameBox!.Text = usernameFromUrl;
    }
}

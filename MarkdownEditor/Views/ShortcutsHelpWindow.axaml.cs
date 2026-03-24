namespace MarkdownEditor.Views;

public partial class ShortcutsHelpWindow : Avalonia.Controls.Window
{
    public ShortcutsHelpWindow()
    {
        InitializeComponent();
        Icon = MainWindow.GetAppIcon();
    }
}

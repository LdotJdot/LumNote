namespace MarkdownEditor.ViewModels;

/// <summary>设置中「自动保存」下拉项。</summary>
public sealed class AutoSaveOptionItem
{
    public string Label { get; init; } = "";
    public int Seconds { get; init; }

    public override string ToString() => Label;
}

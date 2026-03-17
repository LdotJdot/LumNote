using Avalonia.Media;

namespace MarkdownEditor.Engine.Highlighting;

/// <summary>
/// Markdown 语法高亮主题配置（颜色/样式集中管理）
/// </summary>
public sealed class MarkdownHighlightTheme
{
    public required IBrush Foreground { get; init; }
    public required IBrush Heading { get; init; }
    public required IBrush Strong { get; init; }
    public required IBrush Emphasis { get; init; }
    public required IBrush Strikethrough { get; init; }
    public required IBrush CodeInline { get; init; }
    public required IBrush CodeBlock { get; init; }
    public required IBrush Link { get; init; }
    public required IBrush ListMarker { get; init; }
    public required IBrush BlockquoteMarker { get; init; }
    public required IBrush TableSeparator { get; init; }
    public required IBrush Math { get; init; }

    /// <summary>
    /// VSCode 深色风格的默认主题
    /// </summary>
    public static MarkdownHighlightTheme DarkTheme { get; } = new()
    {
        Foreground = new SolidColorBrush(Color.FromUInt32(0xffd4d4d4)),
        Heading = new SolidColorBrush(Color.FromUInt32(0xff4fc1ff)),
        Strong = new SolidColorBrush(Color.FromUInt32(0xfff44747)),
        Emphasis = new SolidColorBrush(Color.FromUInt32(0xffce9178)),
        Strikethrough = new SolidColorBrush(Color.FromUInt32(0xff808080)),
        CodeInline = new SolidColorBrush(Color.FromUInt32(0xffc586c0)),
        CodeBlock = new SolidColorBrush(Color.FromUInt32(0xffc586c0)),
        Link = new SolidColorBrush(Color.FromUInt32(0xff3794ff)),
        ListMarker = new SolidColorBrush(Color.FromUInt32(0xff569cd6)),
        BlockquoteMarker = new SolidColorBrush(Color.FromUInt32(0xff608b4e)),
        TableSeparator = new SolidColorBrush(Color.FromUInt32(0xff808080)),
        Math = new SolidColorBrush(Color.FromUInt32(0xffb5cea8))
    };

    /// <summary>
    /// 浅色风格：白底黑字，编辑器语法高亮
    /// </summary>
    public static MarkdownHighlightTheme LightTheme { get; } = new()
    {
        Foreground = new SolidColorBrush(Color.FromUInt32(0xff24292f)),
        Heading = new SolidColorBrush(Color.FromUInt32(0xff0550ae)),
        Strong = new SolidColorBrush(Color.FromUInt32(0xffcf222e)),
        Emphasis = new SolidColorBrush(Color.FromUInt32(0xff8250df)),
        Strikethrough = new SolidColorBrush(Color.FromUInt32(0xff6e7781)),
        CodeInline = new SolidColorBrush(Color.FromUInt32(0xff0550ae)),
        CodeBlock = new SolidColorBrush(Color.FromUInt32(0xff0550ae)),
        Link = new SolidColorBrush(Color.FromUInt32(0xff0566d2)),
        ListMarker = new SolidColorBrush(Color.FromUInt32(0xff0550ae)),
        BlockquoteMarker = new SolidColorBrush(Color.FromUInt32(0xff0969da)),
        TableSeparator = new SolidColorBrush(Color.FromUInt32(0xff6e7781)),
        Math = new SolidColorBrush(Color.FromUInt32(0xff0550ae))
    };
}


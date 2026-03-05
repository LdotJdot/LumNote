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
        Heading = new SolidColorBrush(Color.FromUInt32(0xff4fc1ff)),       // 偏亮蓝
        Strong = new SolidColorBrush(Color.FromUInt32(0xfff44747)),        // 红色
        Emphasis = new SolidColorBrush(Color.FromUInt32(0xffce9178)),      // 橙
        Strikethrough = new SolidColorBrush(Color.FromUInt32(0xff808080)), // 灰
        CodeInline = new SolidColorBrush(Color.FromUInt32(0xffc586c0)),    // 紫
        CodeBlock = new SolidColorBrush(Color.FromUInt32(0xffc586c0)),     // 同行内代码
        Link = new SolidColorBrush(Color.FromUInt32(0xff3794ff)),          // VSCode 链接蓝
        ListMarker = new SolidColorBrush(Color.FromUInt32(0xff569cd6)),    // 蓝
        BlockquoteMarker = new SolidColorBrush(Color.FromUInt32(0xff608b4e)), // 绿
        TableSeparator = new SolidColorBrush(Color.FromUInt32(0xff808080)),
        Math = new SolidColorBrush(Color.FromUInt32(0xffb5cea8))           // 数学 LaTeX 绿
    };
}


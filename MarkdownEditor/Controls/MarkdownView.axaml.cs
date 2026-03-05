using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MarkdownEditor.Core;
using System.Collections.Generic;

namespace MarkdownEditor.Controls;

public partial class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<MarkdownStyleConfig?> StyleConfigProperty =
        AvaloniaProperty.Register<MarkdownView, MarkdownStyleConfig?>(nameof(StyleConfig));

    /// <summary>
    /// 文档所在目录，用于解析图片相对路径。例如: D:\docs\notes
    /// </summary>
    public static readonly StyledProperty<string?> DocumentBasePathProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(DocumentBasePath));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownStyleConfig? StyleConfig
    {
        get => GetValue(StyleConfigProperty);
        set => SetValue(StyleConfigProperty, value);
    }

    public string? DocumentBasePath
    {
        get => GetValue(DocumentBasePathProperty);
        set => SetValue(DocumentBasePathProperty, value);
    }

    private string? _lastMarkdown;
    private DispatcherTimer? _debounceTimer;

    public MarkdownView()
    {
        InitializeComponent();

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RenderMarkdown();
        };

        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((c, e) =>
        {
            if (c._debounceTimer != null)
            {
                c._debounceTimer.Stop();
                c._debounceTimer.Start();
            }
        });
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Markdown != _lastMarkdown)
            RenderMarkdown();
    }

    private void RenderMarkdown()
    {
        var md = Markdown ?? "";
        if (md == _lastMarkdown) return;
        _lastMarkdown = md;

        ContentPanel.Children.Clear();

        try
        {
            var doc = MarkdownParser.Parse(md);
            foreach (var node in doc.Children)
            {
                var child = RenderNode(node);
                if (child != null)
                    ContentPanel.Children.Add(child);
            }
        }
        catch
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "渲染错误",
                Foreground = Brushes.DarkRed
            });
        }
    }

    private Control? RenderNode(MarkdownNode node)
    {
        return node switch
        {
            HeadingNode h => RenderHeading(h),
            ParagraphNode p => RenderParagraph(p),
            CodeBlockNode c => RenderCodeBlock(c),
            BlockquoteNode b => RenderBlockquote(b),
            BulletListNode bl => RenderBulletList(bl),
            OrderedListNode ol => RenderOrderedList(ol),
            TableNode t => RenderTable(t),
            HorizontalRuleNode => RenderHorizontalRule(),
            MathBlockNode m => RenderMathBlock(m),
            _ => null
        };
    }

    private MarkdownStyleConfig GetStyle() => StyleConfig ?? new MarkdownStyleConfig();

    private Control RenderHeading(HeadingNode h)
    {
        var tb = CreateInlineTextBlock(h.Content);
        tb.FontWeight = FontWeight.Bold;
        var s = GetStyle();
        tb.FontSize = h.Level switch
        {
            1 => s.Heading1Size,
            2 => s.Heading2Size,
            3 => 20,
            4 => 18,
            5 => 16,
            _ => 14
        };
        tb.Margin = new Thickness(0, 8, 0, 4);
        return tb;
    }

    private Control RenderParagraph(ParagraphNode p)
    {
        var tb = CreateInlineTextBlock(p.Content);
        tb.TextWrapping = TextWrapping.Wrap;
        return tb;
    }

    private Control RenderCodeBlock(CodeBlockNode c)
    {
        var s = GetStyle();
        var bg = ParseColor(s.CodeBlockBackground);
        var fg = ParseColor(s.CodeBlockTextColor);
        var border = new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 8, 0, 8)
        };

        var tb = new SelectableTextBlock
        {
            Text = c.Code,
            FontFamily = s.CodeFontFamily,
            FontSize = 13,
            Foreground = new SolidColorBrush(fg),
            TextWrapping = TextWrapping.NoWrap
        };

        border.Child = new ScrollViewer
        {
            Content = tb,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        return border;
    }

    private Control RenderBlockquote(BlockquoteNode b)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var child in b.Children)
        {
            var ctrl = RenderNode(child);
            if (ctrl != null)
                panel.Children.Add(ctrl);
        }

        var bc = ParseColor(GetStyle().BlockquoteBorderColor);
        return new Border
        {
            BorderBrush = new SolidColorBrush(bc),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(16, 8, 16, 8),
            Background = Brushes.Transparent,
            Child = panel
        };
    }

    private Control RenderBulletList(BulletListNode bl)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var item in bl.Items)
        {
            var itemPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };

            if (item.IsTask)
            {
                var check = new TextBlock
                {
                    Text = item.IsChecked ? "☑" : "☐",
                    FontSize = 14,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                };
                itemPanel.Children.Add(check);
            }
            else
            {
                itemPanel.Children.Add(new TextBlock { Text = "•", FontSize = 14, Margin = new Thickness(0, 0, 4, 0) });
            }

            var contentPanel = new StackPanel { Spacing = 2 };
            foreach (var c in item.Content)
            {
                var ctrl = RenderNode(c);
                if (ctrl != null)
                    contentPanel.Children.Add(ctrl);
            }
            itemPanel.Children.Add(contentPanel);
            panel.Children.Add(itemPanel);
        }
        return panel;
    }

    private Control RenderOrderedList(OrderedListNode ol)
    {
        var panel = new StackPanel { Spacing = 4 };
        int i = ol.StartNumber;
        foreach (var item in ol.Items)
        {
            var itemPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            itemPanel.Children.Add(new TextBlock { Text = $"{i}.", FontSize = 14, Margin = new Thickness(0, 0, 4, 0), MinWidth = 24 });
            var contentPanel = new StackPanel { Spacing = 2 };
            foreach (var c in item.Content)
            {
                var ctrl = RenderNode(c);
                if (ctrl != null)
                    contentPanel.Children.Add(ctrl);
            }
            itemPanel.Children.Add(contentPanel);
            panel.Children.Add(itemPanel);
            i++;
        }
        return panel;
    }

    private Control RenderTable(TableNode t)
    {
        var s = GetStyle();
        var grid = new Grid();
        int rows = t.Rows.Count + 1;
        int cols = t.Headers.Count;

        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var borderBrush = new SolidColorBrush(ParseColor(s.TableBorderColor));
        var headerBg = new SolidColorBrush(ParseColor(s.TableHeaderBackground));

        for (int c = 0; c < cols; c++)
        {
            var align = GetTableAlignment(t, c);
            var header = new Border
            {
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Background = headerBg,
                Padding = new Thickness(8),
                Child = CreateAlignedTextBlock(t.Headers[c], FontWeight.Bold, align)
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
        }

        for (int r = 0; r < t.Rows.Count; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var align = GetTableAlignment(t, c);
                var cell = new Border
                {
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8),
                    Child = CreateAlignedTextBlock(c < t.Rows[r].Count ? t.Rows[r][c] : "", FontWeight.Normal, align)
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin = new Thickness(0, 8, 0, 8),
            Child = grid
        };
    }

    private static Avalonia.Layout.HorizontalAlignment GetTableAlignment(TableNode t, int col)
    {
        if (t.ColumnAlignments == null || col >= t.ColumnAlignments.Count)
            return Avalonia.Layout.HorizontalAlignment.Left;
        return t.ColumnAlignments[col] switch
        {
            TableAlign.Center => Avalonia.Layout.HorizontalAlignment.Center,
            TableAlign.Right => Avalonia.Layout.HorizontalAlignment.Right,
            _ => Avalonia.Layout.HorizontalAlignment.Left
        };
    }

    private static TextBlock CreateAlignedTextBlock(string text, FontWeight weight, Avalonia.Layout.HorizontalAlignment align)
    {
        return new TextBlock
        {
            Text = text,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = align
        };
    }

    private static Color ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Colors.Black;
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return Color.FromRgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        if (hex.Length == 8)
            return Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                Convert.ToByte(hex.Substring(6, 2), 16));
        return Colors.Black;
    }

    private Control RenderHorizontalRule()
    {
        return new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            Margin = new Thickness(0, 12, 0, 12)
        };
    }

    private Control RenderMathBlock(MathBlockNode m)
    {
        var s = GetStyle();
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xf0, 0xf4, 0xf8)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 8, 0, 8)
        };
        var tb = new SelectableTextBlock
        {
            Text = "\\[ " + m.LaTeX + " \\]",
            FontFamily = s.CodeFontFamily,
            FontSize = 14,
            Foreground = new SolidColorBrush(ParseColor(s.CodeBlockTextColor)),
            TextWrapping = TextWrapping.Wrap
        };
        border.Child = tb;
        return border;
    }


    private SelectableTextBlock CreateInlineTextBlock(List<InlineNode> inlines)
    {
        var s = GetStyle();
        var linkColor = ParseColor(s.LinkColor);
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        var runList = new List<Inline>();

        foreach (var node in inlines)
        {
            switch (node)
            {
                case TextNode tn:
                    runList.Add(new Run(tn.Content));
                    break;
                case BoldNode bn:
                    runList.Add(CreateInlineSpan(bn.Content, FontWeight.Bold, null, null, linkColor, s));
                    break;
                case ItalicNode @in:
                    runList.Add(CreateInlineSpan(@in.Content, FontWeight.Normal, FontStyle.Italic, null, linkColor, s));
                    break;
                case StrikethroughNode sn:
                    runList.Add(CreateInlineSpan(sn.Content, FontWeight.Normal, null, true, linkColor, s));
                    break;
                case CodeNode cn:
                    runList.Add(new Run(" " + cn.Content + " ")
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0xeb, 0xeb, 0xeb)),
                        FontFamily = s.CodeFontFamily,
                        FontSize = 13
                    });
                    break;
                case LinkNode ln:
                    runList.Add(CreateLinkInline(ln, linkColor));
                    break;
                case ImageNode img:
                    runList.Add(CreateImageInline(img));
                    break;
                case MathInlineNode mn:
                    runList.Add(new Run(" " + mn.LaTeX + " ")
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0xf0, 0xf4, 0xf8)),
                        FontFamily = s.CodeFontFamily,
                        FontSize = 14
                    });
                    break;
            }
        }

        foreach (var item in runList)
        {
            if (item is Inline inline)
                tb.Inlines!.Add(inline);
        }
        return tb;
    }

    private Inline CreateLinkInline(LinkNode ln, Color linkColor)
    {
        var txt = new TextBlock
        {
            Text = ln.Text,
            Foreground = new SolidColorBrush(linkColor),
            TextDecorations = TextDecorations.Underline
        };
        var btn = new Button
        {
            Content = txt,
            Foreground = new SolidColorBrush(linkColor),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        var url = ln.Url;
        btn.Click += async (_, _) =>
        {
            try
            {
                var tl = TopLevel.GetTopLevel(this);
                if (tl?.Launcher != null)
                    await tl.Launcher.LaunchUriAsync(new Uri(url));
            }
            catch { }
        };
        return new InlineUIContainer(btn);
    }

    private Inline CreateImageInline(ImageNode img)
    {
        var imageControl = new Image
        {
            MaxWidth = 500,
            MaxHeight = 400,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(4, 2)
        };

        var basePath = DocumentBasePath ?? "";
        var url = img.Url.Trim();

        // 同步加载：base64 和本地文件
        var bitmap = TryLoadBitmapSync(url, basePath);
        if (bitmap != null)
        {
            imageControl.Source = bitmap;
        }
        else
        {
            // 异步加载：http/https 或需要重试的路径
            imageControl.Classes.Add("image-loading");
            _ = LoadImageAsync(imageControl, url, basePath);
        }

        return new InlineUIContainer(imageControl);
    }

    private static Bitmap? TryLoadBitmapSync(string url, string basePath)
    {
        try
        {
            // data:image/png;base64,xxxx
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var commaIdx = url.IndexOf(',');
                if (commaIdx > 0 && commaIdx + 1 < url.Length)
                {
                    var base64 = url[(commaIdx + 1)..];
                    var bytes = Convert.FromBase64String(base64);
                    return new Bitmap(new MemoryStream(bytes));
                }
            }

            // 本地文件路径
            string filePath = url;
            if (!Path.IsPathRooted(url) && !string.IsNullOrEmpty(basePath))
            {
                filePath = Path.GetFullPath(Path.Combine(basePath, url));
            }

            if (File.Exists(filePath))
            {
                return new Bitmap(filePath);
            }
        }
        catch { }
        return null;
    }

    private static async System.Threading.Tasks.Task LoadImageAsync(Image imageControl, string url, string basePath)
    {
        try
        {
            Bitmap? bitmap = null;

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(url);
                bitmap = new Bitmap(new MemoryStream(bytes));
            }
            else
            {
                string filePath = url;
                if (!Path.IsPathRooted(url) && !string.IsNullOrEmpty(basePath))
                {
                    filePath = Path.GetFullPath(Path.Combine(basePath, url));
                }
                if (File.Exists(filePath))
                {
                    bitmap = new Bitmap(filePath);
                }
            }

            if (bitmap != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    imageControl.Source = bitmap;
                    imageControl.Classes.Remove("image-loading");
                });
            }
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                imageControl.Classes.Remove("image-loading");
            });
        }
    }

    private Span CreateInlineSpan(List<InlineNode> inlines, FontWeight weight, FontStyle? style, bool? strikethrough, Color linkColor, MarkdownStyleConfig s)
    {
        var span = new Span
        {
            FontWeight = weight,
            FontStyle = style ?? FontStyle.Normal,
            TextDecorations = strikethrough == true ? TextDecorations.Strikethrough : null
        };

        foreach (var node in inlines)
        {
            switch (node)
            {
                case TextNode tn:
                    span.Inlines!.Add(new Run(tn.Content));
                    break;
                case BoldNode bn:
                    span.Inlines!.Add(CreateInlineSpan(bn.Content, FontWeight.Bold, null, null, linkColor, s));
                    break;
                case ItalicNode @in:
                    span.Inlines!.Add(CreateInlineSpan(@in.Content, FontWeight.Normal, FontStyle.Italic, null, linkColor, s));
                    break;
                case StrikethroughNode sn:
                    span.Inlines!.Add(CreateInlineSpan(sn.Content, FontWeight.Normal, null, true, linkColor, s));
                    break;
                case CodeNode cn:
                    span.Inlines!.Add(new Run(" " + cn.Content + " ")
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0xeb, 0xeb, 0xeb)),
                        FontFamily = s.CodeFontFamily,
                        FontSize = 13
                    });
                    break;
                case LinkNode ln:
                    span.Inlines!.Add(CreateLinkInline(ln, linkColor));
                    break;
                case MathInlineNode mn:
                    span.Inlines!.Add(new Run(" " + mn.LaTeX + " ")
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0xf0, 0xf4, 0xf8)),
                        FontFamily = s.CodeFontFamily,
                        FontSize = 14
                    });
                    break;
            }
        }
        return span;
    }
}

using MarkdownEditor.Core;
using Xunit;

namespace MarkdownEditor.Tests.Core;

public class MarkdownParserTests
{
    private static string CollectPlainText(IReadOnlyList<InlineNode> nodes)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var n in nodes)
        {
            if (n is TextNode t)
                sb.Append(t.Content);
            else if (n is BoldNode b)
                sb.Append(CollectPlainText(b.Content));
            else if (n is ItalicNode i)
                sb.Append(CollectPlainText(i.Content));
        }
        return sb.ToString();
    }

    [Fact]
    public void OrderedList_parenthesis_marker_parses()
    {
        var doc = MarkdownParser.Parse("2) second\n1) first");
        var ol = Assert.IsType<OrderedListNode>(doc.Children[0]);
        Assert.Equal(2, ol.StartNumber);
        var first = Assert.IsType<ParagraphNode>(ol.Items[0].Content[0]);
        Assert.Equal("second", CollectPlainText(first.Content));
    }

    [Fact]
    public void Link_inline_title_double_quotes()
    {
        var doc = MarkdownParser.Parse(@"[a](https://a.com ""hint"")");
        var p = Assert.IsType<ParagraphNode>(doc.Children[0]);
        var ln = Assert.IsType<LinkNode>(p.Content[0]);
        Assert.Equal("https://a.com", ln.Url);
        Assert.Equal("hint", ln.Title);
    }

    [Fact]
    public void Reference_link_resolves_from_definition()
    {
        var md =
            @"[text][1]

[1]: https://b.org ""t""";
        var doc = MarkdownParser.Parse(md);
        var p = Assert.IsType<ParagraphNode>(doc.Children[0]);
        var ln = Assert.IsType<LinkNode>(p.Content[0]);
        Assert.Equal("text", ln.Text);
        Assert.Equal("https://b.org", ln.Url);
        Assert.Equal("t", ln.Title);
    }

    [Fact]
    public void Reference_image_resolves()
    {
        var md =
            @"![alt][2]

[2]: https://example.com/f.png ""imgt""";
        var doc = MarkdownParser.Parse(md);
        var p = Assert.IsType<ParagraphNode>(doc.Children[0]);
        var img = Assert.IsType<ImageNode>(p.Content[0]);
        Assert.Equal("alt", img.Alt);
        Assert.Equal("https://example.com/f.png", img.Url);
        Assert.Equal("imgt", img.Title);
    }

    [Fact]
    public void Backslash_escapes_open_bracket()
    {
        var doc = MarkdownParser.Parse(@"\[not a link]");
        var p = Assert.IsType<ParagraphNode>(doc.Children[0]);
        Assert.Equal("[not a link]", CollectPlainText(p.Content));
    }

    [Fact]
    public void Table_row_respects_escaped_pipe()
    {
        var md =
            @"| a | b |
| --- | --- |
| x \| y | z |";
        var doc = MarkdownParser.Parse(md);
        var t = Assert.IsType<TableNode>(doc.Children[0]);
        Assert.Equal("x | y", t.Rows[0][0]);
        Assert.Equal("z", t.Rows[0][1]);
    }

    [Fact]
    public void Footnote_definition_multiline_continuation()
    {
        var md =
            @"[^a]: first
  second line";
        var doc = MarkdownParser.Parse(md);
        var fn = Assert.IsType<FootnoteDefNode>(doc.Children[0]);
        Assert.Equal("a", fn.Id);
        var p = Assert.IsType<ParagraphNode>(fn.Content[0]);
        var text = CollectPlainText(p.Content);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Setext_heading_level_1_and_2()
    {
        var doc = MarkdownParser.Parse("Title one\n===\n\nTitle two\n---");
        var h1 = Assert.IsType<HeadingNode>(doc.Children[0]);
        Assert.Equal(1, h1.Level);
        var h2 = Assert.IsType<HeadingNode>(doc.Children[2]);
        Assert.Equal(2, h2.Level);
    }

    [Fact]
    public void Autolink_angle_brackets()
    {
        var doc = MarkdownParser.Parse("<https://ex.com>");
        var p = Assert.IsType<ParagraphNode>(doc.Children[0]);
        var ln = Assert.IsType<LinkNode>(p.Content[0]);
        Assert.Equal("https://ex.com", ln.Url);
    }

    [Fact]
    public void Toc_expands_to_bullet_list_with_headings()
    {
        var md =
            @"# H1

[TOC]

## H2";
        var doc = MarkdownParser.Parse(md);
        Assert.IsType<HeadingNode>(doc.Children[0]);
        var list = Assert.IsType<BulletListNode>(doc.Children[2]);
        Assert.True(list.Items.Count >= 2);
    }

    [Fact]
    public void BoldItalic_triple_star_is_bold_wrapping_italic()
    {
        var doc = MarkdownParser.Parse("***粗体斜体***");
        var p = Assert.IsType<ParagraphNode>(doc.Children[0]);
        var bold = Assert.IsType<BoldNode>(p.Content[0]);
        var italic = Assert.IsType<ItalicNode>(bold.Content[0]);
        Assert.Equal("粗体斜体", CollectPlainText(italic.Content));
    }

    [Fact]
    public void BoldItalic_triple_underscore_is_bold_wrapping_italic()
    {
        var doc = MarkdownParser.Parse("___emph___");
        var p = Assert.IsType<ParagraphNode>(doc.Children[0]);
        var bold = Assert.IsType<BoldNode>(p.Content[0]);
        var italic = Assert.IsType<ItalicNode>(bold.Content[0]);
        Assert.Equal("emph", CollectPlainText(italic.Content));
    }
}

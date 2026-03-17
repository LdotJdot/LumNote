using System.Text;
using MarkdownEditor.Core;

namespace MarkdownEditor.Export;

/// <summary>
/// 将 Markdown AST 导出为单文件 HTML（含内联样式）。
/// </summary>
public sealed class HtmlExporter : IMarkdownExporter
{
    public string FormatId => "html";
    public string DisplayName => "HTML";
    public string[] FileExtensions => ["html", "htm"];

    public Task<ExportResult> ExportAsync(
        string markdown,
        string documentBasePath,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            var doc = MarkdownParser.Parse(markdown);
            var html = BuildHtml(doc, documentBasePath, options?.StyleConfig);
            File.WriteAllText(outputPath, html, Encoding.UTF8);
            return Task.FromResult(new ExportResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ExportResult(false, ex.Message));
        }
    }

    private static string BuildHtml(DocumentNode doc, string documentBasePath, MarkdownStyleConfig? style)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>");
        sb.Append(HtmlEncode(GetTitleFromDoc(doc)));
        sb.Append("</title><style>");
        sb.Append(EmbeddedCss(style));
        sb.Append("</style></head><body>");
        foreach (var child in doc.Children)
            AppendBlock(sb, child, documentBasePath);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string GetTitleFromDoc(DocumentNode doc)
    {
        foreach (var node in doc.Children)
        {
            if (node is HeadingNode h && h.Level == 1 && h.Content.Count > 0)
                return FlattenInlineToText(h.Content);
        }
        return "导出文档";
    }

    private static void AppendBlock(StringBuilder sb, MarkdownNode node, string documentBasePath)
    {
        switch (node)
        {
            case HeadingNode h:
                sb.Append("<h").Append(h.Level).Append(">");
                AppendInlines(sb, h.Content, documentBasePath);
                sb.Append("</h").Append(h.Level).Append(">");
                break;
            case ParagraphNode p:
                sb.Append("<p>");
                AppendInlines(sb, p.Content, documentBasePath);
                sb.Append("</p>");
                break;
            case CodeBlockNode c:
                var langClass = string.IsNullOrEmpty(c.Language?.Trim()) ? "language-plaintext" : "language-" + HtmlEncode(c.Language.Trim());
                sb.Append("<pre><code class=\"").Append(langClass).Append("\">");
                sb.Append(HtmlEncode(c.Code));
                sb.Append("</code></pre>");
                break;
            case BlockquoteNode bq:
                sb.Append("<blockquote>");
                foreach (var child in bq.Children)
                    AppendBlock(sb, child, documentBasePath);
                sb.Append("</blockquote>");
                break;
            case BulletListNode ul:
                sb.Append("<ul>");
                foreach (var item in ul.Items)
                    AppendListItem(sb, item, documentBasePath);
                sb.Append("</ul>");
                break;
            case OrderedListNode ol:
                sb.Append("<ol").Append(ol.StartNumber != 1 ? " start=\"" + ol.StartNumber + "\"" : "").Append(">");
                foreach (var item in ol.Items)
                    AppendListItem(sb, item, documentBasePath);
                sb.Append("</ol>");
                break;
            case TableNode t:
                sb.Append("<table><thead><tr>");
                for (int i = 0; i < t.Headers.Count; i++)
                {
                    var align = GetTableAlignCss(t.ColumnAlignments, i);
                    sb.Append("<th").Append(align).Append(">").Append(HtmlEncode(t.Headers[i])).Append("</th>");
                }
                sb.Append("</tr></thead><tbody>");
                foreach (var row in t.Rows)
                {
                    sb.Append("<tr>");
                    for (int i = 0; i < row.Count; i++)
                    {
                        var align = GetTableAlignCss(t.ColumnAlignments, i);
                        sb.Append("<td").Append(align).Append(">").Append(HtmlEncode(row[i])).Append("</td>");
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
                break;
            case HorizontalRuleNode:
                sb.Append("<hr>");
                break;
            case EmptyLineNode e:
                for (int i = 0; i < Math.Min(e.LineCount, 2); i++)
                    sb.Append("<br>");
                break;
            case MathBlockNode m:
                sb.Append("<div class=\"math-block\">").Append(HtmlEncode(m.LaTeX ?? "")).Append("</div>");
                break;
            case HtmlBlockNode html:
                sb.Append(html.RawHtml ?? "");
                break;
            case DefinitionListNode dl:
                sb.Append("<dl>");
                foreach (var item in dl.Items)
                {
                    sb.Append("<dt>");
                    AppendInlines(sb, item.Term, documentBasePath);
                    sb.Append("</dt>");
                    foreach (var def in item.Definitions)
                        AppendBlock(sb, def, documentBasePath);
                }
                sb.Append("</dl>");
                break;
            case FootnoteDefNode:
                break; // 脚注定义在正文中通常不重复输出，或可输出到页面底部
            default:
                break;
        }
    }

    private static string GetTableAlignCss(List<TableAlign>? alignments, int index)
    {
        if (alignments == null || index >= alignments.Count) return "";
        var a = alignments[index];
        return a == TableAlign.Center ? " style=\"text-align:center\"" : a == TableAlign.Right ? " style=\"text-align:right\"" : "";
    }

    private static void AppendListItem(StringBuilder sb, ListItemNode item, string documentBasePath)
    {
        sb.Append("<li>");
        if (item.IsTask)
            sb.Append("<input type=\"checkbox\" disabled").Append(item.IsChecked ? " checked" : "").Append("> ");
        foreach (var child in item.Content)
            AppendBlock(sb, child, documentBasePath);
        sb.Append("</li>");
    }

    private static void AppendInlines(StringBuilder sb, List<InlineNode> inlines, string documentBasePath)
    {
        foreach (var n in inlines)
        {
            switch (n)
            {
                case TextNode t:
                    sb.Append(HtmlEncode(t.Content ?? ""));
                    break;
                case BoldNode b:
                    sb.Append("<strong>");
                    AppendInlines(sb, b.Content, documentBasePath);
                    sb.Append("</strong>");
                    break;
                case ItalicNode i:
                    sb.Append("<em>");
                    AppendInlines(sb, i.Content, documentBasePath);
                    sb.Append("</em>");
                    break;
                case StrikethroughNode s:
                    sb.Append("<s>");
                    AppendInlines(sb, s.Content, documentBasePath);
                    sb.Append("</s>");
                    break;
                case CodeNode c:
                    sb.Append("<code>").Append(HtmlEncode(c.Content ?? "")).Append("</code>");
                    break;
                case LinkNode ln:
                    sb.Append("<a href=\"").Append(HtmlEncode(ResolveUrl(ln.Url, documentBasePath))).Append("\"");
                    if (!string.IsNullOrEmpty(ln.Title))
                        sb.Append(" title=\"").Append(HtmlEncode(ln.Title)).Append("\"");
                    sb.Append(">").Append(HtmlEncode(ln.Text ?? "")).Append("</a>");
                    break;
                case ImageNode img:
                    var src = ResolveUrl(img.Url ?? "", documentBasePath);
                    sb.Append("<img src=\"").Append(HtmlEncode(src)).Append("\" alt=\"").Append(HtmlEncode(img.Alt ?? "")).Append("\"");
                    if (!string.IsNullOrEmpty(img.Title))
                        sb.Append(" title=\"").Append(HtmlEncode(img.Title)).Append("\"");
                    sb.Append(">");
                    break;
                case MathInlineNode math:
                    sb.Append("<span class=\"math-inline\">").Append(HtmlEncode(math.LaTeX ?? "")).Append("</span>");
                    break;
                case FootnoteRefNode fn:
                    sb.Append("<sup class=\"footnote-ref\">[").Append(HtmlEncode(fn.Id ?? "")).Append("]</sup>");
                    break;
                default:
                    break;
            }
        }
    }

    private static string ResolveUrl(string url, string documentBasePath)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return url;
        if (url.StartsWith("/") || url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (!string.IsNullOrEmpty(documentBasePath) && !Path.IsPathRooted(url))
            return Path.GetFullPath(Path.Combine(documentBasePath, url.Replace('/', Path.DirectorySeparatorChar)));
        return url;
    }

    private static string FlattenInlineToText(List<InlineNode> inlines)
    {
        var sb = new StringBuilder();
        foreach (var n in inlines)
        {
            if (n is TextNode t) sb.Append(t.Content);
            else if (n is BoldNode b) sb.Append(FlattenInlineToText(b.Content));
            else if (n is ItalicNode i) sb.Append(FlattenInlineToText(i.Content));
            else if (n is StrikethroughNode s) sb.Append(FlattenInlineToText(s.Content));
            else if (n is CodeNode c) sb.Append(c.Content);
            else if (n is LinkNode ln) sb.Append(ln.Text);
            else if (n is ImageNode img) sb.Append(img.Alt);
            else if (n is MathInlineNode m) sb.Append(m.LaTeX);
            else if (n is FootnoteRefNode fn) sb.Append("[^" + fn.Id + "]");
        }
        return sb.ToString();
    }

    private static string HtmlEncode(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static string EmbeddedCss(MarkdownStyleConfig? style)
    {
        string textColor = "#d4d4d4";
        string backgroundColor = "#1e1e1e";
        string codeBg = "#252526";
        string codeText = "#d4d4d4";
        string borderColor = "#3f3f46";
        string tableHeaderBg = "#2a2d2e";
        string linkColor = "#3794ff";
        string blockquoteColor = "#b0b0b0";
        string bodyFont = "Microsoft YaHei UI, Segoe UI, sans-serif";
        string codeFont = "Cascadia Code, Consolas, monospace";
        int h1Size = 28, h2Size = 24;
        if (style != null)
        {
            textColor = style.TextColor;
            backgroundColor = style.BackgroundColor;
            codeBg = style.CodeBlockBackground;
            codeText = style.CodeBlockTextColor;
            borderColor = style.TableBorderColor;
            tableHeaderBg = style.TableHeaderBackground;
            linkColor = style.LinkColor;
            blockquoteColor = style.BlockquoteBorderColor;
            bodyFont = style.BodyFontFamily ?? bodyFont;
            codeFont = style.CodeFontFamily ?? codeFont;
            h1Size = style.Heading1Size;
            h2Size = style.Heading2Size;
        }

        var sb = new StringBuilder();
        sb.Append("body { font-family: \"").Append(EscapeCssString(bodyFont)).Append("\"; margin: 1em; line-height: 1.5; color: ").Append(textColor).Append("; background: ").Append(backgroundColor).Append("; }\n");
        sb.Append("h1,h2,h3,h4,h5,h6 { margin: 0.8em 0 0.4em; font-weight: 600; }\n");
        sb.Append("h1 { font-size: ").Append(h1Size).Append("px; }\n");
        sb.Append("h2 { font-size: ").Append(h2Size).Append("px; }\n");
        sb.Append("h3 { font-size: 20px; }\n");
        sb.Append("p { margin: 0.5em 0; }\n");
        sb.Append("pre { background: ").Append(codeBg).Append("; padding: 16px; border-radius: 4px; overflow-x: auto; }\n");
        sb.Append("code { font-family: \"").Append(EscapeCssString(codeFont)).Append("\"; font-size: 0.9em; }\n");
        sb.Append("pre code { background: none; padding: 0; color: ").Append(codeText).Append("; }\n");
        sb.Append("blockquote { border-left: 4px solid ").Append(blockquoteColor).Append("; margin: 0.5em 0; padding-left: 1em; color: ").Append(blockquoteColor).Append("; }\n");
        sb.Append("ul, ol { margin: 0.5em 0; padding-left: 1.5em; }\n");
        sb.Append("table { border-collapse: collapse; width: 100%; margin: 0.5em 0; }\n");
        sb.Append("th, td { border: 1px solid ").Append(borderColor).Append("; padding: 6px 10px; text-align: left; }\n");
        sb.Append("th { background: ").Append(tableHeaderBg).Append("; font-weight: 600; }\n");
        sb.Append("a { color: ").Append(linkColor).Append("; text-decoration: none; }\n");
        sb.Append("a:hover { text-decoration: underline; }\n");
        sb.Append("hr { border: none; border-top: 1px solid ").Append(borderColor).Append("; margin: 1em 0; }\n");
        sb.Append(".math-block, .math-inline { font-style: italic; color: #b5cea8; }\n");
        sb.Append(".footnote-ref { font-size: 0.85em; }\n");
        return sb.ToString();
    }

    private static string EscapeCssString(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

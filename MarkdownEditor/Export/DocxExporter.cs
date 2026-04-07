using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MarkdownEditor.Core;
using MarkdownEditor.Engine;
using MarkdownEditor.Engine.Render;
using SkiaSharp;

namespace MarkdownEditor.Export;

/// <summary>
/// 将 Markdown 导出为 DOCX（Office Open XML）格式。
/// 纯 .NET 实现，不依赖第三方库——使用 System.IO.Compression + System.Xml.Linq。
/// 仅针对浅色主题。
/// </summary>
public sealed class DocxExporter : IMarkdownExporter
{
    public string FormatId => "docx";
    public string DisplayName => "Word 文档 (DOCX)";
    public string[] FileExtensions => ["docx"];

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace WPS = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    private const int EmuPerPx = 9525;
    private const int TwipsPerPt = 20;
    private const int FirstLineIndentTwips = 420;

    public Task<ExportResult> ExportAsync(
        string markdown,
        string documentBasePath,
        string outputPath,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            options.ReportProgress("正在解析 Markdown…", 10);
            var doc = MarkdownParser.Parse(markdown ?? "");
            var ctx = new DocxBuildContext(documentBasePath ?? "");
            ctx.HasNumbering = DocumentUsesNumberingLists(doc);
            ctx.InitializeRelationships();

            options.ReportProgress("正在生成 Word 文档结构…", 35);
            var bodyElements = new List<XElement>();
            ConvertChildren(doc.Children, bodyElements, ctx, 0);

            options.ReportProgress("正在打包 DOCX…", 70);
            BuildPackage(outputPath, bodyElements, ctx);
            options.ReportProgress("正在完成…", 95);
            return Task.FromResult(new ExportResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ExportResult(false, ex.Message));
        }
    }

    #region Package Assembly

    private void BuildPackage(string outputPath, List<XElement> bodyElements, DocxBuildContext ctx)
    {
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        WriteEntry(zip, "[Content_Types].xml", BuildContentTypes(ctx));
        WriteEntry(zip, "_rels/.rels", BuildRootRels());
        WriteEntry(zip, "word/document.xml", BuildDocumentXml(bodyElements));
        WriteEntry(zip, "word/styles.xml", BuildStylesXml());
        WriteEntry(zip, "word/settings.xml", BuildSettingsXml());

        if (ctx.HasNumbering)
            WriteEntry(zip, "word/numbering.xml", BuildNumberingXml());

        WriteEntry(zip, "word/_rels/document.xml.rels", BuildDocumentRels(ctx));

        foreach (var (name, data) in ctx.MediaFiles)
        {
            var entry = zip.CreateEntry("word/media/" + name, CompressionLevel.Optimal);
            using var es = entry.Open();
            es.Write(data, 0, data.Length);
        }
    }

    private static void WriteEntry(ZipArchive zip, string path, XDocument xdoc)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var es = entry.Open();
        using var writer = new System.IO.StreamWriter(es, new UTF8Encoding(false));
        xdoc.Save(writer);
    }

    #endregion

    #region XML Part Builders

    private XDocument BuildContentTypes(DocxBuildContext ctx)
    {
        var types = new XElement(CT + "Types",
            new XElement(CT + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(CT + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
            new XElement(CT + "Default", new XAttribute("Extension", "png"), new XAttribute("ContentType", "image/png")),
            new XElement(CT + "Default", new XAttribute("Extension", "jpeg"), new XAttribute("ContentType", "image/jpeg")),
            new XElement(CT + "Default", new XAttribute("Extension", "jpg"), new XAttribute("ContentType", "image/jpeg")),
            new XElement(CT + "Default", new XAttribute("Extension", "gif"), new XAttribute("ContentType", "image/gif")),
            new XElement(CT + "Default", new XAttribute("Extension", "bmp"), new XAttribute("ContentType", "image/bmp")),
            new XElement(CT + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")),
            new XElement(CT + "Override", new XAttribute("PartName", "/word/styles.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml")),
            new XElement(CT + "Override", new XAttribute("PartName", "/word/settings.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"))
        );
        if (ctx.HasNumbering)
            types.Add(new XElement(CT + "Override", new XAttribute("PartName", "/word/numbering.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml")));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), types);
    }

    private XDocument BuildRootRels()
    {
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(PR + "Relationships",
                new XElement(PR + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "word/document.xml"))
            ));
    }

    private XDocument BuildDocumentXml(List<XElement> bodyElements)
    {
        var body = new XElement(W + "body");
        foreach (var el in bodyElements)
            body.Add(el);

        body.Add(new XElement(W + "sectPr",
            new XElement(W + "pgSz",
                new XAttribute(W + "w", "11906"),
                new XAttribute(W + "h", "16838")),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", "1440"),
                new XAttribute(W + "right", "1800"),
                new XAttribute(W + "bottom", "1440"),
                new XAttribute(W + "left", "1800"),
                new XAttribute(W + "header", "851"),
                new XAttribute(W + "footer", "992"),
                new XAttribute(W + "gutter", "0"))
        ));

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "document",
                new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "wp", WP.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "pic", PIC.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "mc", MC.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "wps", WPS.NamespaceName),
                body));
    }

    private XDocument BuildDocumentRels(DocxBuildContext ctx)
    {
        var rels = new XElement(PR + "Relationships");
        foreach (var (rId, target, relType) in ctx.Relationships)
        {
            var rel = new XElement(PR + "Relationship",
                new XAttribute("Id", rId),
                new XAttribute("Type", relType),
                new XAttribute("Target", target));
            if (relType.Contains("hyperlink", StringComparison.Ordinal))
                rel.Add(new XAttribute("TargetMode", "External"));
            rels.Add(rel);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), rels);
    }

    private XDocument BuildSettingsXml()
    {
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "settings",
                new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
                new XElement(W + "defaultTabStop", new XAttribute(W + "val", "420")),
                new XElement(W + "characterSpacingControl", new XAttribute(W + "val", "doNotCompress"))
            ));
    }

    #endregion

    #region Styles

    private XDocument BuildStylesXml()
    {
        var styles = new XElement(W + "styles",
            new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
            BuildDocDefaults(),
            BuildNormalStyle(),
            BuildHeadingStyle(1, "44", "黑体", "0B0B0B", 360, 120),
            BuildHeadingStyle(2, "32", "黑体", "1A1A1A", 260, 100),
            BuildHeadingStyle(3, "28", "黑体", "1A1A1A", 200, 80),
            BuildHeadingStyle(4, "24", "微软雅黑", "333333", 160, 60),
            BuildHeadingStyle(5, "21", "微软雅黑", "333333", 120, 40),
            BuildHeadingStyle(6, "21", "微软雅黑", "555555", 100, 40),
            BuildCodeStyle(),
            BuildCodeBlockStyle(),
            BuildBlockquoteStyle(),
            BuildHyperlinkStyle(),
            BuildFootnoteRefStyle(),
            BuildListParagraphStyle(),
            BuildTocHeadingStyle()
        );
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), styles);
    }

    private XElement BuildDocDefaults()
    {
        return new XElement(W + "docDefaults",
            new XElement(W + "rPrDefault",
                new XElement(W + "rPr",
                    new XElement(W + "rFonts",
                        new XAttribute(W + "ascii", "等线"),
                        new XAttribute(W + "hAnsi", "等线"),
                        new XAttribute(W + "eastAsia", "等线"),
                        new XAttribute(W + "cs", "Times New Roman")),
                    new XElement(W + "sz", new XAttribute(W + "val", "21")),
                    new XElement(W + "szCs", new XAttribute(W + "val", "21")),
                    new XElement(W + "lang",
                        new XAttribute(W + "val", "en-US"),
                        new XAttribute(W + "eastAsia", "zh-CN"))
                )),
            new XElement(W + "pPrDefault",
                new XElement(W + "pPr",
                    new XElement(W + "spacing",
                        new XAttribute(W + "after", "0"),
                        new XAttribute(W + "line", "360"),
                        new XAttribute(W + "lineRule", "auto"))
                ))
        );
    }

    private XElement BuildNormalStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "default", "1"),
            new XAttribute(W + "styleId", "Normal"),
            new XElement(W + "name", new XAttribute(W + "val", "Normal")),
            new XElement(W + "qFormat"),
            new XElement(W + "pPr",
                new XElement(W + "spacing",
                    new XAttribute(W + "after", "120"),
                    new XAttribute(W + "line", "360"),
                    new XAttribute(W + "lineRule", "auto"))
            ),
            new XElement(W + "rPr",
                new XElement(W + "rFonts",
                    new XAttribute(W + "ascii", "等线"),
                    new XAttribute(W + "hAnsi", "等线"),
                    new XAttribute(W + "eastAsia", "等线")),
                new XElement(W + "sz", new XAttribute(W + "val", "21")),
                new XElement(W + "szCs", new XAttribute(W + "val", "21"))
            ));
    }

    private static XElement BuildHeadingStyle(int level, string szVal, string fontFamily, string color, int spaceBefore, int spaceAfter)
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", $"Heading{level}"),
            new XElement(W + "name", new XAttribute(W + "val", $"heading {level}")),
            new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
            new XElement(W + "next", new XAttribute(W + "val", "Normal")),
            new XElement(W + "qFormat"),
            new XElement(W + "pPr",
                new XElement(W + "keepNext"),
                new XElement(W + "keepLines"),
                new XElement(W + "spacing",
                    new XAttribute(W + "before", spaceBefore.ToString()),
                    new XAttribute(W + "after", spaceAfter.ToString())),
                new XElement(W + "outlineLvl", new XAttribute(W + "val", (level - 1).ToString()))
            ),
            new XElement(W + "rPr",
                new XElement(W + "rFonts",
                    new XAttribute(W + "ascii", fontFamily),
                    new XAttribute(W + "hAnsi", fontFamily),
                    new XAttribute(W + "eastAsia", fontFamily)),
                new XElement(W + "b"),
                new XElement(W + "bCs"),
                new XElement(W + "color", new XAttribute(W + "val", color)),
                new XElement(W + "sz", new XAttribute(W + "val", szVal)),
                new XElement(W + "szCs", new XAttribute(W + "val", szVal))
            ));
    }

    private static XElement BuildCodeStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "character"),
            new XAttribute(W + "styleId", "InlineCode"),
            new XElement(W + "name", new XAttribute(W + "val", "Inline Code")),
            new XElement(W + "rPr",
                new XElement(W + "rFonts",
                    new XAttribute(W + "ascii", "Consolas"),
                    new XAttribute(W + "hAnsi", "Consolas"),
                    new XAttribute(W + "eastAsia", "等线"),
                    new XAttribute(W + "cs", "Consolas")),
                new XElement(W + "sz", new XAttribute(W + "val", "19")),
                new XElement(W + "szCs", new XAttribute(W + "val", "19")),
                new XElement(W + "color", new XAttribute(W + "val", "C7254E")),
                new XElement(W + "shd",
                    new XAttribute(W + "val", "clear"),
                    new XAttribute(W + "color", "auto"),
                    new XAttribute(W + "fill", "F5F5F5"))
            ));
    }

    private static XElement BuildCodeBlockStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", "CodeBlock"),
            new XElement(W + "name", new XAttribute(W + "val", "Code Block")),
            new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
            new XElement(W + "qFormat"),
            new XElement(W + "pPr",
                new XElement(W + "pBdr",
                    new XElement(W + "top", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "4"), new XAttribute(W + "color", "E0E0E0")),
                    new XElement(W + "left", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "4"), new XAttribute(W + "color", "E0E0E0")),
                    new XElement(W + "bottom", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "4"), new XAttribute(W + "color", "E0E0E0")),
                    new XElement(W + "right", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "4"), new XAttribute(W + "color", "E0E0E0"))),
                new XElement(W + "shd",
                    new XAttribute(W + "val", "clear"),
                    new XAttribute(W + "color", "auto"),
                    new XAttribute(W + "fill", "F5F5F5")),
                new XElement(W + "spacing",
                    new XAttribute(W + "before", "120"),
                    new XAttribute(W + "after", "120"),
                    new XAttribute(W + "line", "280"),
                    new XAttribute(W + "lineRule", "auto"))
            ),
            new XElement(W + "rPr",
                new XElement(W + "rFonts",
                    new XAttribute(W + "ascii", "Consolas"),
                    new XAttribute(W + "hAnsi", "Consolas"),
                    new XAttribute(W + "eastAsia", "等线"),
                    new XAttribute(W + "cs", "Consolas")),
                new XElement(W + "sz", new XAttribute(W + "val", "18")),
                new XElement(W + "szCs", new XAttribute(W + "val", "18")),
                new XElement(W + "color", new XAttribute(W + "val", "333333"))
            ));
    }

    private static XElement BuildBlockquoteStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", "Blockquote"),
            new XElement(W + "name", new XAttribute(W + "val", "Blockquote")),
            new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
            new XElement(W + "qFormat"),
            new XElement(W + "pPr",
                new XElement(W + "pBdr",
                    new XElement(W + "left",
                        new XAttribute(W + "val", "single"),
                        new XAttribute(W + "sz", "18"),
                        new XAttribute(W + "space", "12"),
                        new XAttribute(W + "color", "BFBFBF"))),
                new XElement(W + "ind",
                    new XAttribute(W + "left", "480")),
                new XElement(W + "spacing",
                    new XAttribute(W + "before", "80"),
                    new XAttribute(W + "after", "80"))
            ),
            new XElement(W + "rPr",
                new XElement(W + "color", new XAttribute(W + "val", "666666")),
                new XElement(W + "i"),
                new XElement(W + "iCs")
            ));
    }

    private static XElement BuildHyperlinkStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "character"),
            new XAttribute(W + "styleId", "Hyperlink"),
            new XElement(W + "name", new XAttribute(W + "val", "Hyperlink")),
            new XElement(W + "rPr",
                new XElement(W + "color", new XAttribute(W + "val", "2B6CB0")),
                new XElement(W + "u", new XAttribute(W + "val", "single"))
            ));
    }

    private static XElement BuildFootnoteRefStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "character"),
            new XAttribute(W + "styleId", "FootnoteRef"),
            new XElement(W + "name", new XAttribute(W + "val", "Footnote Ref")),
            new XElement(W + "rPr",
                new XElement(W + "vertAlign", new XAttribute(W + "val", "superscript")),
                new XElement(W + "color", new XAttribute(W + "val", "2B6CB0")),
                new XElement(W + "sz", new XAttribute(W + "val", "17")),
                new XElement(W + "szCs", new XAttribute(W + "val", "17"))
            ));
    }

    private static XElement BuildListParagraphStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", "ListParagraph"),
            new XElement(W + "name", new XAttribute(W + "val", "List Paragraph")),
            new XElement(W + "basedOn", new XAttribute(W + "val", "Normal")),
            new XElement(W + "qFormat"),
            new XElement(W + "pPr",
                new XElement(W + "ind", new XAttribute(W + "left", "720"))
            ));
    }

    private static XElement BuildTocHeadingStyle()
    {
        return new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", "TOCHeading"),
            new XElement(W + "name", new XAttribute(W + "val", "TOC Heading")),
            new XElement(W + "basedOn", new XAttribute(W + "val", "Heading1")),
            new XElement(W + "next", new XAttribute(W + "val", "Normal")),
            new XElement(W + "qFormat"));
    }

    #endregion

    #region Numbering

    private XDocument BuildNumberingXml()
    {
        var numbering = new XElement(W + "numbering",
            new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
            BuildBulletAbstractNum(),
            BuildOrderedAbstractNum(),
            new XElement(W + "num", new XAttribute(W + "numId", "1"),
                new XElement(W + "abstractNumId", new XAttribute(W + "val", "0"))),
            new XElement(W + "num", new XAttribute(W + "numId", "2"),
                new XElement(W + "abstractNumId", new XAttribute(W + "val", "1")))
        );
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), numbering);
    }

    private static XElement BuildBulletAbstractNum()
    {
        var absNum = new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", "0"),
            new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")));
        // 各级均用 Unicode 实心圆点（与 Markdown「-」列表常见渲染一致）；勿混用 Wingdings/Courier，
        // 否则会出现小方块、空心框等与预览不符的符号。
        const string bulletDot = "\u2022";
        string[] bullets = [bulletDot, bulletDot, bulletDot, bulletDot, bulletDot, bulletDot, bulletDot, bulletDot, bulletDot];
        string[] fonts = ["Calibri", "Calibri", "Calibri", "Calibri", "Calibri", "Calibri", "Calibri", "Calibri", "Calibri"];
        for (int i = 0; i < 9; i++)
        {
            absNum.Add(new XElement(W + "lvl", new XAttribute(W + "ilvl", i.ToString()),
                new XElement(W + "start", new XAttribute(W + "val", "1")),
                new XElement(W + "numFmt", new XAttribute(W + "val", "bullet")),
                new XElement(W + "lvlText", new XAttribute(W + "val", bullets[i])),
                new XElement(W + "lvlJc", new XAttribute(W + "val", "left")),
                new XElement(W + "pPr",
                    new XElement(W + "ind",
                        new XAttribute(W + "left", ((i + 1) * 720).ToString()),
                        new XAttribute(W + "hanging", "360"))),
                new XElement(W + "rPr",
                    new XElement(W + "rFonts",
                        new XAttribute(W + "ascii", fonts[i]),
                        new XAttribute(W + "hAnsi", fonts[i]),
                        new XAttribute(W + "hint", "default")))
            ));
        }
        return absNum;
    }

    private static XElement BuildOrderedAbstractNum()
    {
        var absNum = new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", "1"),
            new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")));
        string[] fmts = ["decimal", "lowerLetter", "lowerRoman", "decimal", "lowerLetter", "lowerRoman", "decimal", "lowerLetter", "lowerRoman"];
        string[] texts = ["%1.", "%2.", "%3.", "%4.", "%5.", "%6.", "%7.", "%8.", "%9."];
        for (int i = 0; i < 9; i++)
        {
            absNum.Add(new XElement(W + "lvl", new XAttribute(W + "ilvl", i.ToString()),
                new XElement(W + "start", new XAttribute(W + "val", "1")),
                new XElement(W + "numFmt", new XAttribute(W + "val", fmts[i])),
                new XElement(W + "lvlText", new XAttribute(W + "val", texts[i])),
                new XElement(W + "lvlJc", new XAttribute(W + "val", "left")),
                new XElement(W + "pPr",
                    new XElement(W + "ind",
                        new XAttribute(W + "left", ((i + 1) * 720).ToString()),
                        new XAttribute(W + "hanging", "360")))
            ));
        }
        return absNum;
    }

    #endregion

    #region AST → OOXML Conversion

    private void ConvertChildren(List<MarkdownNode> children, List<XElement> output, DocxBuildContext ctx, int depth)
    {
        foreach (var node in children)
            ConvertBlock(node, output, ctx, depth);
    }

    private void ConvertBlock(MarkdownNode node, List<XElement> output, DocxBuildContext ctx, int depth)
    {
        switch (node)
        {
            case HeadingNode h:
                output.Add(BuildHeadingParagraph(h, ctx));
                break;

            case ParagraphNode p:
                output.Add(BuildParagraph(p.Content, ctx, withIndent: true));
                break;

            case CodeBlockNode cb:
                BuildCodeBlock(cb, output);
                break;

            case BlockquoteNode bq:
                BuildBlockquote(bq, output, ctx, depth);
                break;

            case BulletListNode bl:
                BuildBulletList(bl, output, ctx, depth);
                break;

            case OrderedListNode ol:
                BuildOrderedList(ol, output, ctx, depth);
                break;

            case TableNode tbl:
                output.Add(BuildTable(tbl, ctx));
                break;

            case HorizontalRuleNode:
                output.Add(BuildHorizontalRule());
                break;

            case MathBlockNode mb:
                BuildMathBlock(mb, output, ctx);
                break;

            case HtmlBlockNode html:
                BuildHtmlBlock(html, output);
                break;

            case DefinitionListNode dl:
                BuildDefinitionList(dl, output, ctx);
                break;

            case FootnoteSectionNode fn:
                BuildFootnoteSection(fn, output, ctx);
                break;

            case FootnoteDefNode fd:
                BuildFootnoteDef(fd, output, ctx, depth);
                break;

            case TableOfContentsNode:
                output.Add(BuildTocPlaceholder());
                break;

            case EmptyLineNode:
                break;

            case DocumentNode doc:
                ConvertChildren(doc.Children, output, ctx, depth);
                break;
        }
    }

    private XElement BuildHeadingParagraph(HeadingNode heading, DocxBuildContext ctx)
    {
        var p = new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "pStyle", new XAttribute(W + "val", $"Heading{heading.Level}"))));
        AddInlineRuns(heading.Content, p, ctx);
        return p;
    }

    private XElement BuildParagraph(List<InlineNode> inlines, DocxBuildContext ctx, bool withIndent = false, string? style = null, XElement? extraPPr = null)
    {
        var pPr = new XElement(W + "pPr");
        if (style != null)
            pPr.Add(new XElement(W + "pStyle", new XAttribute(W + "val", style)));
        if (withIndent)
            pPr.Add(new XElement(W + "ind", new XAttribute(W + "firstLine", FirstLineIndentTwips.ToString())));
        if (extraPPr != null)
        {
            foreach (var child in extraPPr.Elements())
                pPr.Add(child);
        }

        var p = new XElement(W + "p", pPr);
        AddInlineRuns(inlines, p, ctx);
        return p;
    }

    private void BuildCodeBlock(CodeBlockNode codeBlock, List<XElement> output)
    {
        var lines = codeBlock.Code.Split('\n');
        foreach (var line in lines)
        {
            var text = line.TrimEnd('\r');
            var p = new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "pStyle", new XAttribute(W + "val", "CodeBlock"))),
                new XElement(W + "r",
                    new XElement(W + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        SanitizeWordText(text))));
            output.Add(p);
        }
    }

    private void BuildBlockquote(BlockquoteNode bq, List<XElement> output, DocxBuildContext ctx, int depth)
    {
        foreach (var child in bq.Children)
        {
            switch (child)
            {
                case ParagraphNode p:
                    output.Add(BuildParagraph(p.Content, ctx, withIndent: false, style: "Blockquote"));
                    break;
                case BlockquoteNode nested:
                    BuildBlockquote(nested, output, ctx, depth + 1);
                    break;
                default:
                    ConvertBlock(child, output, ctx, depth + 1);
                    break;
            }
        }
    }

    private void BuildBulletList(BulletListNode list, List<XElement> output, DocxBuildContext ctx, int depth)
    {
        foreach (var item in list.Items)
            BuildListItem(item, output, ctx, numId: 1, ilvl: depth, depth);
    }

    private void BuildOrderedList(OrderedListNode list, List<XElement> output, DocxBuildContext ctx, int depth)
    {
        foreach (var item in list.Items)
            BuildListItem(item, output, ctx, numId: 2, ilvl: depth, depth);
    }

    private void BuildListItem(ListItemNode item, List<XElement> output, DocxBuildContext ctx, int numId, int ilvl, int depth)
    {
        bool firstContent = true;
        foreach (var child in item.Content)
        {
            if (child is ParagraphNode p)
            {
                var pPr = new XElement(W + "pPr",
                    new XElement(W + "pStyle", new XAttribute(W + "val", "ListParagraph")));
                if (firstContent)
                {
                    pPr.Add(new XElement(W + "numPr",
                        new XElement(W + "ilvl", new XAttribute(W + "val", ilvl.ToString())),
                        new XElement(W + "numId", new XAttribute(W + "val", numId.ToString()))));
                    firstContent = false;
                }
                else
                {
                    pPr.Add(new XElement(W + "ind",
                        new XAttribute(W + "left", ((ilvl + 1) * 720).ToString())));
                }
                var para = new XElement(W + "p", pPr);

                if (item.IsTask)
                {
                    string checkbox = item.IsChecked ? "\u2611 " : "\u2610 ";
                    para.Add(new XElement(W + "r",
                        new XElement(W + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            checkbox)));
                }

                AddInlineRuns(p.Content, para, ctx);
                output.Add(para);
            }
            else if (child is BulletListNode nestedBl)
            {
                BuildBulletList(nestedBl, output, ctx, depth + 1);
            }
            else if (child is OrderedListNode nestedOl)
            {
                BuildOrderedList(nestedOl, output, ctx, depth + 1);
            }
            else
            {
                ConvertBlock(child, output, ctx, depth + 1);
            }
        }
    }

    private XElement BuildTable(TableNode table, DocxBuildContext ctx)
    {
        int colCount = table.Headers.Count;
        int colWidthTwips = colCount > 0 ? 8300 / colCount : 2000;

        var tblPr = new XElement(W + "tblPr",
            new XElement(W + "tblW", new XAttribute(W + "w", "0"), new XAttribute(W + "type", "auto")),
            new XElement(W + "tblBorders",
                new XElement(W + "top", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "0"), new XAttribute(W + "color", "BFBFBF")),
                new XElement(W + "left", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "0"), new XAttribute(W + "color", "BFBFBF")),
                new XElement(W + "bottom", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "0"), new XAttribute(W + "color", "BFBFBF")),
                new XElement(W + "right", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "0"), new XAttribute(W + "color", "BFBFBF")),
                new XElement(W + "insideH", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "0"), new XAttribute(W + "color", "BFBFBF")),
                new XElement(W + "insideV", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", "4"), new XAttribute(W + "space", "0"), new XAttribute(W + "color", "BFBFBF"))),
            new XElement(W + "tblLook",
                new XAttribute(W + "val", "04A0"),
                new XAttribute(W + "firstRow", "1"),
                new XAttribute(W + "lastRow", "0"),
                new XAttribute(W + "firstColumn", "0"),
                new XAttribute(W + "lastColumn", "0"),
                new XAttribute(W + "noHBand", "0"),
                new XAttribute(W + "noVBand", "1"))
        );

        var tblGrid = new XElement(W + "tblGrid");
        for (int i = 0; i < colCount; i++)
            tblGrid.Add(new XElement(W + "gridCol", new XAttribute(W + "w", colWidthTwips.ToString())));

        var tbl = new XElement(W + "tbl", tblPr, tblGrid);

        var headerRow = new XElement(W + "tr",
            new XElement(W + "trPr", new XElement(W + "tblHeader")));
        for (int i = 0; i < colCount; i++)
        {
            var align = GetTableAlignment(table, i);
            var tcPr = new XElement(W + "tcPr",
                new XElement(W + "shd",
                    new XAttribute(W + "val", "clear"),
                    new XAttribute(W + "color", "auto"),
                    new XAttribute(W + "fill", "F0F0F0")));
            var pPr = new XElement(W + "pPr",
                new XElement(W + "jc", new XAttribute(W + "val", align)));
            headerRow.Add(new XElement(W + "tc", tcPr,
                new XElement(W + "p", pPr,
                    new XElement(W + "r",
                        new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "bCs")),
                        new XElement(W + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            SanitizeWordText(i < table.Headers.Count ? table.Headers[i] : ""))))));
        }
        tbl.Add(headerRow);

        foreach (var row in table.Rows)
        {
            var tr = new XElement(W + "tr");
            for (int i = 0; i < colCount; i++)
            {
                var align = GetTableAlignment(table, i);
                var cellText = i < row.Count ? row[i] : "";
                var pPr = new XElement(W + "pPr",
                    new XElement(W + "jc", new XAttribute(W + "val", align)));

                var inlines = MarkdownParser.ParseInline(cellText);
                var p = new XElement(W + "p", pPr);
                AddInlineRuns(inlines, p, ctx);
                tr.Add(new XElement(W + "tc",
                    new XElement(W + "tcPr"),
                    p));
            }
            tbl.Add(tr);
        }

        return tbl;
    }

    private static string GetTableAlignment(TableNode table, int colIndex)
    {
        if (table.ColumnAlignments == null || colIndex >= table.ColumnAlignments.Count)
            return "left";
        return table.ColumnAlignments[colIndex] switch
        {
            TableAlign.Center => "center",
            TableAlign.Right => "right",
            _ => "left"
        };
    }

    private static XElement BuildHorizontalRule()
    {
        return new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "pBdr",
                    new XElement(W + "bottom",
                        new XAttribute(W + "val", "single"),
                        new XAttribute(W + "sz", "6"),
                        new XAttribute(W + "space", "1"),
                        new XAttribute(W + "color", "BFBFBF")))),
            new XElement(W + "r",
                new XElement(W + "t", "")));
    }

    private void BuildMathBlock(MathBlockNode mathBlock, List<XElement> output, DocxBuildContext ctx)
    {
        var imageData = RenderMathToPng(mathBlock.LaTeX, ctx, blockLevel: true);
        if (imageData != null)
        {
            var (rId, widthEmu, heightEmu) = imageData.Value;
            var p = new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "jc", new XAttribute(W + "val", "center")),
                    new XElement(W + "spacing",
                        new XAttribute(W + "before", "120"),
                        new XAttribute(W + "after", "120"))),
                BuildDrawingRun(rId, widthEmu, heightEmu, "math", ctx));
            output.Add(p);
        }
        else
        {
            output.Add(new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "jc", new XAttribute(W + "val", "center"))),
                new XElement(W + "r",
                    new XElement(W + "rPr",
                        new XElement(W + "rFonts",
                            new XAttribute(W + "ascii", "Cambria Math"),
                            new XAttribute(W + "hAnsi", "Cambria Math")),
                        new XElement(W + "i")),
                    new XElement(W + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        SanitizeWordText(mathBlock.LaTeX ?? "")))));
        }
    }

    private void BuildHtmlBlock(HtmlBlockNode html, List<XElement> output)
    {
        var text = StripHtmlTags(html.RawHtml).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;
        foreach (var line in text.Split('\n'))
        {
            output.Add(new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "ind", new XAttribute(W + "firstLine", FirstLineIndentTwips.ToString()))),
                new XElement(W + "r",
                    new XElement(W + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        SanitizeWordText(line.TrimEnd('\r'))))));
        }
    }

    private void BuildDefinitionList(DefinitionListNode dl, List<XElement> output, DocxBuildContext ctx)
    {
        foreach (var item in dl.Items)
        {
            var termPara = new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "spacing", new XAttribute(W + "before", "120"), new XAttribute(W + "after", "40"))));
            foreach (var inline in item.Term)
                AddInlineRunSingle(inline, termPara, ctx, bold: true);
            output.Add(termPara);

            foreach (var def in item.Definitions)
            {
                if (def is ParagraphNode p)
                {
                    var extraPPr = new XElement("temp",
                        new XElement(W + "ind", new XAttribute(W + "left", "480")));
                    output.Add(BuildParagraph(p.Content, ctx, withIndent: false, extraPPr: extraPPr));
                }
                else
                {
                    ConvertBlock(def, output, ctx, 1);
                }
            }
        }
    }

    private void BuildFootnoteSection(FootnoteSectionNode fn, List<XElement> output, DocxBuildContext ctx)
    {
        output.Add(BuildHorizontalRule());

        foreach (var entry in fn.Items)
        {
            var p = new XElement(W + "p",
                new XElement(W + "pPr",
                    new XElement(W + "spacing", new XAttribute(W + "before", "40"), new XAttribute(W + "after", "40"))));

            p.Add(new XElement(W + "r",
                new XElement(W + "rPr",
                    new XElement(W + "vertAlign", new XAttribute(W + "val", "superscript")),
                    new XElement(W + "sz", new XAttribute(W + "val", "17")),
                    new XElement(W + "szCs", new XAttribute(W + "val", "17"))),
                new XElement(W + "t", entry.Number.ToString())));

            p.Add(new XElement(W + "r",
                new XElement(W + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    " ")));

            foreach (var content in entry.Content)
            {
                if (content is ParagraphNode para)
                    AddInlineRuns(para.Content, p, ctx);
            }
            output.Add(p);
        }
    }

    private void BuildFootnoteDef(FootnoteDefNode fd, List<XElement> output, DocxBuildContext ctx, int depth)
    {
        foreach (var child in fd.Content)
            ConvertBlock(child, output, ctx, depth);
    }

    private static XElement BuildTocPlaceholder()
    {
        return new XElement(W + "p",
            new XElement(W + "pPr",
                new XElement(W + "pStyle", new XAttribute(W + "val", "TOCHeading"))),
            new XElement(W + "r",
                new XElement(W + "t", "目录")));
    }

    #endregion

    #region Inline Conversion

    private void AddInlineRuns(List<InlineNode> inlines, XElement paragraph, DocxBuildContext ctx)
    {
        foreach (var inline in inlines)
            AddInlineRunSingle(inline, paragraph, ctx);
    }

    private void AddInlineRunSingle(InlineNode node, XElement paragraph, DocxBuildContext ctx,
        bool bold = false, bool italic = false, bool strikethrough = false)
    {
        switch (node)
        {
            case TextNode text:
            {
                var rPr = BuildRunProperties(bold, italic, strikethrough);
                paragraph.Add(new XElement(W + "r", rPr,
                    new XElement(W + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        SanitizeWordText(text.Content))));
                break;
            }

            case BoldNode b:
                foreach (var child in b.Content)
                    AddInlineRunSingle(child, paragraph, ctx, bold: true, italic: italic, strikethrough: strikethrough);
                break;

            case ItalicNode i:
                foreach (var child in i.Content)
                    AddInlineRunSingle(child, paragraph, ctx, bold: bold, italic: true, strikethrough: strikethrough);
                break;

            case StrikethroughNode s:
                foreach (var child in s.Content)
                    AddInlineRunSingle(child, paragraph, ctx, bold: bold, italic: italic, strikethrough: true);
                break;

            case CodeNode code:
            {
                var rPr = new XElement(W + "rPr",
                    new XElement(W + "rStyle", new XAttribute(W + "val", "InlineCode")));
                if (bold) rPr.Add(new XElement(W + "b"), new XElement(W + "bCs"));
                if (italic) rPr.Add(new XElement(W + "i"), new XElement(W + "iCs"));
                paragraph.Add(new XElement(W + "r", rPr,
                    new XElement(W + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        SanitizeWordText(code.Content))));
                break;
            }

            case LinkNode link:
            {
                var rId = ctx.AddHyperlinkRelationship(link.Url);
                var rPr = new XElement(W + "rPr",
                    new XElement(W + "rStyle", new XAttribute(W + "val", "Hyperlink")));
                if (bold) rPr.Add(new XElement(W + "b"), new XElement(W + "bCs"));
                if (italic) rPr.Add(new XElement(W + "i"), new XElement(W + "iCs"));
                paragraph.Add(new XElement(W + "hyperlink",
                    new XAttribute(R + "id", rId),
                    new XElement(W + "r", rPr,
                        new XElement(W + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            SanitizeWordText(link.Text)))));
                break;
            }

            case ImageNode img:
                BuildImageInline(img, paragraph, ctx);
                break;

            case MathInlineNode math:
                BuildMathInline(math, paragraph, ctx);
                break;

            case FootnoteMarkerNode fm:
            {
                var rPr = new XElement(W + "rPr",
                    new XElement(W + "rStyle", new XAttribute(W + "val", "FootnoteRef")));
                paragraph.Add(new XElement(W + "r", rPr,
                    new XElement(W + "t", SanitizeWordText($"[{fm.Number}]"))));
                break;
            }

            case FootnoteRefNode fr:
            {
                var rPr = new XElement(W + "rPr",
                    new XElement(W + "rStyle", new XAttribute(W + "val", "FootnoteRef")));
                paragraph.Add(new XElement(W + "r", rPr,
                    new XElement(W + "t", SanitizeWordText($"[{fr.Id}]"))));
                break;
            }
        }
    }

    private static XElement? BuildRunProperties(bool bold, bool italic, bool strikethrough)
    {
        if (!bold && !italic && !strikethrough) return null;
        var rPr = new XElement(W + "rPr");
        if (bold) { rPr.Add(new XElement(W + "b")); rPr.Add(new XElement(W + "bCs")); }
        if (italic) { rPr.Add(new XElement(W + "i")); rPr.Add(new XElement(W + "iCs")); }
        if (strikethrough) rPr.Add(new XElement(W + "strike"));
        return rPr;
    }

    #endregion

    #region Image Embedding

    private void BuildImageInline(ImageNode img, XElement paragraph, DocxBuildContext ctx)
    {
        var result = LoadAndEmbedImage(img.Url, ctx);
        if (result == null)
        {
            paragraph.Add(new XElement(W + "r",
                new XElement(W + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    SanitizeWordText($"[图片: {img.Alt ?? ""}]"))));
            return;
        }

        var (rId, widthEmu, heightEmu) = result.Value;
        paragraph.Add(BuildDrawingRun(rId, widthEmu, heightEmu, img.Alt, ctx));
    }

    private (string rId, long widthEmu, long heightEmu)? LoadAndEmbedImage(string url, DocxBuildContext ctx)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        var resolvedPath = ResolveImagePath(url, ctx.DocumentBasePath);
        if (resolvedPath == null || !File.Exists(resolvedPath))
            return null;

        try
        {
            byte[] imageData = File.ReadAllBytes(resolvedPath);
            string ext = Path.GetExtension(resolvedPath).TrimStart('.').ToLowerInvariant();
            if (ext == "jpg") ext = "jpeg";
            if (ext != "png" && ext != "jpeg" && ext != "gif" && ext != "bmp")
                ext = "png";

            int pixelWidth = 0, pixelHeight = 0;
            using (var bmp = SKBitmap.Decode(imageData))
            {
                if (bmp != null)
                {
                    pixelWidth = bmp.Width;
                    pixelHeight = bmp.Height;
                }
            }

            if (pixelWidth <= 0 || pixelHeight <= 0)
                return null;

            const int maxWidthEmu = 5900000;
            long widthEmu = (long)pixelWidth * EmuPerPx;
            long heightEmu = (long)pixelHeight * EmuPerPx;

            if (widthEmu > maxWidthEmu)
            {
                double scale = (double)maxWidthEmu / widthEmu;
                widthEmu = maxWidthEmu;
                heightEmu = (long)(heightEmu * scale);
            }

            string fileName = $"image{ctx.NextImageIndex()}.{ext}";
            string rId = ctx.AddImageRelationship(fileName);
            ctx.MediaFiles.Add((fileName, imageData));

            return (rId, widthEmu, heightEmu);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveImagePath(string url, string basePath)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        var raw = url.Trim();
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>')
            raw = raw[1..^1].Trim();

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        if (Path.IsPathRooted(raw))
            return raw;

        if (!string.IsNullOrEmpty(basePath))
        {
            try
            {
                var sep = raw.Replace('/', Path.DirectorySeparatorChar);
                return Path.GetFullPath(Path.Combine(basePath, sep));
            }
            catch { return null; }
        }
        return null;
    }

    private XElement BuildDrawingRun(string rId, long widthEmu, long heightEmu, string? alt, DocxBuildContext ctx)
    {
        uint docPrId = ctx.NextDrawingElementId();
        uint cNvId = ctx.NextDrawingElementId();
        string safeName = SanitizeWordText(alt ?? "Image");
        if (string.IsNullOrEmpty(safeName))
            safeName = "Image";
        if (safeName.Length > 250)
            safeName = safeName[..250];

        return new XElement(W + "r",
            new XElement(W + "drawing",
                new XElement(WP + "inline",
                    new XAttribute("distT", "0"),
                    new XAttribute("distB", "0"),
                    new XAttribute("distL", "0"),
                    new XAttribute("distR", "0"),
                    new XElement(WP + "extent",
                        new XAttribute("cx", widthEmu.ToString()),
                        new XAttribute("cy", heightEmu.ToString())),
                    new XElement(WP + "docPr",
                        new XAttribute("id", docPrId.ToString()),
                        new XAttribute("name", safeName)),
                    new XElement(WP + "cNvGraphicFramePr",
                        new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", "1"))),
                    new XElement(A + "graphic",
                        new XElement(A + "graphicData",
                            new XAttribute("uri", PIC.NamespaceName),
                            new XElement(PIC + "pic",
                                new XElement(PIC + "nvPicPr",
                                    new XElement(PIC + "cNvPr",
                                        new XAttribute("id", cNvId.ToString()),
                                        new XAttribute("name", safeName)),
                                    new XElement(PIC + "cNvPicPr")),
                                new XElement(PIC + "blipFill",
                                    new XElement(A + "blip",
                                        new XAttribute(R + "embed", rId)),
                                    new XElement(A + "stretch",
                                        new XElement(A + "fillRect"))),
                                new XElement(PIC + "spPr",
                                    new XElement(A + "xfrm",
                                        new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                                        new XElement(A + "ext",
                                            new XAttribute("cx", widthEmu.ToString()),
                                            new XAttribute("cy", heightEmu.ToString()))),
                                    new XElement(A + "prstGeom",
                                        new XAttribute("prst", "rect"),
                                        new XElement(A + "avLst"))))))
                )));
    }

    #endregion

    #region Math Rendering

    private (string rId, long widthEmu, long heightEmu)? RenderMathToPng(string latex, DocxBuildContext ctx, bool blockLevel)
    {
        try
        {
            var config = new EngineConfig
            {
                TextColor = 0xFF000000,
                PageBackground = 0xFFFFFFFF,
                MathBackground = 0xFFFFFFFF,
                BaseFontSize = 16f
            };

            var renderer = new SkiaRenderer(config);
            var latexRaw = latex ?? "";
            var (boxW, boxH, boxD) = renderer.MeasureMathFormula(latexRaw);
            float inkH = boxH + boxD;
            if (boxW <= 0 && inkH <= 0)
                return null;

            // DrawFormula 在水平方向左右各留 4px，与 MathSkiaRenderer / 布局中 MeasureMathInline 的 w+8 一致
            const float horizontalPad = 8f;
            float drawW = Math.Max(1f, boxW + horizontalPad);
            // 与 SkiaLayoutEngine.LayoutMathBlock 的 MathBlockPadding（上下各 16px）一致；行内公式略留边
            const float blockVerticalPad = 32f;
            float drawH = Math.Max(1f, inkH + (blockLevel ? blockVerticalPad : 8f));

            int renderW = (int)Math.Ceiling(drawW);
            int renderH = (int)Math.Ceiling(drawH);

            using var surface = SKSurface.Create(new SKImageInfo(renderW, renderH, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface == null) return null;

            surface.Canvas.Clear(SKColors.White);
            renderer.DrawMathFormula(surface.Canvas, new SKRect(0, 0, renderW, renderH), latexRaw);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return null;

            byte[] pngBytes = data.ToArray();
            long widthEmu = (long)renderW * EmuPerPx;
            long heightEmu = (long)renderH * EmuPerPx;

            const long maxWidthEmu = 5900000;
            if (widthEmu > maxWidthEmu)
            {
                double scale = (double)maxWidthEmu / widthEmu;
                widthEmu = maxWidthEmu;
                heightEmu = (long)(heightEmu * scale);
            }

            string fileName = $"math{ctx.NextImageIndex()}.png";
            string rId = ctx.AddImageRelationship(fileName);
            ctx.MediaFiles.Add((fileName, pngBytes));

            return (rId, widthEmu, heightEmu);
        }
        catch
        {
            return null;
        }
    }

    private void BuildMathInline(MathInlineNode math, XElement paragraph, DocxBuildContext ctx)
    {
        var result = RenderMathToPng(math.LaTeX, ctx, blockLevel: false);
        if (result != null)
        {
            var (rId, widthEmu, heightEmu) = result.Value;
            paragraph.Add(BuildDrawingRun(rId, widthEmu, heightEmu, "math", ctx));
        }
        else
        {
            var latexText = SanitizeWordText(math.LaTeX ?? "");
            paragraph.Add(new XElement(W + "r",
                new XElement(W + "rPr",
                    new XElement(W + "rFonts",
                        new XAttribute(W + "ascii", "Cambria Math"),
                        new XAttribute(W + "hAnsi", "Cambria Math")),
                    new XElement(W + "i")),
                new XElement(W + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    latexText)));
        }
    }

    #endregion

    #region Utilities

    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    private static string StripHtmlTags(string html)
    {
        return HtmlTagRegex.Replace(html, "");
    }

    /// <summary>
    /// 文档中是否出现无序/有序列表（用于在转换前决定是否生成 numbering 部件与 rId3）。
    /// </summary>
    private static bool DocumentUsesNumberingLists(DocumentNode doc) =>
        doc.Children.Any(UsesBulletOrOrderedListRecursive);

    private static bool UsesBulletOrOrderedListRecursive(MarkdownNode node) =>
        node switch
        {
            BulletListNode or OrderedListNode => true,
            DocumentNode d => d.Children.Any(UsesBulletOrOrderedListRecursive),
            BlockquoteNode b => b.Children.Any(UsesBulletOrOrderedListRecursive),
            ListItemNode li => li.Content.Any(UsesBulletOrOrderedListRecursive),
            DefinitionListNode dl => dl.Items.Any(i => i.Definitions.Any(UsesBulletOrOrderedListRecursive)),
            FootnoteDefNode fd => fd.Content.Any(UsesBulletOrOrderedListRecursive),
            FootnoteSectionNode fs => fs.Items.Any(e => e.Content.Any(UsesBulletOrOrderedListRecursive)),
            _ => false
        };

    /// <summary>
    /// WordprocessingML 的 w:t 必须符合 XML 1.0 字符范围；控制字符会导致整包损坏。
    /// </summary>
    private static string SanitizeWordText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
        {
            int v = r.Value;
            if (v is 0x9 or 0xA or 0xD)
            {
                sb.Append(' ');
                continue;
            }

            if (v < 0x20)
                continue;
            if (v <= 0xD7FF)
                sb.Append(r.ToString());
            else if (v >= 0xE000 && v <= 0xFFFD)
                sb.Append(r.ToString());
            else if (v >= 0x10000 && v <= 0x10FFFF)
                sb.Append(r.ToString());
        }

        return sb.ToString();
    }

    #endregion

    #region Build Context

    private sealed class DocxBuildContext
    {
        private const string StyleRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
        private const string SettingsRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings";
        private const string NumberingRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering";
        private const string ImageRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
        private const string HyperlinkRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";

        public string DocumentBasePath { get; }
        public bool HasNumbering { get; set; }
        public List<(string FileName, byte[] Data)> MediaFiles { get; } = [];
        public List<(string RId, string Target, string Type)> Relationships { get; } = [];

        private int _nextRid;
        private uint _drawingElementId;
        private int _imageIndex;

        public DocxBuildContext(string documentBasePath) => DocumentBasePath = documentBasePath;

        /// <summary>
        /// 必须在转换正文前调用。Word 要求 relationship Id 为 rId1、rId2… 形式。
        /// </summary>
        public void InitializeRelationships()
        {
            Relationships.Add(("rId1", "styles.xml", StyleRelType));
            Relationships.Add(("rId2", "settings.xml", SettingsRelType));
            if (HasNumbering)
                Relationships.Add(("rId3", "numbering.xml", NumberingRelType));
            _nextRid = Relationships.Count + 1;
        }

        public uint NextDrawingElementId() => ++_drawingElementId;

        public int NextImageIndex() => ++_imageIndex;

        public string AddImageRelationship(string fileName)
        {
            var rId = "rId" + _nextRid++;
            Relationships.Add((rId, "media/" + fileName, ImageRelType));
            return rId;
        }

        public string AddHyperlinkRelationship(string url)
        {
            if (string.IsNullOrEmpty(url))
                url = "#";
            var existing = Relationships.FirstOrDefault(r =>
                r.Target == url && r.Type.Contains("hyperlink", StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(existing.RId))
                return existing.RId;

            var rId = "rId" + _nextRid++;
            Relationships.Add((rId, url, HyperlinkRelType));
            return rId;
        }
    }

    #endregion
}

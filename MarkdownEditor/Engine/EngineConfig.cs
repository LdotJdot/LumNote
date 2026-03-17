using System;
using System.IO;

namespace MarkdownEditor.Engine;

/// <summary>
/// 渲染引擎配置 - 字体、样式等
/// </summary>
public sealed class EngineConfig
{
    public string BodyFontFamily { get; set; } =
        "Microsoft YaHei UI,Segoe UI,PingFang SC,sans-serif";
    public string CodeFontFamily { get; set; } =
        "Cascadia Code,Consolas,Microsoft YaHei Mono,monospace";

    /// <summary>
    /// 数学公式首选字体列表（逗号分隔，优先级从左到右），
    /// 建议包含 Latin Modern Math / STIX Two Math / Cambria Math 等支持 MATH 表的字体。
    /// </summary>
    public string MathFontFamily { get; set; } =
        "Latin Modern Math,STIX Two Math,Cambria Math,Times New Roman";

    /// <summary>
    /// 可选：显式指定数学字体文件路径（OTF/TTF），
    /// 若设置且存在，则优先通过 SKTypeface.FromFile 加载该字体。
    /// </summary>
    public string? MathFontFilePath { get; set; } = DetectDefaultMathFontFilePath();
    public float BaseFontSize { get; set; } = 16;
    public float LineSpacing { get; set; } = 1.35f;
    public float ParaSpacing { get; set; } = 10f;

    /// <summary>预览区整体缩放（1f=100%），作用于字号与所有间距。</summary>
    public float ZoomLevel { get; set; } = 1f;

    /// <summary>列表块顶部/底部与段落的额外间距（使段落与列表不会贴太近）。</summary>
    public float ListBlockMarginTop { get; set; } = 6f;
    public float ListBlockMarginBottom { get; set; } = 6f;
    /// <summary>列表项与下一项之间的额外间距。</summary>
    public float ListItemGap { get; set; } = 3f;

    /// <summary>预览区内容左右内边距（与布局引擎协调，用于测量/滚动）。</summary>
    public float ContentPaddingX { get; set; } = 24f;

    /// <summary>预览区内容上下内边距。</summary>
    public float ContentPaddingY { get; set; } = 16f;

    /// <summary>块级内容左侧缩进（与视口边缘的距离）。</summary>
    public float BlockIndent { get; set; } = 14f;

    /// <summary>文档底部额外留白。</summary>
    public float ExtraBottomPadding { get; set; } = 8f;

    /// <summary>代码块/HTML 块内边距。</summary>
    public float CodeBlockPadding { get; set; } = 16f;

    /// <summary>块内文本与块边缘的内边距。</summary>
    public float BlockInnerPadding { get; set; } = 12f;

    /// <summary>引用块与正文的间距及缩进。</summary>
    public float BlockquotePadding { get; set; } = 8f;
    public float BlockquoteIndent { get; set; } = 14f;

    /// <summary>列表项相对列表边缘的缩进。</summary>
    public float ListItemIndent { get; set; } = 20f;

    /// <summary>脚注区块顶部留白。</summary>
    public float FootnoteTopMargin { get; set; } = 24f;

    /// <summary>定义列表术语与定义的缩进差。</summary>
    public float DefinitionListIndent { get; set; } = 24f;

    /// <summary>正文文本颜色。</summary>
    public uint TextColor { get; set; } = 0xFFD4D4D4;
    /// <summary>整页背景色（Markdown 渲染区域底色）。</summary>
    public uint PageBackground { get; set; } = 0xFF1E1E1E;
    public uint TableBorderColor { get; set; } = 0xFFD0D7DE;
    public uint TableHeaderBackground { get; set; } = 0xFFF6F8FA;

    public uint CodeBackground { get; set; } = 0xFF252526;
    public uint CodeKeywordColor { get; set; } = 0xFF569CD6;
    public uint CodeStringColor { get; set; } = 0xFFCE9178;
    public uint CodeCommentColor { get; set; } = 0xFF6A9955;
    public uint CodeNumberColor { get; set; } = 0xFFB5CEA8;
    public uint CodeDefaultColor { get; set; } = 0xFFD4D4D4;
    public uint MathBackground { get; set; } = 0xFF252526;
    public uint SelectionColor { get; set; } = 0x503399FF; // 默认带透明度的高亮
    public uint ImagePlaceholderColor { get; set; } = 0xFF404040;
    public uint LinkColor { get; set; } = 0xFF3794FF;

    /// <summary>返回将 ZoomLevel 应用到所有像素尺寸后的新配置，ZoomLevel 置为 1。用于传入布局/渲染引擎。</summary>
    public EngineConfig WithZoomApplied()
    {
        float z = Math.Clamp(ZoomLevel, 0.5f, 2.5f);
        return new EngineConfig
        {
            BodyFontFamily = BodyFontFamily,
            CodeFontFamily = CodeFontFamily,
            MathFontFamily = MathFontFamily,
            MathFontFilePath = MathFontFilePath,
            BaseFontSize = BaseFontSize * z,
            LineSpacing = LineSpacing,
            ParaSpacing = ParaSpacing * z,
            ZoomLevel = 1f,
            ListBlockMarginTop = ListBlockMarginTop * z,
            ListBlockMarginBottom = ListBlockMarginBottom * z,
            ListItemGap = ListItemGap * z,
            ContentPaddingX = ContentPaddingX * z,
            ContentPaddingY = ContentPaddingY * z,
            BlockIndent = BlockIndent * z,
            ExtraBottomPadding = ExtraBottomPadding * z,
            CodeBlockPadding = CodeBlockPadding * z,
            BlockInnerPadding = BlockInnerPadding * z,
            BlockquotePadding = BlockquotePadding * z,
            BlockquoteIndent = BlockquoteIndent * z,
            ListItemIndent = ListItemIndent * z,
            FootnoteTopMargin = FootnoteTopMargin * z,
            DefinitionListIndent = DefinitionListIndent * z,
            TextColor = TextColor,
            PageBackground = PageBackground,
            TableBorderColor = TableBorderColor,
            TableHeaderBackground = TableHeaderBackground,
            CodeBackground = CodeBackground,
            CodeKeywordColor = CodeKeywordColor,
            CodeStringColor = CodeStringColor,
            CodeCommentColor = CodeCommentColor,
            CodeNumberColor = CodeNumberColor,
            CodeDefaultColor = CodeDefaultColor,
            MathBackground = MathBackground,
            SelectionColor = SelectionColor,
            ImagePlaceholderColor = ImagePlaceholderColor,
            LinkColor = LinkColor
        };
    }

    public static EngineConfig FromStyle(Core.MarkdownStyleConfig? style)
    {
        if (style == null)
            return new EngineConfig();
        var c = new EngineConfig
        {
            BodyFontFamily = style.BodyFontFamily,
            CodeFontFamily = style.CodeFontFamily,
            ZoomLevel = (float)Math.Clamp(style.ZoomLevel, 0.5, 2.5),
            TextColor = Core.ColorUtils.ParseHexColor(style.TextColor),
            TableBorderColor = Core.ColorUtils.ParseHexColor(style.TableBorderColor),
            TableHeaderBackground = Core.ColorUtils.ParseHexColor(style.TableHeaderBackground),
            PageBackground = Core.ColorUtils.ParseHexColor(style.BackgroundColor),
            CodeBackground = Core.ColorUtils.ParseHexColor(style.CodeBlockBackground),
            CodeKeywordColor = Core.ColorUtils.ParseHexColor(style.CodeKeywordColor),
            CodeStringColor = Core.ColorUtils.ParseHexColor(style.CodeStringColor),
            CodeCommentColor = Core.ColorUtils.ParseHexColor(style.CodeCommentColor),
            CodeNumberColor = Core.ColorUtils.ParseHexColor(style.CodeNumberColor),
            CodeDefaultColor = Core.ColorUtils.ParseHexColor(style.CodeDefaultColor),
            MathBackground = Core.ColorUtils.ParseHexColor(style.MathBackground),
            SelectionColor = Core.ColorUtils.ParseHexColor(style.SelectionColor),
            ImagePlaceholderColor = Core.ColorUtils.ParseHexColor(style.ImagePlaceholderColor),
            LinkColor = Core.ColorUtils.ParseHexColor(style.LinkColor)
        };
        return c;
    }

    /// <summary>
    /// 尝试基于应用运行目录自动探测默认的数学字体文件（Latin Modern Math）。
    /// 优先查找发布/调试输出目录下的 asserts\otf\latinmodern-math.otf。
    /// </summary>
    private static string? DetectDefaultMathFontFilePath()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "asserts", "otf", "latinmodern-math.otf"),
                // 开发环境下，若未复制到输出目录，则尝试从工程根目录相对路径查找
                Path.Combine(baseDir, "..", "..", "..", "asserts", "otf", "latinmodern-math.otf"),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
        }
        catch
        {
            // 忽略探测过程中的任何异常，保持 MathFontFilePath 为空，由 MathFontFamily 回退
        }

        return null;
    }

}

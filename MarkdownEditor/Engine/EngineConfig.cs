using System;
using System.Globalization;
using System.IO;

namespace MarkdownEditor.Engine;

/// <summary>
/// 渲染引擎配置 - 字体、样式等
/// </summary>
public sealed class EngineConfig
{
    public string BodyFontFamily { get; set; } = "Microsoft YaHei UI,Segoe UI,PingFang SC,sans-serif";
    public string CodeFontFamily { get; set; } = "Cascadia Code,Consolas,Microsoft YaHei Mono,monospace";
    /// <summary>
    /// 数学公式首选字体列表（逗号分隔，优先级从左到右），
    /// 建议包含 Latin Modern Math / STIX Two Math / Cambria Math 等支持 MATH 表的字体。
    /// </summary>
    public string MathFontFamily { get; set; } = "Latin Modern Math,STIX Two Math,Cambria Math,Times New Roman";
    /// <summary>
    /// 可选：显式指定数学字体文件路径（OTF/TTF），
    /// 若设置且存在，则优先通过 SKTypeface.FromFile 加载该字体。
    /// </summary>
    public string? MathFontFilePath { get; set; } = DetectDefaultMathFontFilePath();
    public float BaseFontSize { get; set; } = 16;
    public float LineSpacing { get; set; } = 1.4f;
    public float ParaSpacing { get; set; } = 8f;

    public uint TextColor { get; set; } = 0xFFD4D4D4;
    public uint TableBorderColor { get; set; } = 0xFFD0D7DE;
    public uint TableHeaderBackground { get; set; } = 0xFFF6F8FA;

    public uint CodeBackground { get; set; } = 0xFF252526;
    public uint MathBackground { get; set; } = 0xFF252526;
    public uint SelectionColor { get; set; } = 0x503399FF; // 默认带透明度的高亮
    public uint ImagePlaceholderColor { get; set; } = 0xFF404040;
    public uint LinkColor { get; set; } = 0xFF3794FF;

    public static EngineConfig FromStyle(Core.MarkdownStyleConfig? style)
    {
        if (style == null) return new EngineConfig();
        var c = new EngineConfig
        {
            BodyFontFamily = style.BodyFontFamily,
            CodeFontFamily = style.CodeFontFamily,
            // 若未来 MarkdownStyleConfig 扩展了数学字体相关字段，可在此补充映射；
            // 目前保留 EngineConfig.MathFontFamily/MathFontFilePath 的默认值或外部显式设置。
            TextColor = ParseHexColor(style.TextColor),
            TableBorderColor = ParseHexColor(style.TableBorderColor),
            TableHeaderBackground = ParseHexColor(style.TableHeaderBackground),
            CodeBackground = ParseHexColor(style.CodeBlockBackground),
            MathBackground = ParseHexColor(style.MathBackground),
            SelectionColor = ParseHexColor(style.SelectionColor),
            ImagePlaceholderColor = ParseHexColor(style.ImagePlaceholderColor),
            LinkColor = ParseHexColor(style.LinkColor)
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

    private static uint ParseHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0xFFD0D7DE;
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return 0xFF000000u | uint.Parse(hex, NumberStyles.HexNumber);
        if (hex.Length == 8)
            return uint.Parse(hex, NumberStyles.HexNumber);
        return 0xFFD0D7DE;
    }
}

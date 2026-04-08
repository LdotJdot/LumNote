using MarkdownEditor.Core;
using SkiaSharp;

namespace MarkdownEditor.Engine.Layout.Blocks;

/// <summary>
/// 标题块布局器 - 负责将 HeadingNode 布局为 LayoutBlock（仅负责自身内容，统一通过 ILayoutEnvironment 测量）。
/// </summary>
public sealed class HeadingBlockLayouter : IBlockLayouter
{
    public bool Matches(MarkdownNode node) => node is HeadingNode;

    public LayoutBlock Layout(MarkdownNode node, in BlockLayoutContext ctx)
    {
        var h = (HeadingNode)node;
        var env = ctx.Environment;
        var block = new LayoutBlock
        {
            BlockIndex = ctx.BlockIndex,
            StartLine = ctx.StartLine,
            EndLine = ctx.EndLine
        };

        var fontSize = h.Level switch { 1 => 28, 2 => 24, 3 => 20, 4 => 18, 5 => 16, _ => 14 };
        var plain = env.FlattenInlines(h.Content);
        var bodyTf = env.GetBodyTypeface();
        var font = new SKFont(SKTypeface.FromFamilyName(bodyTf.FamilyName, SKFontStyle.Bold), fontSize);
        var lineH = fontSize * env.LineSpacing;
        var innerW = ctx.Width - env.BlockInnerPadding * 2;
        var segments = env.BreakTextWithWrap(plain, innerW, font);
        float y = 0;
        var headingStyle = (RunStyle)((int)RunStyle.Heading1 + Math.Clamp(h.Level, 1, 6) - 1);
        foreach (var seg in segments)
        {
            var line = new LayoutLine { Y = y, Height = lineH };
            line.Runs.Add(new LayoutRun(seg, new SKRect(env.BlockInnerPadding, y, ctx.Width - env.BlockInnerPadding, y + lineH), headingStyle, block.BlockIndex, 0));
            block.Lines.Add(line);
            y += lineH;
        }
        y += env.ParaSpacing;
        block.Bounds = new SKRect(0, 0, ctx.Width, y);
        return block;
    }
}

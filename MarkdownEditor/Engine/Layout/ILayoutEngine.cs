using MarkdownEditor.Core;
using MarkdownEditor.Engine.Document;

namespace MarkdownEditor.Engine.Layout;

/// <summary>
/// 布局引擎 - 将 AST 块转为可绘制布局
/// 支持增量、缓存、对象池
/// </summary>
public interface ILayoutEngine
{
    /// <summary>
    /// 布局单个块。footnoteRefOrder 为正文中脚注引用出现顺序（用于上标编号），null 表示不解析脚注。
    /// </summary>
    LayoutBlock Layout(MarkdownNode node, float width, int blockIndex, int startLine, int endLine, IReadOnlyList<string>? footnoteRefOrder = null);

    /// <summary>
    /// 布局文末脚注区（按 refOrder 顺序、自动编号）。refPositionsById 为每个 id 的引用位置列表，用于生成 ↩︎ 回链。
    /// </summary>
    LayoutBlock LayoutFootnoteSection(IReadOnlyList<string> refOrder, IReadOnlyDictionary<string, FootnoteDefNode> defs, IReadOnlyDictionary<string, List<(int blockIndex, int charOffset)>> refPositionsById, float width, int blockIndex);

    /// <summary>
    /// 可用宽度变化时是否需重新布局
    /// </summary>
    bool RequiresRelayoutOnWidthChange { get; }

    /// <summary>
    /// 测量文本内 x 位置对应的字符偏移（用于命中测试）
    /// </summary>
    int MeasureTextOffset(string text, float x, RunStyle style);
}

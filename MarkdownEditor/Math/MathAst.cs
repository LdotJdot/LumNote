namespace MarkdownEditor.Latex;

/// <summary>数学公式根节点，既可表示行内也可表示块级公式。</summary>
public sealed class MathFormula
{
    public required MathNode Root { get; init; }
}

/// <summary>数学样式（对应 TeX 的 display / text / script / scriptscript）。</summary>
public enum MathStyle
{
    Display,
    Text,
    Script,
    ScriptScript
}

/// <summary>数学节点基类。</summary>
public abstract class MathNode;

/// <summary>顺序节点，一串子节点按水平方向依次排布。</summary>
public sealed class MathSequence : MathNode
{
    public required List<MathNode> Children { get; init; }
}

/// <summary>单个可见符号（字母、数字、运算符等）。</summary>
public sealed class MathSymbol : MathNode
{
    public required string Text { get; init; }
}

/// <summary>文本节点（用于 \text{...} 中的普通文字，例如中文说明）。</summary>
public sealed class MathText : MathNode
{
    /// <summary>原样保留的文本内容，不再按数学符号拆分。</summary>
    public required string Text { get; init; }
}

/// <summary>上标/下标组合。</summary>
public sealed class MathSupSub : MathNode
{
    public required MathNode Base { get; init; }
    public MathNode? Sup { get; init; }
    public MathNode? Sub { get; init; }
}

/// <summary>分数 a/b。</summary>
public sealed class MathFraction : MathNode
{
    public required MathNode Numerator { get; init; }
    public required MathNode Denominator { get; init; }
}

/// <summary>根号，例如 \sqrt{x} 或 \sqrt[n]{x}。</summary>
public sealed class MathRoot : MathNode
{
    /// <summary>可选的根指数（如 \sqrt[n]{x} 中的 n）。</summary>
    public MathNode? Degree { get; init; }
    public required MathNode Radicand { get; init; }
}

/// <summary>多行等式（如 eqnarray 中的一行）。</summary>
public sealed class MathRow : MathNode
{
    /// <summary>一行中的各个对齐单元，由 & 分隔。</summary>
    public required List<MathNode> Cells { get; init; }
}

/// <summary>多行环境（如 eqnarray、align 等）。</summary>
public sealed class MathEnvironment : MathNode
{
    /// <summary>环境名，如 "eqnarray"。</summary>
    public required string Name { get; init; }

    /// <summary>环境中的所有行。</summary>
    public required List<MathRow> Rows { get; init; }
}

/// <summary>重音节点，如 \hat{x}、\vec{v}、\widehat{\gamma}。</summary>
public sealed class MathAccent : MathNode
{
    /// <summary>重音命令名（不含反斜杠），如 "hat"、"vec"。</summary>
    public required string AccentName { get; init; }

    /// <summary>被加重音的基础节点。</summary>
    public required MathNode Base { get; init; }
}


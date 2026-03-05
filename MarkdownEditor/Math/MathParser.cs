using System;
using System.Collections.Generic;
using System.Text;

namespace MarkdownEditor.Latex;

/// <summary>
/// LaTeX 数学解析器：实现一个“完整的数学子集”，而不是全量 TeX 引擎。
/// 输入为 $...$ 或 $$...$$ 内部的 LaTeX 数学文本，输出 MathFormula/MathNode AST。
/// </summary>
public static class MathParser
{
    /// <summary>
    /// 入口：将 LaTeX 文本解析为 MathFormula。
    /// 若检测到 eqnarray 环境，则按环境解析；否则视为普通表达式。
    /// </summary>
    public static MathFormula Parse(string input)
    {
        input ??= string.Empty;
        var normalized = input.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return new MathFormula
            {
                Root = new MathSequence { Children = [] }
            };
        }

        // 特判 eqnarray 环境
        if (normalized.Contains("\\begin{eqnarray}", StringComparison.Ordinal))
        {
            var envNode = ParseEqnarrayEnvironment(normalized);
            return new MathFormula { Root = envNode };
        }

        var parser = new Parser(normalized);
        var root = parser.ParseExpression();
        return new MathFormula { Root = root };
    }

    /// <summary>
    /// 针对 \begin{eqnarray}...\end{eqnarray} 的专用解析逻辑：
    /// - 直接在原始文本上截取环境体
    /// - 然后复用通用环境解析逻辑 ParseEnvironmentBody
    /// </summary>
    private static MathEnvironment ParseEqnarrayEnvironment(string input)
    {
        const string envName = "eqnarray";
        const string beginTag = "\\begin{eqnarray}";
        const string endTag = "\\end{eqnarray}";

        var begin = input.IndexOf(beginTag, StringComparison.Ordinal);
        var end = input.IndexOf(endTag, StringComparison.Ordinal);
        if (begin < 0 || end < 0 || end <= begin)
        {
            // 防御性回退：按普通表达式解析整段
            var fallback = new Parser(input).ParseExpression();
            return new MathEnvironment
            {
                Name = envName,
                Rows =
                [
                    new MathRow { Cells = [fallback] }
                ]
            };
        }

        var bodyStart = begin + beginTag.Length;
        var body = input[bodyStart..end];

        var result = ParseEnvironmentBody(body, envName);
        return result;
    }

    /// <summary>
    /// 解析一个通用的多行环境体（不含 \begin/\end），支持 eqnarray/align/matrix/pmatrix 等：
    /// - 按 \\ 切行
    /// - 每行按 & 切列
    /// - 每个单元格单独用 Parser 解析为 MathNode
    /// </summary>
    private static MathEnvironment ParseEnvironmentBody(string body, string envName)
    {
        body = body.Replace("\r\n", "\n").Replace('\r', '\n');

        var rows = new List<MathRow>();

        // 支持两种换行写法：
        // 1) 标准 TeX: \\ 换行
        // 2) Markdown 风格：行尾单个 \ 加换行
        var rowBuffers = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\')
            {
                if (i + 1 < body.Length && body[i + 1] == '\\')
                {
                    rowBuffers.Add(sb.ToString());
                    sb.Clear();
                    i++; // 跳过第二个 '\'
                    continue;
                }
                if (i + 1 < body.Length && body[i + 1] == '\n')
                {
                    rowBuffers.Add(sb.ToString());
                    sb.Clear();
                    i++; // 跳过换行
                    continue;
                }
            }

            sb.Append(c);
        }
        if (sb.Length > 0 || rowBuffers.Count == 0)
            rowBuffers.Add(sb.ToString());

        // array 环境的首行通常是列格式说明，如 {ccc|c}，应跳过不当作数据行解析。
        if (envName == "array" && rowBuffers.Count > 0)
        {
            var first = rowBuffers[0].Trim();
            if (first.Length > 0 && first[0] == '{' && first.LastIndexOf('}') > 0)
            {
                rowBuffers.RemoveAt(0);
            }
        }

        foreach (var raw in rowBuffers)
        {
            var rowText = raw.Trim();
            if (rowText.Length == 0)
                continue;

            var cellTexts = rowText.Split('&');
            var cells = new List<MathNode>();
            foreach (var cellRaw in cellTexts)
            {
                var cell = cellRaw.Trim();
                if (cell.Length == 0)
                {
                    cells.Add(new MathSequence { Children = [] });
                    continue;
                }
                var parser = new Parser(cell);
                cells.Add(parser.ParseExpression());
            }

            if (cells.Count == 0)
                cells.Add(new MathSequence { Children = [] });

            rows.Add(new MathRow { Cells = cells });
        }

        if (rows.Count == 0)
        {
            rows.Add(new MathRow { Cells = [new MathSequence { Children = [] }] });
        }

        return new MathEnvironment
        {
            Name = envName,
            Rows = rows
        };
    }

    /// <summary>
    /// 内部递归下降解析器：处理单行/单表达式的 LaTeX 数学文本。
    /// </summary>
    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text)
        {
            _text = text ?? string.Empty;
            _pos = 0;
        }

        private bool End => _pos >= _text.Length;
        private char Current => _pos < _text.Length ? _text[_pos] : '\0';

        public MathNode ParseExpression()
        {
            var nodes = ParseSequence(stopChars: null);
            return nodes.Count switch
            {
                0 => new MathSequence { Children = [] },
                1 => nodes[0],
                _ => new MathSequence { Children = nodes }
            };
        }

        /// <summary>解析一串节点，直到遇到 stopChars 中的任意字符或输入结束。</summary>
        private List<MathNode> ParseSequence(HashSet<char>? stopChars)
        {
            var list = new List<MathNode>();
            while (!End)
            {
                SkipWhitespace();
                if (End)
                    break;

                if (stopChars != null && stopChars.Contains(Current))
                    break;

                var atom = ParseAtom();
                atom = ParseSupSub(atom);
                list.Add(atom);
            }
            return list;
        }

            /// <summary>
            /// 解析一个“原子”：
            /// - 分组 { ... }
            /// - 分数 \frac{...}{...}
            /// - 根号 \sqrt[...] { ... }
            /// - 重音命令 \hat{x}, \vec{v}, \widehat{\gamma}
            /// - 普通命令 \alpha, \sum, \cdots ...
            /// - 普通符号/标识符序列（其中 CJK 会自动视为“文本节点”，等价于隐式 \text）
            /// </summary>
        private MathNode ParseAtom()
        {
            SkipWhitespace();
            if (End)
                return new MathSequence { Children = [] };

            var ch = Current;

            // 花括号分组
            if (ch == '{')
            {
                _pos++; // consume '{'
                var inner = ParseSequence(stopChars: new HashSet<char> { '}' });
                if (!End && Current == '}')
                    _pos++; // consume '}'

                return inner.Count switch
                {
                    0 => new MathSequence { Children = [] },
                    1 => inner[0],
                    _ => new MathSequence { Children = inner }
                };
            }

            // 控制序列
            if (ch == '\\')
            {
                var command = ReadCommand();

                if (command == "text")
                    return ParseText();

                if (command == "frac")
                    return ParseFraction();
                if (command == "sqrt")
                    return ParseSqrt();
                if (IsAccentCommand(command))
                    return ParseAccent(command);
                if (command == "begin")
                    return ParseEnvironmentInline();

                var mapped = MapCommandToSymbol(command);
                // 映射为空字符串的命令（如 \left / \right 等“结构性命令”）在渲染层不直接生成符号，
                // 仅影响后续普通符号的解析方式，这里将其视为“空节点”跳过。
                if (string.IsNullOrEmpty(mapped))
                {
                    return new MathSequence { Children = [] };
                }
                return new MathSymbol { Text = mapped };
            }

            // 普通符号/文本序列
            var sb = new StringBuilder();
            bool first = true;
            bool isCjkSequence = false;
            while (!End)
            {
                var c = Current;
                // 注意：'[' 在这里可以作为普通字符，但 ']' 需要在 ParseSequence 的 stopChars
                // 机制下被识别为“结束标记”，因此不能被原子内部吞掉，否则像 \sqrt[3]{}
                // 这样的结构会把 ']' 也并入次数项文本。
                if (c == '\\' || c == '{' || c == '}' || c == '^' || c == '_' || c == '&' || c == ']')
                    break;
                if (char.IsWhiteSpace(c))
                    break;

                if (first)
                {
                    isCjkSequence = IsCjk(c);
                    first = false;
                }
                else if (isCjkSequence && !IsCjk(c))
                {
                    // 已经在解析 CJK 文本序列，遇到非 CJK 时终止，避免把数字/英文混进同一个文本节点
                    break;
                }

                sb.Append(c);
                _pos++;
            }

            if (sb.Length == 0)
            {
                // 单个特殊字符也作为 symbol
                sb.Append(Current);
                _pos++;
            }

            if (isCjkSequence)
            {
                // 裸露的 CJK 文本自动视为“隐式 \text”，用正文 Text 节点渲染
                return new MathText { Text = sb.ToString() };
            }

            return new MathSymbol { Text = sb.ToString() };
        }

        /// <summary>解析附着在 base 上的一串 ^ / _。</summary>
        private MathNode ParseSupSub(MathNode @base)
        {
            // firstScript 用于区分“同一层上的连续上下标”（如 x^a_b）
            // 与“对已经带上下标的整个原子再次施加上下标”（如 {x^2}^2）。
            bool firstScript = true;

            while (true)
            {
                SkipWhitespace();
                if (End)
                    break;

                bool isSup;
                if (Current == '^')
                    isSup = true;
                else if (Current == '_')
                    isSup = false;
                else
                    break;

                _pos++; // consume ^ or _
                SkipWhitespace();

                MathNode node;
                if (!End && Current == '{')
                {
                    _pos++; // '{'
                    var inner = ParseSequence(stopChars: new HashSet<char> { '}' });
                    if (!End && Current == '}')
                        _pos++; // '}'

                    node = inner.Count switch
                    {
                        0 => new MathSequence { Children = [] },
                        1 => inner[0],
                        _ => new MathSequence { Children = inner }
                    };
                }
                else
                {
                    // 与 TeX 保持一致：若未使用花括号，则 ^/_ 仅作用于“单个原子”：
                    // - 若后面是控制序列（如 \alpha），则整个命令作为一个原子
                    // - 否则仅取单个字符作为上下标，避免 "^2f" 被整体当作上标
                    if (!End && Current == '\\')
                    {
                        node = ParseAtom();
                    }
                    else if (!End)
                    {
                        var ch = Current;
                        _pos++;
                        node = new MathSymbol { Text = ch.ToString() };
                    }
                    else
                    {
                        node = new MathSequence { Children = [] };
                    }
                }

                if (@base is MathSupSub supSub)
                {
                    // 情况 A：这是本次 ParseSupSub 调用遇到的第一个 ^/_，
                    // 且 base 已经带有上下标（典型形态：{x^2}^2）。
                    // 这时新的 ^/_ 应该“作用在整个 {x^2} 上”，
                    // 因此需要把现有 supSub 当作一个整体再包一层。
                    if (firstScript && (supSub.Sup != null || supSub.Sub != null))
                    {
                        @base = new MathSupSub
                        {
                            Base = @base,
                            Sup = isSup ? node : null,
                            Sub = isSup ? null : node
                        };
                    }
                    else
                    {
                        // 情况 B：连续脚标/上标，如 x^a_b，后续的 _b
                        // 应该与前面的 ^a 共享同一个 Base，而不是再嵌套一层。
                        @base = new MathSupSub
                        {
                            Base = supSub.Base,
                            Sup = isSup ? node : supSub.Sup,
                            Sub = isSup ? supSub.Sub : node
                        };
                    }
                }
                else
                {
                    @base = new MathSupSub
                    {
                        Base = @base,
                        Sup = isSup ? node : null,
                        Sub = isSup ? null : node
                    };
                }

                firstScript = false;
            }

            return @base;
        }

        /// <summary>\frac{num}{den}</summary>
        private MathFraction ParseFraction()
        {
            var numerator = ParseRequiredGroup();
            var denominator = ParseRequiredGroup();
            return new MathFraction
            {
                Numerator = numerator,
                Denominator = denominator
            };
        }

        /// <summary>\sqrt[deg]{rad}</summary>
        private MathRoot ParseSqrt()
        {
            MathNode? degree = null;

            SkipWhitespace();
            if (!End && Current == '[')
            {
                _pos++; // '['
                var inner = ParseSequence(stopChars: new HashSet<char> { ']' });
                if (!End && Current == ']')
                    _pos++; // ']'

                degree = inner.Count switch
                {
                    0 => null,
                    1 => inner[0],
                    _ => new MathSequence { Children = inner }
                };
            }

            var radicand = ParseRequiredGroup();
            return new MathRoot
            {
                Degree = degree,
                Radicand = radicand
            };
        }

        /// <summary>重音命令，如 \hat{x}, \vec{v}, \widehat{\gamma}。</summary>
        private MathAccent ParseAccent(string command)
        {
            MathNode @base;
            SkipWhitespace();
            if (!End && Current == '{')
            {
                _pos++; // '{'
                var inner = ParseSequence(stopChars: new HashSet<char> { '}' });
                if (!End && Current == '}')
                    _pos++; // '}'

                @base = inner.Count switch
                {
                    0 => new MathSequence { Children = [] },
                    1 => inner[0],
                    _ => new MathSequence { Children = inner }
                };
            }
            else
            {
                @base = ParseAtom();
            }

            return new MathAccent
            {
                AccentName = command,
                Base = @base
            };
        }

        /// <summary>
        /// 解析行内环境 \begin{env} ... \end{env}，用于 align/matrix/pmatrix 等。
        /// 假定当前已经消费了 "\begin"，当前位置在 '{' 或其后。
        /// </summary>
        private MathEnvironment ParseEnvironmentInline()
        {
            SkipWhitespace();
            if (End || Current != '{')
            {
                // 容错：无法解析为环境时，当作普通符号处理
                return ParseEnvironmentBody(string.Empty, "begin");
            }

            _pos++; // consume '{'
            int nameStart = _pos;
            while (!End && _text[_pos] != '}')
                _pos++;
            if (End)
                return ParseEnvironmentBody(string.Empty, "begin");

            var envName = _text[nameStart.._pos];
            if (!End && Current == '}')
                _pos++; // consume '}'

            int bodyStart = _pos;
            var endTag = "\\end{" + envName + "}";
            int end = _text.IndexOf(endTag, _pos, StringComparison.Ordinal);
            if (end < 0)
            {
                // 找不到结束标签，退化为将剩余内容整体当作环境体
                end = _text.Length;
            }
            var body = _text[bodyStart..end];
            _pos = Math.Min(_text.Length, end + endTag.Length);

            return ParseEnvironmentBody(body, envName);
        }

        /// <summary>解析强制分组 { ... }，若缺失则回退为单个原子。</summary>
        private MathNode ParseRequiredGroup()
        {
            SkipWhitespace();
            if (!End && Current == '{')
            {
                _pos++; // '{'
                var inner = ParseSequence(stopChars: new HashSet<char> { '}' });
                if (!End && Current == '}')
                    _pos++; // '}'

                return inner.Count switch
                {
                    0 => new MathSequence { Children = [] },
                    1 => inner[0],
                    _ => new MathSequence { Children = inner }
                };
            }

            return ParseAtom();
        }

            /// <summary>\text{...}：将其中内容作为普通文本节点（不再做数学解析）。</summary>
            private MathNode ParseText()
            {
                SkipWhitespace();
                if (End || Current != '{')
                {
                    // 容错：缺少花括号时退化为普通符号 "\text"
                    return new MathSymbol { Text = "\\text" };
                }

                _pos++; // consume '{'
                var sb = new StringBuilder();
                int depth = 1;
                while (!End && depth > 0)
                {
                    var c = Current;
                    _pos++;

                    if (c == '\\')
                    {
                        // 处理简单转义：\\ \{ \}
                        if (!End)
                        {
                            var next = Current;
                            if (next is '\\' or '{' or '}')
                            {
                                sb.Append(next);
                                _pos++;
                                continue;
                            }
                        }
                        sb.Append('\\');
                        continue;
                    }

                    if (c == '{')
                    {
                        depth++;
                        sb.Append(c);
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                            break;
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                return new MathText { Text = sb.ToString() };
            }

        /// <summary>跳过空白字符（空格、Tab、换行）。</summary>
        private void SkipWhitespace()
        {
            while (!End && char.IsWhiteSpace(Current))
                _pos++;
        }

        /// <summary>
        /// 读取一个控制序列：
        /// - \alpha → "alpha"
        /// - \sum   → "sum"
        /// - \\     → "\\"（双反斜杠命令）
        /// </summary>
        private string ReadCommand()
        {
            if (Current != '\\')
                return string.Empty;

            _pos++; // consume '\'
            if (End)
                return string.Empty;

            // 特殊：\\
            if (_text[_pos] == '\\')
            {
                _pos++;
                return "\\\\";
            }

            var sb = new StringBuilder();
            while (!End)
            {
                var c = _text[_pos];
                // 只把 ASCII 字母当作命令名的一部分，避免把后面的中文一起吞进去：
                // \cdots梯度场 -> "cdots" + 后续单独解析，而不是 "cdots梯度场"
                if (!char.IsLetter(c) || c > 0x007F)
                    break;
                sb.Append(c);
                _pos++;
            }

            if (sb.Length == 0 && !End)
            {
                // 非字母控制符，如 \{ \} 等
                sb.Append(_text[_pos]);
                _pos++;
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 简单判断一个字符是否属于 CJK（中日韩）文字或常见假名/全角片假名，用于在数学模式中自动将裸露中文视为文本。
    /// </summary>
    private static bool IsCjk(char c) =>
        (c >= '\u3400' && c <= '\u9FFF')   // CJK 统一表意文字扩展
        || (c >= '\uF900' && c <= '\uFAFF') // CJK 兼容表意文字
        || (c >= '\u3040' && c <= '\u30FF') // 日文平假名/片假名
        || (c >= '\u31F0' && c <= '\u31FF') // 片假名扩展
        || (c >= '\uFF65' && c <= '\uFF9F'); // 半角片假名

    /// <summary>当前命令名是否为重音命令。</summary>
    private static bool IsAccentCommand(string name) =>
        name is "hat" or "widehat" or "tilde" or "widetilde" or "vec" or "bar";

    /// <summary>
    /// 将控制序列映射为可见符号字符串。
    /// 未知命令保留为 "\name" 的形式，方便布局/渲染层后续处理。
    /// </summary>
    private static string MapCommandToSymbol(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        // \left / \right 在当前实现中不做可变大小括号伸缩，仅保留后续紧跟的括号本身，
        // 因此命令本身映射为空字符串，由后续普通字符解析出实际括号符号。
        if (name is "left" or "right")
            return string.Empty;

        // 间距命令：统一映射为一个或多个空格，而不是把命令名原样输出。
        // - \,  通常表示细空格；这里折中为一个普通空格。
        // - \;  比 \, 稍宽，这里也用一个空格表示。
        // - \:  中等空格，同样折中处理。
        if (name is "," or ";" or ":")
            return " ";

        // 文字间距：\quad / \qquad 映射为若干空格。
        if (name == "quad")
            return "   ";
        if (name == "qquad")
            return "       ";

        // \mathbf：当前实现不支持独立的粗体数学字母族，这里将命令本身映射为空字符串，
        // 后续的 {A} / {x} / {v} 等内容仍按普通数学符号解析和渲染。
        if (name == "mathbf")
            return string.Empty;

        // 行分隔命令在普通表达式中一般不应出现，这里简化为换行符
        if (name == "\\\\")
            return "\n";

        if (CommandMap.TryGetValue(name, out var mapped))
            return mapped;

        return "\\" + name;
    }

    /// <summary>常用 LaTeX 数学命令到 Unicode 符号的映射。</summary>
    private static readonly Dictionary<string, string> CommandMap = new(StringComparer.Ordinal)
    {
        // 希腊字母（小写）
        ["alpha"] = "α",
        ["beta"] = "β",
        ["gamma"] = "γ",
        ["delta"] = "δ",
        ["epsilon"] = "ϵ",
        ["varepsilon"] = "ε",
        ["zeta"] = "ζ",
        ["eta"] = "η",
        ["theta"] = "θ",
        ["vartheta"] = "ϑ",
        ["iota"] = "ι",
        ["kappa"] = "κ",
        ["lambda"] = "λ",
        ["mu"] = "μ",
        ["nu"] = "ν",
        ["xi"] = "ξ",
        ["pi"] = "π",
        ["varpi"] = "ϖ",
        ["rho"] = "ρ",
        ["varrho"] = "ϱ",
        ["sigma"] = "σ",
        ["varsigma"] = "ς",
        ["tau"] = "τ",
        ["upsilon"] = "υ",
        ["phi"] = "ϕ",
        ["varphi"] = "φ",
        ["chi"] = "χ",
        ["psi"] = "ψ",
        ["omega"] = "ω",

        // 希腊字母（大写，常用子集）
        ["Gamma"] = "Γ",
        ["Delta"] = "Δ",
        ["Theta"] = "Θ",
        ["Lambda"] = "Λ",
        ["Xi"] = "Ξ",
        ["Pi"] = "Π",
        ["Sigma"] = "Σ",
        ["Upsilon"] = "Υ",
        ["Phi"] = "Φ",
        ["Psi"] = "Ψ",
        ["Omega"] = "Ω",

        // 省略号
        ["ldots"] = "…",
        ["cdots"] = "⋯",
        ["vdots"] = "⋮",
        ["ddots"] = "⋱",
        ["dots"] = "…",

        // 基本运算符
        ["times"] = "×",
        ["cdot"] = "·",
        ["pm"] = "±",
        ["mp"] = "∓",
        ["ast"] = "∗",
        ["star"] = "⋆",
        ["dagger"] = "†",
        ["ddagger"] = "‡",
        ["cap"] = "∩",
        ["cup"] = "∪",
        ["setminus"] = "∖",

        // 关系运算符
        ["leq"] = "≤",
        ["geq"] = "≥",
        ["le"] = "≤",
        ["ge"] = "≥",
        ["neq"] = "≠",
        ["approx"] = "≈",
        ["equiv"] = "≡",
        ["propto"] = "∝",
        ["in"] = "∈",
        ["ni"] = "∋",
        ["subset"] = "⊂",
        ["supset"] = "⊃",
        ["subseteq"] = "⊆",
        ["supseteq"] = "⊇",

        // 算子
        ["sum"] = "∑",
        ["prod"] = "∏",
        ["int"] = "∫",
        ["oint"] = "∮",
        ["nabla"] = "∇",
        ["partial"] = "∂",

        // 逻辑符号
        ["forall"] = "∀",
        ["exists"] = "∃",
        ["neg"] = "¬",
        ["land"] = "∧",
        ["lor"] = "∨",
        ["implies"] = "⇒",
        ["Rightarrow"] = "⇒",
        ["Leftarrow"] = "⇐",
        ["Leftrightarrow"] = "⇔",

        // 一些常见的“长”箭头/蕴含符号别名
        ["Longrightarrow"] = "⇒",
        ["Longleftarrow"] = "⇐",
        ["Longleftrightarrow"] = "⇔",
        ["longrightarrow"] = "→",
        ["longleftarrow"] = "←",

        // 箭头（子集）
        ["to"] = "→",
        ["rightarrow"] = "→",
        ["leftarrow"] = "←",
        ["uparrow"] = "↑",
        ["downarrow"] = "↓",
        ["leftrightarrow"] = "↔",

        // 其他常用符号
        ["infty"] = "∞",
        ["aleph"] = "ℵ",
        ["Re"] = "ℜ",
        ["Im"] = "ℑ",
        ["wp"] = "℘",
        ["ell"] = "ℓ",
        ["prime"] = "′",
        ["emptyset"] = "∅",

        // 常见算子名，按直立文本形式渲染
        ["det"] = "det"
    };
}


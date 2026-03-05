# MarkdownEditor 数学公式使用说明

本说明基于编辑器内置的 LaTeX 数学子集与 Skia 渲染引擎，重点介绍：

- 如何在 Markdown 中书写数学公式；
- 支持哪些常见的 LaTeX 语法（特别是矩阵、多行公式）；
- 如何配置与使用 Latin Modern Math 等数学字体。

---

## 1. 行内公式与块级公式

- **行内公式**：使用单个 `$...$` 包裹，例如：

  ```markdown
  质能守恒方程可以写成 $E = mc^2$。
  ```

- **块级公式**：使用一对 `$$...$$`，单行或多行均可，例如：

  ```markdown
  $$
  \sum_{i=1}^n a_i = 0
  $$
  ```

  或者：

  ```markdown
  $$\int_0^{+\infty} e^{-x^2}\,dx = \frac{\sqrt{\pi}}{2}$$
  ```

> 说明：语法与常见 MathJax/LaTeX 用法基本一致，当前实现不支持完整的 LaTeX 语法，而是一个“足够常用”的数学子集。

---

## 2. 常用结构与矩阵环境

编辑器内置的解析与渲染支持以下常用结构：

- 上下标：`x_i^2`、`x_{i+1}^{(2)}`；
- 分数：`\frac{a}{b}`、`\dfrac{a}{b}`（显示效果相同，均使用显示样式）；
- 根号：`\sqrt{x^2+1}`、`\sqrt[3]{x^2 + y^2}`；
- 大算子：`\sum`、`\prod`、`\int`、`\oint`，在块级公式下会上下堆叠上下限；
- 希腊字母与常用运算符（` \alpha, \beta, \gamma, \infty, \leq, \geq, \neq, \cdots, \vdots, \ddots` 等）。

矩阵与多行环境支持：

- 无括号矩阵：`matrix`；
- 圆括号矩阵：`pmatrix`；
- 方括号矩阵：`bmatrix`；
- 花括号矩阵：`Bmatrix`；
- 单/双竖线矩阵：`vmatrix`、`Vmatrix`；
- 多行等式环境：`eqnarray`。

示例：

```markdown
$$
\mathbf{A} = \begin{bmatrix}
a_{11} & a_{12} & \cdots & a_{1n} \\\\
a_{21} & a_{22} & \cdots & a_{2n} \\\\
\vdots & \vdots & \ddots & \vdots \\\\
a_{m1} & a_{m2} & \cdots & a_{mn}
\end{bmatrix}_{m \times n}
$$
```

> 提示：`\begin{matrix}...\end{matrix}` 这类环境内部使用 `&` 分列、`\\\\` 或行尾 `\`+换行分行，行为与 LaTeX 基本一致。

---

## 3. \left / \right 与括号

为了兼容常见 LaTeX 写法，解析器支持 `\left` / `\right` 命令，但当前实现不会根据内容高度自动伸缩括号，而是：

- 忽略命令本身，仅绘制后面的括号符号；
- 仍然可以用在增广矩阵等场景中，视觉效果与固定大小括号接近。

示例：

```markdown
$$
\left[\begin{array}{ccc|c}
1 & 2 & 3 & 4 \\\\
5 & 6 & 7 & 8
\end{array}\right]
$$
```

---

## 4. CJK 与 LaTeX 混排

编辑器的数学解析会自动将“裸露的中文 / 日文 / 全角字符”视作普通文本，使用正文字体渲染，例如：

```markdown
$$
\vec\nabla \times (\vec\nabla f) = 0 \cdots\cdots 梯度场必是无旋场
$$
```

- 公式中的 `\vec\nabla`、运算符等用数学字体（如 Latin Modern Math）渲染；
- 句尾的“梯度场必是无旋场”会使用正文 CJK 字体，保证阅读体验。

如需在数学模式中明确插入一段说明文字，也可以使用 `\text{...}`：

```markdown
$$
f(x) = x^2, \quad \text{这是一个简单示例}
$$
```

---

## 5. 数学字体配置（Latin Modern Math）

数学渲染使用 SkiaSharp 字体系统，具体行为由 `EngineConfig` 控制。核心字段有：

- `BodyFontFamily`：正文字体列表；
- `CodeFontFamily`：代码字体列表；
- `MathFontFamily`：数学字体候选列表（默认包含 `Latin Modern Math, STIX Two Math, Cambria Math, Times New Roman`）；
- `MathFontFilePath`：可选的数学字体文件路径（OTF/TTF）。

加载顺序为：

1. 若设置了 `MathFontFilePath` 且文件存在，则优先通过 `SKTypeface.FromFile` 加载该字体（推荐指向 Latin Modern Math 的 OTF 文件）；  
2. 否则按 `MathFontFamily` 中的家族名顺序（如 `Latin Modern Math`、`STIX Two Math` 等）尝试从系统已安装字体中加载；  
3. 若仍失败，则回退到正文字体家族。

> 若希望在所有机器上都得到一致的数学排版效果，建议：
>
> 1. 在工程中随应用一起分发 Latin Modern Math 的 OTF 字体文件（遵循 GUST Font License / LPPL 许可）；  
> 2. 在运行时配置 `MathFontFilePath` 指向该 OTF 文件的实际路径；  
> 3. 或在系统层面安装 Latin Modern Math 字体，并确保 `MathFontFamily` 包含 `"Latin Modern Math"`。

---

## 6. 推荐的测试文档

项目中提供了两份与数学相关的示例/回归文档：

- `Sample_Math.md`：集中展示了分数、矩阵、向量、线性方程组等常见结构的写法；  
- `Sample_Math_Regression.md`：专门用于回归测试数学渲染，包含矩阵、增广矩阵、`eqnarray` 多行公式、表格中的行内公式等。

每次修改数学解析 / 布局 / 渲染或更换数学字体后，建议打开这两份文档，手动检查：

- 复杂矩阵和多行环境的对齐情况；  
- 上下标、根号、大算子的排版是否自然；  
- 中英文混排与 CJK 文本是否清晰可读；  
- 表格和列表中的行内公式是否被截断或挤压。


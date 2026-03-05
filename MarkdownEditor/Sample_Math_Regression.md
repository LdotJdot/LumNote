# 数学公式回归测试文档
$$
\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{2}}}}}} = \frac{\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{2}}}}}}}{\frac{2}{3}}
$$


$ 1+\sqrt{4} $


$\sqrt[{\sqrt{3}^2}]{x^2 + y^2}$
> 本文档用于回归测试编辑器内置 LaTeX 数学渲染（含 Latin Modern Math 字体）。  
> 建议在每次修改数学解析/布局/渲染相关代码后，打开本文件查看整体效果。

---

## 1. 基本行内 / 块级公式

行内公式示例：质能方程 $E = mc^2$，以及含上下标的 $x_i^2 + y_j^2$。

块级公式示例：

$$
E = mc^2
$$

带求和、积分、大算子：

$$
\sum_{i=1}^n a_i = 0, \qquad
\int_{0}^{+\infty} e^{-x^2}\,dx = \frac{\sqrt{\pi}}{2}
$$

---

## 2. 分数与根号

简单分数：

$$
\frac{a}{b}, \quad \frac{\partial f}{\partial x}
$$

嵌套分数与根号：

$$
\frac{1}{1 + \frac{1}{x}}, \qquad
\sqrt{x^2 + 1}, \qquad
\sqrt[3]{x^2 + y^2}
$$

---

## 3. 2×2 / 3×3 矩阵与多行结构

无括号矩阵：

$$
\begin{matrix}
a & b \\\\
c & d
\end{matrix}
$$

圆括号矩阵（pmatrix）：

$$
\begin{pmatrix}
a & b \\\\
c & d
\end{pmatrix}
$$

方括号矩阵（bmatrix）：

$$
\begin{bmatrix}
a & b \\\\
c & d
\end{bmatrix}
$$

3×3 矩阵：

$$
\mathbf{A} = \begin{bmatrix}
a_{11} & a_{12} & a_{13} \\\\
a_{21} & a_{22} & a_{23} \\\\
a_{31} & a_{32} & a_{33}
\end{bmatrix}
$$

带省略号的 $m \times n$ 通用矩阵：

$$
\mathbf{A} = \begin{bmatrix}
a_{11} & a_{12} & \cdots & a_{1n} \\\\
a_{21} & a_{22} & \cdots & a_{2n} \\\\
\vdots & \vdots & \ddots & \vdots \\\\
a_{m1} & a_{m2} & \cdots & a_{mn}
\end{bmatrix}_{m \times n}
$$

---

## 4. 增广矩阵与 \left / \right

测试 `\left` / `\right` 与增广矩阵的对齐与括号渲染：

$$
\left[\begin{array}{ccc|c}
1 & 2 & 3 & 4 \\\\
5 & 6 & 7 & 8 \\\\
9 & 10 & 11 & 12
\end{array}\right]
$$

---

## 5. 线性方程组与向量

线性方程组：

$$
\begin{cases}
a_{11}x_1 + a_{12}x_2 + a_{13}x_3 = b_1 \\\\
a_{21}x_1 + a_{22}x_2 + a_{23}x_3 = b_2 \\\\
a_{31}x_1 + a_{32}x_2 + a_{33}x_3 = b_3
\end{cases}
$$

向量与列向量：

$$
\mathbf{x} = \begin{bmatrix} x_1 \\\\ x_2 \\\\ x_3 \end{bmatrix}, \qquad
\mathbf{v} = \begin{bmatrix} x & y & z \end{bmatrix}
$$

---

## 6. 梯度 / 散度 / 旋度（含中文说明）

下面的例子与 `Sample.md` 中的 MathJax 示例一致，用于测试 CJK 与 LaTeX 混排：

$$
\begin{eqnarray}
\vec\nabla \times (\vec\nabla f) & = & 0  \cdots\cdots 梯度场必是无旋场 \\\\
\vec\nabla \cdot(\vec\nabla \times \vec F) & = & 0 \cdots\cdots 旋度场必是无散场 \\\\
\vec\nabla \cdot (\vec\nabla f) & = & {\vec\nabla}^2 f \\\\
\vec\nabla \times(\vec\nabla \times \vec F) & = & \vec\nabla(\vec\nabla \cdot \vec F) - {\vec\nabla}^2 \vec F
\end{eqnarray}
$$

请特别检查：

- CJK 文本（“梯度场必是无旋场” 等）是否使用正文字体、与周围公式对齐良好；  
- 箭头、nabla、上标/下标在 Latin Modern Math 下的形状与间距是否自然。

---

## 7. 表格中的公式

表格内行内公式用于测试单元格布局中的 `RunStyle.Math` 与宽度测量：

| 描述           | 公式                         |
| -------------- | ---------------------------- |
| 行内求和       | $\sum_{i=1}^n a_i$           |
| 行内分数       | $\frac{a}{b}$                |
| 行内向量与点积 | $\vec{x} \cdot \vec{y} = 0$  |

请确认：

- 含公式的列宽不会被错误压缩或截断；  
- 多行表格在垂直方向上行高足够，公式不会被裁切。


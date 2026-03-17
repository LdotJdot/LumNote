# MathLaTeX 公式速查手册12334

$\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{2}}}}}} = \frac{\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{\sqrt{2}}}}}}}{\frac{2}{3}}$

${{{x^{2^2}}^2}^2}^2$

$3+ \sqrt{{\sqrt[1+{\sqrt{3}^2}]{x^2 + y^2}}^2}-1$

${{{x^2}^2}^2}^2$

## 一、分数 (Fractions)123321axzzzzzzzz

### 行内分数
这是行内分数 $a/b$ 或 $\frac{a}{b}$ 的示例。

### 独立显示分数
$$
\frac{a}{b}
$$

### 复杂分数
$$
\frac{\partial f}{\partial x} = \frac{x^2 + y^2}{2xy} \quad \text{或} \quad \frac{1}{1+\frac{1}{x}}
$$

---

## 二、矩阵 (Matrices)

### 1. 无括号矩阵 (matrix)
$$
\begin{matrix}
a & b \\
c & d
\end{matrix}
$$

### 2. 圆括号矩阵 (pmatrix)
$$
\begin{pmatrix}
a & b \\
c & d
\end{pmatrix}
$$

### 3. 方括号矩阵 (bmatrix) ⭐最常用
$$
\begin{bmatrix}
a & b \\
c & d
\end{bmatrix}
$$

### 4. 花括号矩阵 (Bmatrix)
$$
\begin{Bmatrix}
a & b \\
c & d
\end{Bmatrix}
$$

### 5. 行列式 (vmatrix)
$$
\begin{vmatrix}
a & b \\
c & d
\end{vmatrix}
$$

### 6. 双竖线矩阵 (Vmatrix)
$$
\begin{Vmatrix}
a & b \\
c & d
\end{Vmatrix}
$$

---

## 三、复杂矩阵示例

### 3×3 矩阵
$$
\mathbf{A} = \begin{bmatrix}
a_{11} & a_{12} & a_{13} \\
a_{21} & a_{22} & a_{23} \\
a_{31} & a_{32} & a_{33}
\end{bmatrix}
$$

### 增广矩阵
$$
\left[\begin{array}{ccc|c}
1 & 2 & 3 & 4 \\
5 & 6 & 7 & 8 \\
9 & 10 & 11 & 12
\end{array}\right]
$$

### 分块矩阵
$$
\begin{bmatrix}
\mathbf{A}_{n \times n} & \mathbf{B} \\
\mathbf{0} & \mathbf{C}_{m \times m}
\end{bmatrix}
$$

### 对角矩阵
$$
\mathbf{\Lambda} = \text{diag}(\lambda_1, \lambda_2, \lambda_3) = 
\begin{bmatrix}
\lambda_1 & 0 & 0 \\
0 & \lambda_2 & 0 \\
0 & 0 & \lambda_3
\end{bmatrix}
$$

### 带省略号的通用矩阵
$$
\mathbf{A} = \begin{bmatrix}
a_{11} & a_{12} & \cdots & a_{1n} \\
a_{21} & a_{22} & \cdots & a_{2n} \\
\vdots & \vdots & \ddots & \vdots \\
a_{m1} & a_{m2} & \cdots & a_{mn}
\end{bmatrix}_{m \times n}
$$

---

## 四、向量 (Vectors)

### 行向量
$$
\mathbf{v} = \begin{bmatrix} x & y & z \end{bmatrix}
$$

### 列向量
$$
\mathbf{v} = \begin{bmatrix} x \\ y \\ z \end{bmatrix}
$$

---

## 五、常用符号速查

| 符号       | LaTeX           | 效果               |
| ---------- | --------------- | ------------------ |
| 分数       | `\frac{a}{b}`   | $\frac{a}{b}$      |
| 大分数     | `\dfrac{a}{b}`  | $\dfrac{a}{b}$     |
| 水平省略号 | `\cdots`        | $\cdots$           |
| 垂直省略号 | `\vdots`        | $\vdots$           |
| 对角省略号 | `\ddots`        | $\ddots$           |
| 转置       | `^T` 或 `^\top` | $\mathbf{A}^T$     |
| 逆矩阵     | `^{-1}`         | $\mathbf{A}^{-1}$  |
| 行列式     | `\det`          | $\det(\mathbf{A})$ |
| 单位矩阵   | `\mathbf{I}`    | $\mathbf{I}$       |

---

## 六、综合示例：线性方程组

$$
\begin{cases}
a_{11}x_1 + a_{12}x_2 + a_{13}x_3 = b_1 \\
a_{21}x_1 + a_{22}x_2 + a_{23}x_3 = b_2 \\
a_{31}x_1 + a_{32}x_2 + a_{33}x_3 = b_3
\end{cases}
\quad \Longleftrightarrow \quad
\mathbf{A}\mathbf{x} = \mathbf{b}
$$

其中：
$$
\mathbf{A} = \begin{bmatrix}
a_{11} & a_{12} & a_{13} \\
a_{21} & a_{22} & a_{23} \\
a_{31} & a_{32} & a_{33}
\end{bmatrix}, \quad
\mathbf{x} = \begin{bmatrix} x_1 \\ x_2 \\ x_3 \end{bmatrix}, \quad
\mathbf{b} = \begin{bmatrix} b_1 \\ b_2 \\ b_3 \end{bmatrix}
$$

---

## 七、源码参考

```latex
% 分数
\frac{分子}{分母}

% 矩阵环境选择
\begin{matrix} ... \end{matrix}      % 无括号
\begin{pmatrix} ... \end{pmatrix}    % 圆括号 ()
\begin{bmatrix} ... \end{bmatrix}    % 方括号 []
\begin{Bmatrix} ... \end{Bmatrix}    % 花括号 {}
\begin{vmatrix} ... \end{vmatrix}    % 单竖线 ||
\begin{Vmatrix} ... \end{Vmatrix}    % 双竖线 ||||

% 矩阵内部
&   % 列分隔
\\  % 换行

% 增广矩阵（使用 array 环境）
\left[\begin{array}{cc|c} ... \end{array}\right]

$ 表示行内公式： 
 
质能守恒方程可以用一个很简洁的方程式 $E=mc^2$ 来表达。
 
$$ 表示整行公式：
 
$$\sum_{i=1}^n a_i=0$$
 
$$f(x_1,x_x,\ldots,x_n) = x_1^2 + x_2^2 + \cdots + x_n^2 $$
 
$$\sum^{j-1}_{k=0}{\widehat{\gamma}_{kj} z_k}$$
 
更复杂的公式:
$$
\begin{eqnarray}
\vec\nabla \times (\vec\nabla f) & = & 0  \cdots\cdots梯度场必是无旋场\\
\vec\nabla \cdot(\vec\nabla \times \vec F) & = & 0\cdots\cdots旋度场必是无散场\\
\vec\nabla \cdot (\vec\nabla f) & = & {\vec\nabla}^2f\\
\vec\nabla \times(\vec\nabla \times \vec F) & = & \vec\nabla(\vec\nabla \cdot \vec F) - {\vec\nabla}^2 \vec F\\
\end{eqnarray}
$$
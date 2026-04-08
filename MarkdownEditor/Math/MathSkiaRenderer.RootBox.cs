using System;
using SkiaSharp;

namespace MarkdownEditor.Latex;

public static partial class MathSkiaRenderer
{
    /// <summary>
    /// 根号盒子：完全用几何路径绘制“脚 + 斜线 + 横线”，不再依赖字体中的 √ glyph，
    /// 以避免在不同数学字体下钩子与横线脱节的问题。
    /// </summary>
    private sealed class RootBox : Box
    {
        /// <summary>
        /// 统一的根号几何度量结果：所有“魔法数”和几何关系都集中到一次计算中，
        /// Box 的 Height/Depth/Width 与实际绘制路径严格对应。
        /// baseline 约定为 0，所有 Y 坐标均相对基线。
        /// </summary>
        private readonly struct RadicalMetrics
        {
            public readonly float BarGap; // 横线到被开方内容顶部的间距
            public readonly float SlantRun; // 从横线左端到被开方内容左缘的水平距离
            public readonly float StrokeWidth; // 斜线/横线的线宽
            public readonly float StrokeHalf; // 线宽一半，方便做安全扩展
            public readonly float LeftPad; // 整个根号在 xStart 左侧预留的空白，避免钩子侵入左侧内容

            public readonly float DxMain;
            public readonly float DyMain;
            public readonly float DxHook;
            public readonly float DyHook;

            public readonly float BarYOffset; // baseline 为 0 时，横线的 Y 坐标（通常为负）

            public readonly float Height; // Box 基线以上高度（包含线宽）
            public readonly float Depth; // Box 基线以下深度（包含线宽）

            public RadicalMetrics(
                float barGap,
                float slantRun,
                float strokeWidth,
                float strokeHalf,
                float leftPad,
                float dxMain,
                float dyMain,
                float dxHook,
                float dyHook,
                float barYOffset,
                float height,
                float depth
            )
            {
                BarGap = barGap;
                SlantRun = slantRun;
                StrokeWidth = strokeWidth;
                StrokeHalf = strokeHalf;
                LeftPad = leftPad;
                DxMain = dxMain;
                DyMain = dyMain;
                DxHook = dxHook;
                DyHook = dyHook;
                BarYOffset = barYOffset;
                Height = height;
                Depth = depth;
            }
        }

        private readonly Box? _degree;
        private readonly Box _radicand;
        private readonly SKFont _radicalFont;
        private readonly SKPaint _paint;
        private readonly RadicalMetrics _metrics;

        // 根指数在 RootBox 内部的水平偏移与基线偏移（相对于整体基线）。
        private readonly float _degreeOffsetX;
        private readonly float _degreeBaselineOffset;
        // 若根指数向左溢出根号几何形状，则将整个内容在 Box 内部整体右移，
        // 保证 Box 的 xStart 始终是“最左边”的位置，Width 覆盖所有内容。
        private readonly float _contentOffsetX;

        public RootBox(Box? degree, Box radicand, SKFont radicalFont, SKPaint paint)
        {
            _degree = degree;
            _radicand = radicand;
            _radicalFont = radicalFont;
            _paint = paint;

            bool wrapsInnerRoot = radicand is RootBox;
            _metrics = ComputeRadicalMetrics(radicand, radicalFont, wrapsInnerRoot);

            // 根据根指数盒子计算其在 RootBox 内部的相对位置：
            // - 水平：锚点选在“第二段斜线”（主对角线）靠近横线的一小段上，使根指数略微侵入根号矩形内部；
            // - 垂直：完全位于横线之上，底部距横线约 0.05em。
            _degreeOffsetX = 0;
            _degreeBaselineOffset = 0;
            _contentOffsetX = 0;
            if (_degree != null)
            {
                float em = _radicalFont.Size;
                // baseline=0 坐标系下的横线 Y：
                float barY = _metrics.BarYOffset;

                // 在 baseline=0 坐标系下重建根号三点：
                float xBarStartLocal = _metrics.LeftPad;
                float xP2Local = xBarStartLocal;
                float yP2Local = barY;
                float xP1Local = xP2Local + _metrics.DxMain;
                float yP1Local = yP2Local + _metrics.DyMain;

                // 主对角线 P1→P2：选取靠近横线一侧的大约 1/4 处作为“连接点”，
                // 该点将作为根指数盒子的“右下角锚点”，保证锚点不动时整体平移不会改变
                // 与斜线的几何关系。
                float t = 0.25f;
                float anchorXOnDiag = xP2Local + t * (xP1Local - xP2Local);
                float anchorYOnDiag = yP2Local + t * (yP1Local - yP2Local);

                // 垂直方向：将根指数盒子的“右下角”精确锚定在该点：
                // anchorY = baseline(次数) + Depth(次数)，解出基线偏移。
                _degreeBaselineOffset = anchorYOnDiag - _degree.Depth;

                // 水平方向：同理，将根指数盒子的“右下角 X 坐标”锚定在该点：
                // anchorX = xStart + _contentOffsetX + _degreeOffsetX + Width(次数)，
                // 在本地坐标下去掉 xStart/_contentOffsetX。
                _degreeOffsetX = anchorXOnDiag - _degree.Width;
            }

            // 根号几何本体在 Box 内部的 X 范围：[0, geoRight]
            float geoLeft = 0f;
            float geoRight = _metrics.LeftPad + _metrics.SlantRun + _radicand.Width;

            // 若存在根指数，则它的 X 范围为 [_degreeOffsetX, _degreeOffsetX + _degree.Width]。
            float leftMost = geoLeft;
            float rightMost = geoRight;
            if (_degree != null)
            {
                leftMost = Math.Min(leftMost, _degreeOffsetX);
                rightMost = Math.Max(rightMost, _degreeOffsetX + _degree.Width);
            }

            // 若根指数向左溢出（leftMost < 0），则整体内容在 Box 内部右移 -leftMost，
            // 保证 Box 的 xStart 始终是“所有内容中最左的那个 X”。
            _contentOffsetX = -leftMost;
            Width = rightMost - leftMost;

            // 高度/深度：以几何根号本身为基础，再并入根指数盒子的包围盒，
            // 这样在多层嵌套时，外层行高不会裁剪掉高阶/复杂的次数项。
            float above = _metrics.Height;
            float below = _metrics.Depth;
            if (_degree != null)
            {
                // baseline = 0 时，根指数基线位于 _degreeBaselineOffset，
                // 顶部/底部坐标分别为：
                float degreeTop = _degreeBaselineOffset - _degree.Height;
                float degreeBottom = _degreeBaselineOffset + _degree.Depth;

                // 需要的“基线以上高度/以下深度”是根号本体和根指数两者的最大值。
                above = Math.Max(above, -degreeTop);
                below = Math.Max(below, degreeBottom);
            }
            Height = above;
            Depth = below;
        }

        public override void Draw(SKCanvas canvas, float xStart, float baselineY)
        {
            using var radicalPaint = new SKPaint
            {
                Color = _paint.Color,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = _metrics.StrokeWidth
            };

            // 被开方内容顶部与根号横线位置
            var radicandTop = baselineY - _radicand.Height;
            var barY = baselineY + _metrics.BarYOffset;

            // 可选的根指数：其位置完全由 _contentOffsetX / _degreeOffsetX / _degreeBaselineOffset 决定，
            // 不再参与根号路径的起点计算。
            float offsetXRoot = xStart + _contentOffsetX + _metrics.LeftPad;
            if (_degree != null)
            {
                var degreeBaseline = baselineY + _degreeBaselineOffset;
                _degree.Draw(canvas, xStart + _contentOffsetX + _degreeOffsetX, degreeBaseline);
            }

            // 计算几何根号路径：
            // 只用三段线段：顶部横线、主对角线（接近 (-1, -1) 但稍微“竖直一些”）、小钩子（接近 (-1, 1)）。
            // 长度基于 em 和被开方内容高度自动调整，这样多层嵌套时比例一致。
            float xBarStart = offsetXRoot; // 顶部横线左端
            float contentWidth = _radicand.Width;
            // 顶部横线右端：覆盖整个被开方盒子（如果被开方本身是根号，则连同其钩子+横线一起视为一个整体）。
            float xRadicand = xBarStart + _metrics.SlantRun;
            float xBarEnd = xRadicand + contentWidth;

            // 三个关键点：P0(钩子尖) → P1(折点) → P2(横线左端)
            float xP2 = xBarStart;
            float yP2 = barY;
            float xP1 = xP2 + _metrics.DxMain;
            float yP1 = yP2 + _metrics.DyMain;
            float xP0 = xP1 + _metrics.DxHook;
            float yP0 = yP1 + _metrics.DyHook;

            using (var path = new SKPath())
            {
                // 根号路径：先画小钩子，再接主对角线，最后接到顶部横线左端。
                path.MoveTo(xP0, yP0);
                path.LineTo(xP1, yP1);
                path.LineTo(xP2, yP2);
                canvas.DrawPath(path, radicalPaint);
            }

            // 顶部横线：从左端起点一直覆盖到被开方内容右边，略多出一点
            canvas.DrawLine(xBarStart, barY, xBarEnd, barY, radicalPaint);

            // 绘制被开方内容，与横线之间留少量水平间距；右边则基本与横线右端对齐
            var radicandBaseline = baselineY;
            _radicand.Draw(canvas, xRadicand, radicandBaseline);
        }

        /// <summary>
        /// 统一计算根号的几何包围框以及绘制向量。
        /// 约定 baseline 为 0，所有 Y 坐标均相对基线。
        /// </summary>
        private static RadicalMetrics ComputeRadicalMetrics(
            Box radicand,
            SKFont radicalFont,
            bool wrapsInnerRoot
        )
        {
            float em = radicalFont.Size;
            float barGap = em * 0.08f;

            float strokeWidth = Math.Max(1f, em * 0.06f);
            float strokeHalf = strokeWidth * 0.5f;

            // baseline = 0 时，被开方内容顶部与横线位置
            float radicandTop = -radicand.Height;
            float barY = radicandTop - barGap;

            // 目标：主对角线的最低点（折点附近）只略微低于被开方内容基线，
            // 而不是远远垂到下面。这里直接根据“需要向下延伸多少”反推 mainLen，
            // 而不是简单按整体高度放大。
            float targetBelowMain = Math.Max(radicand.Depth, em * 0.20f);
            // barY 是横线的 Y（通常为负），baseline = 0，所以需要的竖直位移为：
            float desiredDyMain = targetBelowMain - barY;

            // 主对角线长度：至少 ~0.8em，同时满足竖直分量接近 desiredDyMain。
            float minMainLen = wrapsInnerRoot ? em * 0.78f : em * 0.85f;
            float mainLen = Math.Max(minMainLen, desiredDyMain / 0.93f);

            // 钩子比例：外层略小，内层略大，但“形状”（方向）完全一致，避免视觉上出现两套风格。
            float hookScaleInner = 0.11f;
            float hookScaleOuter = 0.08f;
            float hookScale = wrapsInnerRoot ? hookScaleOuter : hookScaleInner;
            float hookLen = mainLen * hookScale;

            // 在 Skia 坐标下构造向量：整套根号统一使用一套角度，
            // 内外层仅在长度比例上有所区别，避免最内层钩子“另起一套风格”。
            float dxMain = -mainLen * 0.38f;
            float dyMain = mainLen * 0.93f;
            float dxHook = -hookLen * 0.52f;
            float dyHook = -hookLen * 0.86f;

            // 顶点 Y 坐标（baseline = 0）
            float y2 = barY;
            float y1 = y2 + dyMain;
            float y0 = y1 + dyHook;

            float minY = Math.Min(y0, Math.Min(y1, y2));
            float maxY = Math.Max(y0, Math.Max(y1, y2));
            maxY = Math.Max(maxY, 0); // baseline 也参与深度比较

            // 加上线宽的安全空间
            float minYWithStroke = minY - strokeHalf;
            float maxYWithStroke = maxY + strokeHalf;

            float radicalAbove = -minYWithStroke;
            float radicalBelow = maxYWithStroke;

            float height = Math.Max(radicand.Height, radicalAbove);
            float depth = Math.Max(radicand.Depth, radicalBelow);

            // 钩子向左突出的水平距离；保证不会画到 xStart 左边。
            float hookLeft = dxMain + dxHook; // 相对于横线左端（xBarStart）
            float leftPad = 0;
            if (hookLeft < 0)
                leftPad = -hookLeft + strokeHalf;

            // 顶部横线预留一段长度给根号形状（主对角线 + 钩子），再接被开方整体内容。
            // 使用与字号相关的固定比例，使最内层与外层根号形状一致。
            float slantRun = em * 0.40f + strokeHalf;

            return new RadicalMetrics(
                barGap,
                slantRun,
                strokeWidth,
                strokeHalf,
                leftPad,
                dxMain,
                dyMain,
                dxHook,
                dyHook,
                barY,
                height,
                depth
            );
        }
    }
}

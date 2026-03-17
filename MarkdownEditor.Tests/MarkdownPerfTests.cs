using System;
using System.Linq;
using MarkdownEditor.Core;
using MarkdownEditor.Latex;
using MarkdownEditor.ViewModels;
using Xunit;
using SkiaSharp;

namespace MarkdownEditor.Tests;

public class MarkdownPerfTests
{
    [Fact]
    public void NormalizeOrderedLists_ShouldNotChangeSimpleContent()
    {
        var vmType = typeof(MainViewModel);
        var method = vmType.GetMethod("NormalizeOrderedLists", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        string input = "1. a\n2. b\n\nParagraph";
        var result = (string?)method!.Invoke(null, new object?[] { input });
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeFormulaSource_ShouldFlattenNewlines()
    {
        var type = typeof(MathSkiaRenderer);
        var method = type.GetMethod("NormalizeFormulaSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        string input = "\n +123 \n";
        var result = (string?)method!.Invoke(null, new object?[] { input });
        Assert.Equal("+123", result);
    }

    [Fact]
    public void MathSkiaRenderer_ShouldCacheParseResults()
    {
        var body = SKTypeface.Default;
        var math = body;
        const string latex = "x^2 + y^2";

        var m1 = MathSkiaRenderer.MeasureFormula(latex, body, math, 16);
        var m2 = MathSkiaRenderer.MeasureFormula(latex, body, math, 16);

        Assert.Equal(m1.width, m2.width);
        Assert.Equal(m1.height, m2.height);
        Assert.Equal(m1.depth, m2.depth);
    }
}


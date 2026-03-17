# AOT 发布体积说明
123
## 体积来源321
321
1. **Avalonia + Skia 等**  
   空白 Avalonia AOT 应用约 21 MB（libSkiaSharp、libHarfBuzzSharp、av_libglesv2 等），属正常范围。

2. **Avalonia.Fonts.Inter**  
   若不需要嵌入 Inter 字体，可移除该包，约减 3 MB。

## 当前优化（已做）

- `OptimizationPreference=Size`、`InvariantGlobalization=true`、Release 不嵌入调试符号。

## 发布命令

```bash
dotnet publish MarkdownEditor/MarkdownEditor.csproj -c Release -r win-x64 --self-contained
```

## 可选：进一步减体积

- 移除 **Avalonia.Fonts.Inter**（约减 3 MB）。
- **TrimMode=full** 可再减一点，需充分测试。

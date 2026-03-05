# Markdown Editor

基于 AvaloniaUI 的跨平台 Markdown 文档编辑管理器，使用 .NET 10，100% C# 实现，支持 Native AOT 发布。

## 功能特性

- **实时 Markdown 渲染**：50ms 防抖，输入即预览
- **可选中预览**：右侧预览文本支持选中复制（SelectableTextBlock）
- **表格渲染**：支持 GFM 表格、列对齐（左/中/右）
- **可点击超链接**：小手光标，点击在默认浏览器打开
- **可调整布局**：左右拖拽分割线调整文档列表、编辑器、预览区域宽度
- **风格配置**：界面与 Markdown 样式通过 `~/.markdown-editor/config.json` 配置
- **文档搜索**：按文件名、路径过滤文档列表
- **GitHub 风格格式**：标题、加粗、斜体、删除线、代码块、表格、任务列表、引用、缩进代码块等
- **跨平台**：Windows、Linux、macOS
- **AOT 发布**：可编译为独立可执行文件，无需 .NET 运行时

## 技术实现

- **Markdown 解析器**：纯 C# 实现，无外部依赖，采用 `ReadOnlySpan<char>` 和 `[MethodImpl(AggressiveInlining)]` 优化
- **GFM 支持**：表格、任务列表 `- [ ]` / `- [x]`、代码块、行内代码、链接、图片等
- **渲染控件**：自定义 `MarkdownView`，将 AST 转为 Avalonia 原生控件

## 构建与运行

```bash
# 开发运行
dotnet run --project MarkdownEditor

# 发布（AOT，Windows x64）
dotnet publish MarkdownEditor\MarkdownEditor.csproj -r win-x64 -c Release

# 发布（Linux x64）
dotnet publish MarkdownEditor\MarkdownEditor.csproj -r linux-x64 -c Release

# 发布（macOS Apple Silicon）
dotnet publish MarkdownEditor\MarkdownEditor.csproj -r osx-arm64 -c Release
```

发布输出位于 `MarkdownEditor\bin\Release\net10.0\<rid>\publish\`。

## 使用说明

1. 点击「选择文档文件夹」选择包含 `.md` 文件的目录
2. 左侧文档列表显示所有 Markdown 文件，搜索框可过滤
3. 点击文档在中间编辑、右侧实时预览
4. 使用「保存」按钮或 Ctrl+S 保存
5. 拖拽面板之间的分割线可调整各区域宽度

## 配置文件

首次运行后，配置保存在 `~/.markdown-editor/config.json`（Windows: `%USERPROFILE%\.markdown-editor\config.json`）。可配置项：

- **ui**：界面颜色、侧边栏背景等
- **markdown**：代码块背景色、表格边框色、标题字号、链接颜色等

参考 `config.sample.json` 修改。

## 依赖

- .NET 10
- Avalonia 11.2

# 性能与设计分析：改进与下一轮优化参考

> 目标：跨平台 + AOT 友好；识别高成本低价值功能；抓取坏味道与结构问题。

---

## 本轮已做（2025-03 全面优化）

- **删除** 未使用的 `MarkdownView`（axaml + axaml.cs），仅保留 `MarkdownEngineView` 单一预览。
- **搜索**：`SearchQuery` setter 仅调用 `FilterDocuments()`；`DoSearch()` 由主窗口 400ms 防抖后调用；`DoSearch` 优先使用 `CachedMarkdown`，读盘限制为单次最多 50 个文件、单文件 ≤2MB。
- **预览滚动**：`MarkdownEngineView` 使用 32ms 节流定时器替代每次 `ScrollChanged` 都 Post 重绘。
- **文件外部修改**：仅在存在 `CurrentFilePath` 时启动 2s 定时器，无当前文件时停止。
- **TrimmerRoots**：保留 `AppConfig`、`UiConfig`、`MarkdownStyleConfig`，便于 PublishTrimmed 后 JSON 序列化。
- **打开链接**：抽象为 `Core.IOpenUrlService` + `DefaultOpenUrlService`，`EngineRenderControl` 调用 `OpenUrlService.Open(url)`。
- **颜色解析**：抽成 `Core.ColorUtils.ParseHexColor`，`EngineConfig` 复用，便于单测与 AOT。
- **单元测试**：新增 `MarkdownEditor.Tests`（xUnit），覆盖 `ColorUtils`、`AppConfig` 往返、`OpenUrlService` 注入，共 22 个用例，全部通过。
- **定时器/延时安全**：所有 `DispatcherTimer.Tick` 与 `Dispatcher.UIThread.Post` 回调均用 try-catch 包裹，异常不导致进程崩溃；主窗口 `Closed` 时调用 `StopAllTimers()` 停止高亮/搜索防抖/文件检测定时器；`MarkdownEngineView` 在 `OnDetachedFromVisualTree` 中停止预览防抖与滚动节流定时器；关闭时先停定时器再保存配置，整体用 try-catch 保证关闭流程可完成。

---

## 一、跨平台与 AOT 结论（尽量不挂钩 Windows）

### 1.1 文件外部修改检测

- **保持当前方案（定时轮询）**：只用到 `File.GetLastWriteTimeUtc(path)`，.NET BCL 跨平台，无 Windows 专有 API，AOT 友好。
- **若改为事件驱动**：用 **`System.IO.FileSystemWatcher`** 即可，同属 BCL，在 Windows/Linux/macOS 上均有实现，不依赖 Windows 专有 API，AOT 兼容；只需监视当前文件所在目录 + `Filter = 文件名`，收到 `Changed` 再比较 `LastWriteTimeUtc`。**不要**用 Windows 专有的 `ReadDirectoryChangesW` 或 P/Invoke。

### 1.2 当前与平台/AOT 强相关的点

| 位置 | 问题 | 建议 |
|------|------|------|
| **csproj** | `OutputType=WinExe` | 跨平台需改为 `Exe`，并处理各平台窗口/托盘等差异。 |
| **MainWindow.axaml.cs** | 反射设置 AvaloniaEdit `EnableRectangularSelection` | 易被裁剪、随版本断裂。改为公共 API 或 TrimmerRoots 显式保留该类型/属性。 |
| **EngineRenderControl.cs** | `Process.Start(UseShellExecute = true)` 打开链接 | 行为依赖系统默认浏览器，属“平台相关行为”而非 P/Invoke。跨平台可接受；若需统一行为可抽象为 `IOpenUrlService` 再按平台实现。 |
| **AppConfig** | 配置与最近文件/文件夹已改用 LumConfig 持久化 | 无反射、AOT 友好，不再使用 JsonSerializer。 |

### 1.3 已确认无问题的用法

- `Environment.GetFolderPath(SpecialFolder.*)`：.NET 已抽象，各平台有实现。
- 无 `DllImport`、`Assembly.Load`、`Activator.CreateInstance(Type)` 等动态加载。

---

## 二、性能/资源占用大但功能简单或鸡肋（供筛选、评估改进潜力）

以下按「潜在提升」与「实现成本」粗分，便于你排优先级。

### 高潜力、建议优先评估

| 项 | 位置 | 现象 | 潜在提升 | 说明 |
|----|------|------|----------|------|
| **搜索无防抖 + 全量读盘** | `MainViewModel.SearchQuery` setter → `FilterDocuments()` + `DoSearch()` | 每次按键都过滤文档列表并可能对多文件 `File.ReadAllText` | 输入时卡顿、大文件/多文档时内存与 IO 峰值高 | 对 `SearchQuery` 做 300–500ms 防抖；`DoSearch` 限制单次搜索文件数或大小、或异步+取消，优先用 `CachedMarkdown`。 |
| **预览滚动触发重绘过频** | `MarkdownEngineView`：`Scroll.ScrollChanged` → `Dispatcher.UIThread.Post(..., Background)` → `InvalidateVisual()` | 快速滚动产生大量 Post + 全量 `Render()` | CPU 占用高、滚动不跟手 | 对滚动事件做节流（如 32ms 或与选区一致），或仅当可见区域变化超过一屏再重绘。 |
| **两套预览并存** | ~~`MarkdownView` vs `MarkdownEngineView`~~ | ~~主界面只用 Engine，MarkdownView 保留完整实现~~ | - | **已做**：已删除 `MarkdownView`，仅保留 `MarkdownEngineView`。 |

### 中等潜力、可按需优化

| 项 | 位置 | 现象 | 潜在提升 | 说明 |
|----|------|------|----------|------|
| **文件外部修改定时器** | `MainWindow`：每 2s `CheckFileChangedExternally()` | 关文档后定时器仍可能运行；仅读时间戳，单次开销小 | 省电、逻辑更清晰 | 无文档或无当前文件时停止定时器；或改为 FileSystemWatcher（跨平台 BCL）事件驱动，定时仅作兜底。 |
| **全量解析文档** | `RenderEngine`：`EnsureParsed` 时 `ParseFullDocument`，按块再 `MarkdownParser.Parse(text)` | 文档大、块多时解析次数多 | 大文档打开/切换更流畅 | 评估“按文档一次解析再按块映射”是否可行，或对未可见块延迟解析。 |
| **行索引全文本扫描** | `RenderEngine.EnsureLineIndex`：对 `doc.FullText` 逐字符扫 `\n` | 大文档 O(n) | 大文档内存与 CPU | 若 `IDocumentSource` 能提供行起始信息则避免全文本扫描。 |
| **导出各自创建 RenderEngine** | `PdfExporter`、`LongImageExporter` 等各自 `new RenderEngine(...)` | 配置/宽度不一致时导出与预览可能有细微差异；重复创建引擎 | 一致性、少量内存与初始化开销 | 统一导出用引擎或配置入口，或工厂单例。 |

### 成本低、可顺手做

| 项 | 位置 | 现象 | 建议 |
|----|------|------|------|
| **预览防抖不一致** | ~~MarkdownView 50ms vs MarkdownEngineView 200ms~~ | **已做**：已删除 MarkdownView，仅剩 Engine 预览 200ms 防抖。 | - |
| **EffectiveConfig 等** | 已有缓存 | - | 保持即可。 |

---

## 三、设计/实现坏味道（为下一轮优化做准备）

### 3.1 实现不聪明、易出 Bug

| 位置 | 问题类型 | 说明 |
|------|----------|------|
| **MainViewModel.SearchQuery** | 无防抖、重逻辑 | setter 内直接 `FilterDocuments()` + `DoSearch()`，DoSearch 可能多文件 `File.ReadAllText`，易性能问题与竞态。 |
| **MainViewModel** | CachedMarkdown 与读盘混用 | 部分用 `doc.CachedMarkdown`，部分用 `File.ReadAllText(doc.FullPath)`，语义不统一，易出现“未打开文档被整文件读入”。 |
| **MainWindow.axaml.cs** | 反射写内部属性 | `GetType().GetProperty("EnableRectangularSelection").SetValue(...)` 依赖 AvaloniaEdit 内部，易随版本断裂，Trimming 不友好。 |
| **EngineConfig** | 字体路径硬编码 | `Path.Combine(baseDir, "..", "..", "..", "asserts", "otf", ...)` 依赖固定目录结构，换工程布局易失效。 |
| **SkiaLayoutEngine** | 字体名写死 | `Cascadia Code`、`Microsoft YaHei UI` 等写死在代码中，跨平台/无该字体时表现不可控。 |
| **EngineDrawOp** | 背景色硬编码 | `offscreen.Clear(new SKColor(0x1e, 0x1e, 0x1e))` 与 UI 主题重复，应来自配置或常量。 |
| **AppConfig** | 静默吞异常 | `Load`/`Save` 里 `catch { }` 无日志，排查配置问题困难。 |

### 3.2 难扩展、通用性差

| 位置 | 问题类型 | 说明 |
|------|----------|------|
| **MainWindow** | 上帝视图 | 窗口、编辑器高亮、滚动同步、文件树、搜索、导出、设置、菜单、快捷键、Alt 键处理等全在一类，职责过多，难单测和扩展。 |
| **MainViewModel** | 上帝 VM | 文档列表、打开/当前文档、搜索、文件树、最近文件、配置、编辑/预览状态、焦点栈、导出等全在一类，难维护和测试。 |
| **Views ↔ MainViewModel** | 强耦合 | 大量 `DataContext is MainViewModel`、直接调 `vm.XXX`，不利于替换 VM 或复用视图。 |
| **MarkdownEngineView** | 与 MainViewModel 直接耦合 | 仍存在 `DataContext is MainViewModel` 与直接调用 VM 命令/状态，复用性差。 |
| **Engine ↔ Math** | 命名与依赖 | SkiaLayoutEngine 直接引用 `MarkdownEditor.Latex.MathSkiaRenderer`，若以后做“无数学”轻量引擎需改依赖；命名“Latex”与“Math”混用。 |
| **AppConfig** | 路径不可配置 | `DefaultConfigPath` / `RecentFilesPath` 固定为 `UserProfile\.markdown-editor\`，无便携模式或多配置。 |

### 3.3 临时实现、重复逻辑

| 位置 | 问题类型 | 说明 |
|------|----------|------|
| **ParseHexColor / ParseColor** | ~~重复实现~~ | **已做**：抽成 `Core.ColorUtils.ParseHexColor`，EngineConfig 复用；MarkdownView 已删。 |
| **MarkdownView vs MarkdownEngineView** | ~~两套预览~~ | **已做**：已删除 MarkdownView，仅保留 MarkdownEngineView。 |
| **MarkdownHighlightingService** | 与解析强耦合 | 注释写“与 MarkdownParser 一致”“暂时关闭行内高亮避免偏移错位”，解析改一动高亮易错位，需同步改两处。 |
| **EngineDrawOp.Dispose()** | 空实现 | `public void Dispose() { }`，若将来持有非托管资源易遗漏释放。 |

### 3.4 依赖方向与可测试性

- **Core** 被 Engine/ViewModels/Views 引用：合理。
- **Views 直接依赖 MainViewModel 具体类型**：不利于替换 VM、可插拔 UI 和单元测试。
- **导出**：各导出自建 RenderEngine，无统一工厂或配置入口，一致性与可测试性差。

---

## 四、建议的改进优先级（供你筛选）

1. **跨平台/AOT**：TrimmerRoots 或 Source Generator 保证 AppConfig 等序列化；打开链接抽象为接口；矩形选择改为公共 API 或显式保留。
2. **性能/资源**：搜索防抖 + 限制/异步读盘；预览滚动节流；评估删除或收敛 MarkdownView。
3. **坏味道**：先做“高潜力”项（搜索、滚动、双预览），再逐步拆分 MainWindow/MainViewModel、统一 CachedMarkdown 语义、抽离配置与字体路径、去重 ParseColor/ParseHexColor。

文件外部修改检测：保持定时轮询或改用 **FileSystemWatcher**（BCL、跨平台、不挂钩 Windows），均可；AOT 与跨平台不受影响。

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using AvaloniaEdit.Rendering;
using MarkdownEditor;
using MarkdownEditor.Controls;
using MarkdownEditor.Engine.Highlighting;
using MarkdownEditor.ViewModels;

namespace MarkdownEditor.Views;

/// <summary>
/// 封装与编辑器相关的行为：文本绑定、语法高亮、防抖和光标状态更新。
/// 将这部分逻辑从 MainWindow 抽离，便于维护和测试。
/// </summary>
internal sealed class EditorController
{
    private readonly TextEditor _editor;
    private readonly MainViewModel _viewModel;
    private readonly MarkdownHighlightingService _highlightService;
    private MarkdownColorizer _colorizer;
    private readonly DispatcherTimer _highlightTimer;
    private SearchPanel? _searchPanel;
    private int _highlightVersion;
    private int _appliedHighlightVersion;
    private string _currentEditorPath = "";

    /// <summary>当前已缓存高亮的行窗口（含首尾，0-based）。-1 表示尚未缓存。</summary>
    private int _highlightedWindowStart = -1;
    private int _highlightedWindowEnd = -1;

    /// <summary>下一次高亮是否必须强制重算当前窗口（例如文本已变更）。</summary>
    private bool _forceRehighlightWindow;
    private int _pendingGoToLine = -1;
    private bool _currentMarkdownChangeFromEditor;

    /// <summary>用于 Alt+Left/Right 的光标位置历史：上一次光标偏移。</summary>
    private int _lastCaretOffsetForHistory = -1;

    /// <summary>最近一次已压入后退栈的偏移，避免对相邻位置重复记录。</summary>
    private int _lastHistoryPushedOffset = -1;

    /// <summary>下一次光标位置变化是否跳过历史记录（用于程序化导航，例如 Alt+Back/Forward、搜索结果跳转等）。</summary>
    private bool _suppressNextHistoryRecord;

    private DiffBackgroundRenderer? _diffBackgroundRenderer;
    private DiffGutterMargin? _diffGutterMargin;
    private DiffLineNumberMargin? _diffLineNumberMargin;

    public EditorController(TextEditor editor, MainViewModel viewModel)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _highlightService = new MarkdownHighlightingService();
        _colorizer = new MarkdownColorizer(MarkdownHighlightTheme.DarkTheme);

        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _highlightTimer.Tick += (_, _) =>
        {
            try
            {
                _highlightTimer.Stop();
                UpdateEditorHighlight();
            }
            catch
            {
                _highlightTimer.Stop();
            }
        };

        SetupEditorHighlighting();
    }

    /// <summary>切换编辑器语法高亮主题（深色/浅色）。</summary>
    public void SetHighlightTheme(MarkdownHighlightTheme theme)
    {
        _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
        _colorizer = new MarkdownColorizer(theme);
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _forceRehighlightWindow = true;
        _highlightedWindowStart = _highlightedWindowEnd = -1;
        UpdateEditorHighlight();
    }

    /// <summary>请求在下一次 VM→Editor 同步时跳转到指定行（用于搜索结果导航后等待文档加载完成）。</summary>
    public void RequestGoToLine(int lineNumber)
    {
        _pendingGoToLine = lineNumber;
    }

    /// <summary>打开内置查找面板并将焦点移到搜索输入框。</summary>
    public void FocusFind()
    {
        _editor.Focus();
        _searchPanel?.Open();
        if (_searchPanel != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var firstTextBox = _searchPanel.GetVisualDescendants()
                        .OfType<Avalonia.Controls.TextBox>()
                        .FirstOrDefault(t => t.Focusable);
                    firstTextBox?.Focus();
                }
                catch
                {
                    // SearchPanel 未加载或已销毁时忽略
                }
            }, DispatcherPriority.Input);
        }
    }

    private void SetupEditorHighlighting()
    {
        _searchPanel = SearchPanel.Install(_editor);

        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;

        // 启用列选择（矩形选择）能力。
        // AvaloniaEdit 尚未在公共 API 上统一暴露该开关，这里通过一个受控 helper 做“兼容性 hack”：
        // - 优先尝试已知的公开属性/接口（未来版本若提供）；
        // - 否则退回到反射设置内部属性，并用窄范围 try/catch 包裹，避免影响正常编辑。
        TryEnableRectangularSelection(_editor);

        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        // 监听滚动偏移变化，用于根据视口位置增量触发高亮，而不是只在文本变更时。
        _editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
        {
            // 文本为空或尚未加载文档时不必触发高亮
            if (_editor.Document == null)
                return;

            // 滚动触发的高亮只用于调整窗口位置，不强制重算当前窗口的 token。
            _highlightTimer.Stop();
            _highlightTimer.Interval = TimeSpan.FromMilliseconds(90);
            _highlightTimer.Start();
        };

        _editor.Text = _viewModel.CurrentMarkdown ?? string.Empty;
        _currentEditorPath = _viewModel.CurrentFilePath ?? "";

        // 初始光标信息
        _viewModel.CaretLine = _editor.TextArea.Caret.Line;
        _viewModel.CaretColumn = _editor.TextArea.Caret.Column;
        if (_viewModel.ActiveDocument != null)
            _viewModel.ActiveDocument.LastCaretOffset = _editor.TextArea.Caret.Offset;
        _lastCaretOffsetForHistory = _editor.TextArea.Caret.Offset;
        _lastHistoryPushedOffset = -1;
        _suppressNextHistoryRecord = false;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentMarkdown))
            {
                var target = _viewModel.CurrentMarkdown ?? string.Empty;
                // #region agent log
                var hasVirtual = MarkdownEditor.Services.DiffVirtualLineHelper.ContainsVirtualLines(target);
                var skip = _currentMarkdownChangeFromEditor && !hasVirtual;
                DebugLog.Write("C", "EditorController.PropertyChanged", "CurrentMarkdown changed", new { targetLen = target.Length, fromEditor = _currentMarkdownChangeFromEditor, hasVirtual, skip });
                // #endregion
                if (_currentMarkdownChangeFromEditor)
                {
                    _currentMarkdownChangeFromEditor = false;
                    if (!hasVirtual)
                        return;
                }

                var willSet = !string.Equals(_editor.Text, target, StringComparison.Ordinal);
                // #region agent log
                if (willSet) DebugLog.Write("C", "EditorController.PropertyChanged.set", "Setting editor.Text", new { targetLen = target.Length });
                // #endregion
                if (willSet)
                {
                    var caret = _editor.TextArea.Caret.Offset;
                    _editor.Text = target;
                    _editor.TextArea.Caret.Offset = Math.Min(caret, _editor.Text.Length);
                    _currentEditorPath = _viewModel.CurrentFilePath ?? "";

                    // 文本/文档切换后重置光标历史起点，避免旧文档位置影响后续“大跳转”判断。
                    _lastCaretOffsetForHistory = _editor.TextArea.Caret.Offset;
                    _lastHistoryPushedOffset = -1;

                    if (_pendingGoToLine > 0 && _editor.Document != null && _editor.Document.LineCount >= _pendingGoToLine)
                    {
                        // 仅当文档已加载足够行时再跳转，避免异步读盘时内容未到导致跳转失效。
                        _suppressNextHistoryRecord = true;
                        var lineNum = Math.Clamp(_pendingGoToLine, 1, _editor.Document.LineCount);
                        var line = _editor.Document.GetLineByNumber(lineNum);
                        _editor.TextArea.Caret.Offset = line.Offset;
                        _editor.TextArea.Caret.BringCaretToView();
                        _pendingGoToLine = -1;
                    }
                }
            }
        };

        _editor.TextChanged += (_, _) =>
        {
            var text = _editor.Text ?? string.Empty;
            if (!string.Equals(_viewModel.CurrentMarkdown, text, StringComparison.Ordinal))
            {
                _currentMarkdownChangeFromEditor = true;
                _viewModel.CurrentMarkdown = text;
            }

            if (_viewModel.IsDiffCompareActive)
            {
                try { _editor.TextArea.TextView.Redraw(); } catch (VisualLinesInvalidException) { }
            }

            // 文本已变化，下一次高亮需要强制重算当前窗口（避免使用旧 token）。
            _forceRehighlightWindow = true;
            // 文本结构变化后，原有窗口可能已经不再适用，重置缓存范围，交由下一次计算。
            _highlightedWindowStart = -1;
            _highlightedWindowEnd = -1;

            _highlightTimer.Stop();
            _highlightTimer.Interval = GetHighlightInterval(text);
            _highlightTimer.Start();
        };

        _editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            // 更新状态栏的行列信息
            _viewModel.CaretLine = _editor.TextArea.Caret.Line;
            _viewModel.CaretColumn = _editor.TextArea.Caret.Column;
            var caretOffset = _editor.TextArea.Caret.Offset;
            if (_viewModel.ActiveDocument != null)
                _viewModel.ActiveDocument.LastCaretOffset = caretOffset;

            // 若本次光标移动由程序化导航触发，则仅更新基准位置，不记录历史。
            if (_suppressNextHistoryRecord)
            {
                _suppressNextHistoryRecord = false;
                _lastCaretOffsetForHistory = caretOffset;
                return;
            }

            // 记录用于 Alt+Left/Right 的“焦点历史”：当光标发生较大跳转时，将跳转前位置压入 VM 的后退栈。
            if (!string.IsNullOrEmpty(_currentEditorPath) && _editor.Document != null)
            {
                if (_lastCaretOffsetForHistory >= 0 && _lastCaretOffsetForHistory != caretOffset)
                {
                    try
                    {
                        var doc = _editor.Document;
                        var currentLine = doc.GetLineByOffset(caretOffset).LineNumber;
                        var lastLine = doc.GetLineByOffset(_lastCaretOffsetForHistory).LineNumber;
                        var lineDelta = Math.Abs(currentLine - lastLine);
                        var offsetDelta = Math.Abs(caretOffset - _lastCaretOffsetForHistory);
                        // 仅在“明显跳转”时记录历史：行号或偏移变化足够大，且与上次记录位置相距足够远，避免相邻位置重复压栈。
                        const int minDistanceFromLastPushed = 40;
                        bool farEnoughFromLast =
                            _lastHistoryPushedOffset < 0
                            || Math.Abs(_lastCaretOffsetForHistory - _lastHistoryPushedOffset) >= minDistanceFromLastPushed;
                        if (
                            (lineDelta >= 5 || offsetDelta >= 40)
                            && _lastHistoryPushedOffset != _lastCaretOffsetForHistory
                            && farEnoughFromLast
                        )
                        {
                            _viewModel.RecordLocation(
                                _currentEditorPath,
                                _lastCaretOffsetForHistory
                            );
                            _lastHistoryPushedOffset = _lastCaretOffsetForHistory;
                        }
                    }
                    catch
                    {
                        // 行号计算失败时不影响编辑，仅忽略本次历史记录。
                    }
                }

                _lastCaretOffsetForHistory = caretOffset;
            }
        };
    }

    /// <summary>
    /// 启用 AvaloniaEdit 的矩形选择（Alt+拖拽列选）。
    /// </summary>
    private static void TryEnableRectangularSelection(TextEditor editor)
    {
        if (editor?.Options == null)
            return;
        try
        {
            editor.Options.EnableRectangularSelection = true;
        }
        catch
        {
            // 矩形选择非核心功能，个别版本若无此属性则静默忽略。
        }
    }

    /// <summary>开启或关闭编辑区 diff 背景（与 Git 版本比对）。getLineMap 为 null 时移除渲染器。addedBrush/removedBrush 为 null 时使用内置默认色。行号区左侧会显示 +/- 符号。</summary>
    internal void SetDiffMode(Func<MarkdownEditor.Models.GitDiffLineMap?>? getLineMap, Avalonia.Media.IBrush? addedBrush = null, Avalonia.Media.IBrush? removedBrush = null)
    {
        var textArea = _editor.TextArea;
        var textView = textArea?.TextView;
        if (textView == null) return;

        if (_diffBackgroundRenderer != null)
        {
            textView.BackgroundRenderers.Remove(_diffBackgroundRenderer);
            _diffBackgroundRenderer = null;
        }
        if (_diffGutterMargin != null && textArea != null)
        {
            _diffGutterMargin.RemoveFromTextView(textView);
            textArea.LeftMargins.Remove(_diffGutterMargin);
            _diffGutterMargin = null;
        }
        if (_diffLineNumberMargin != null && textArea != null)
        {
            _diffLineNumberMargin.RemoveFromTextView(textView);
            textArea.LeftMargins.Remove(_diffLineNumberMargin);
            _diffLineNumberMargin = null;
        }
        if (getLineMap != null)
        {
            _editor.ShowLineNumbers = false;
            _diffBackgroundRenderer = new DiffBackgroundRenderer(getLineMap, addedBrush, removedBrush);
            textView.BackgroundRenderers.Add(_diffBackgroundRenderer);
            _diffGutterMargin = new DiffGutterMargin(getLineMap);
            _diffGutterMargin.AddToTextView(textView);
            textArea!.LeftMargins.Insert(0, _diffGutterMargin);
            _diffLineNumberMargin = new DiffLineNumberMargin();
            _diffLineNumberMargin.AddToTextView(textView);
            textArea.LeftMargins.Insert(1, _diffLineNumberMargin);
        }
        else
        {
            _editor.ShowLineNumbers = true;
        }
        try { textView.Redraw(); } catch (VisualLinesInvalidException) { }
    }

    /// <summary>供窗口在执行程序化导航前调用，确保下一次光标变化不会被记录到 Alt+Left/Right 历史中。</summary>
    internal void SuppressNextHistoryRecord()
    {
        _suppressNextHistoryRecord = true;
    }

    private void UpdateEditorHighlight()
    {
        var text = _editor.Text ?? string.Empty;
        var version = ++_highlightVersion;

        int? windowStart = null;
        int? windowEnd = null;

        // 根据当前视口 + 总行数，计算一个“最多约 10 页”的高亮窗口。
        var doc = _editor.Document;
        var visible = GetVisibleLineRange(_editor, extraLines: 0);
        int totalLines = doc?.LineCount ?? 0;

        if (visible.HasValue && totalLines > 0)
        {
            int visibleStart = visible.Value.startLine;
            int visibleEnd = visible.Value.endLine;
            int visibleCount = Math.Max(1, visibleEnd - visibleStart + 1);

            // 约 10 页窗口：可见行数 * 10，至少 200 行，以避免超小窗口导致频繁重算。
            const int maxPages = 10;
            int targetWindowSize = Math.Max(200, visibleCount * maxPages);

            if (totalLines <= targetWindowSize)
            {
                // 总行数未超过“10 页”范围，直接对整篇做高亮。
                windowStart = null;
                windowEnd = null;
            }
            else
            {
                // 以视口中心为基准，向上/向下扩展窗口。
                int center = (visibleStart + visibleEnd) / 2;
                int half = targetWindowSize / 2;
                int start = Math.Max(0, center - half);
                int end = Math.Min(totalLines - 1, start + targetWindowSize - 1);
                // 若靠近文末导致窗口不满，再向前补齐。
                start = Math.Max(0, end - targetWindowSize + 1);

                // 若是仅由滚动触发，且当前可见区域仍落在已缓存窗口内，则可以直接复用现有 token。
                if (
                    !_forceRehighlightWindow
                    && _highlightedWindowStart >= 0
                    && _highlightedWindowEnd >= _highlightedWindowStart
                    && visibleStart >= _highlightedWindowStart
                    && visibleEnd <= _highlightedWindowEnd
                    && start >= _highlightedWindowStart
                    && end <= _highlightedWindowEnd
                )
                {
                    // 当前视口仍在已缓存的“10 页”窗口内，无需重新计算高亮。
                    return;
                }

                windowStart = start;
                windowEnd = end;
            }
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var tokens = _highlightService.Analyze(text, windowStart, windowEnd);
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        try
                        {
                            if (version <= _appliedHighlightVersion)
                                return;
                            if (_editor.TextArea?.TextView == null)
                                return;

                            _appliedHighlightVersion = version;
                            _colorizer.UpdateTokens(tokens);
                            // 记录当前已应用的窗口范围，供后续滚动重用。
                            if (windowStart.HasValue && windowEnd.HasValue)
                            {
                                _highlightedWindowStart = windowStart.Value;
                                _highlightedWindowEnd = windowEnd.Value;
                            }
                            else if (doc != null)
                            {
                                // 整篇文档已被高亮。
                                _highlightedWindowStart = 0;
                                _highlightedWindowEnd = Math.Max(0, doc.LineCount - 1);
                            }
                            _forceRehighlightWindow = false;

                            _editor.TextArea.TextView.Redraw();
                        }
                        catch (VisualLinesInvalidException)
                        {
                            // 视图正在重建（如焦点切到文件树重命名框）时可能抛出，忽略即可
                        }
                        catch
                        {
                            // 控件已销毁或断开视觉树时忽略，不影响使用
                        }
                    },
                    DispatcherPriority.Background
                );
            }
            catch
            {
                // 高亮分析异常不影响编辑
            }
        });
    }

    private static TimeSpan GetHighlightInterval(string text)
    {
        var length = text?.Length ?? 0;
        if (length <= 5_000)
            return TimeSpan.FromMilliseconds(180);
        if (length <= 50_000)
            return TimeSpan.FromMilliseconds(320);
        return TimeSpan.FromMilliseconds(650);
    }

    private static (int startLine, int endLine)? GetVisibleLineRange(
        TextEditor editor,
        int extraLines = 40
    )
    {
        var textView = editor.TextArea?.TextView;
        if (textView == null)
            return null;

        try
        {
            // 未量完或换主题等导致可视行失效时，访问 VisualLines 会抛 VisualLinesInvalidException。
            if (!textView.VisualLinesValid)
                return null;

            var visualLines = textView.VisualLines;
            if (visualLines == null || visualLines.Count == 0)
                return null;

            int first = visualLines.Min(v => v.FirstDocumentLine.LineNumber) - 1;
            int last = visualLines.Max(v => v.LastDocumentLine.LineNumber) - 1;

            if (editor.Document == null)
                return (Math.Max(0, first), Math.Max(first, last));

            int maxIndex = Math.Max(0, editor.Document.LineCount - 1);
            int start = Math.Max(0, first - extraLines);
            int end = Math.Min(maxIndex, last + extraLines);
            if (end < start)
                end = start;
            return (start, end);
        }
        catch (VisualLinesInvalidException)
        {
            // 视图尚未完成布局或正在重建可视行时抛出，直接视为“当前不可用”，本次不做增量高亮。
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using MarkdownEditor.Core;

namespace MarkdownEditor.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>供设置页 ComboBox ItemsSource 绑定（与静态数组同一引用集）。</summary>
    public IEnumerable<AutoSaveOptionItem> AutoSaveItemsList => AutoSaveOptionItems;

    public static readonly AutoSaveOptionItem[] AutoSaveOptionItems =
    {
        new() { Label = "禁用", Seconds = 0 },
        new() { Label = "1 分钟", Seconds = 60 },
        new() { Label = "5 分钟", Seconds = 300 },
        new() { Label = "10 分钟", Seconds = 600 },
        new() { Label = "20 分钟", Seconds = 1200 },
    };

    /// <summary>设置页绑定：自动保存间隔。</summary>
    public AutoSaveOptionItem SelectedAutoSaveOption
    {
        get
        {
            var s = Config.Ui.AutoSaveIntervalSeconds;
            foreach (var o in AutoSaveOptionItems)
            {
                if (o.Seconds == s)
                    return o;
            }
            return AutoSaveOptionItems[0];
        }
        set
        {
            if (value == null)
                return;
            if (Config.Ui.AutoSaveIntervalSeconds == value.Seconds)
                return;
            Config.Ui.AutoSaveIntervalSeconds = value.Seconds;
            StopAllAutoSaveTimers();
            try
            {
                Config.Save(AppConfig.DefaultConfigPath);
            }
            catch
            {
                /* 忽略 */
            }
            OnPropertyChanged(nameof(SelectedAutoSaveOption));
        }
    }

    /// <summary>磁盘上文件已不存在时，是否在原路径重新创建（自动保存）。返回 false 则放弃本次并等待用户后续编辑再计时。</summary>
    public Func<string, Task<bool>>? AutoSaveAskRecreateMissingFileAsync { get; set; }

    /// <summary>自动保存写入失败等提示。</summary>
    public Func<string, Task>? AutoSaveShowFailureAsync { get; set; }

    private readonly Dictionary<DocumentItem, DispatcherTimer> _autoSaveTimers = new();
    private DispatcherTimer? _autoSaveDebounceTimer;
    private DocumentItem? _autoSaveDebounceDoc;

    private void ScheduleAutoSaveDebounced()
    {
        if (Config.Ui.AutoSaveIntervalSeconds <= 0)
            return;
        if (_activeDocument == null)
            return;
        if (string.IsNullOrEmpty(_activeDocument.FullPath))
            return;
        if (IsPreviewableImagePath(_activeDocument.FullPath))
            return;

        _autoSaveDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _autoSaveDebounceTimer.Stop();
        _autoSaveDebounceTimer.Tick -= OnAutoSaveDebounceTick;
        _autoSaveDebounceDoc = _activeDocument;
        _autoSaveDebounceTimer.Tick += OnAutoSaveDebounceTick;
        _autoSaveDebounceTimer.Start();
    }

    private void OnAutoSaveDebounceTick(object? sender, EventArgs e)
    {
        _autoSaveDebounceTimer?.Stop();
        _autoSaveDebounceTimer!.Tick -= OnAutoSaveDebounceTick;
        var doc = _autoSaveDebounceDoc;
        _autoSaveDebounceDoc = null;
        if (doc == null || !doc.IsOpen || !doc.IsModified || string.IsNullOrEmpty(doc.FullPath))
            return;
        if (IsPreviewableImagePath(doc.FullPath))
            return;
        if (Config.Ui.AutoSaveIntervalSeconds <= 0)
            return;

        StopAutoSaveTimer(doc);
        var sec = Config.Ui.AutoSaveIntervalSeconds;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _autoSaveTimers.Remove(doc);
            _ = ProcessAutoSaveCoreAsync(doc);
        };
        _autoSaveTimers[doc] = timer;
        timer.Start();
    }

    private void StopAutoSaveTimer(DocumentItem? doc)
    {
        if (doc == null)
            return;
        if (_autoSaveTimers.Remove(doc, out var t))
        {
            t.Stop();
        }
    }

    private void StopAllAutoSaveTimers()
    {
        foreach (var kv in _autoSaveTimers)
            kv.Value.Stop();
        _autoSaveTimers.Clear();
        _autoSaveDebounceTimer?.Stop();
        _autoSaveDebounceDoc = null;
    }

    private string GetDocumentTextForAutoSave(DocumentItem doc)
    {
        if (ReferenceEquals(_activeDocument, doc))
            return _currentMarkdown ?? "";
        return doc.CachedMarkdown ?? "";
    }

    private async Task ProcessAutoSaveCoreAsync(DocumentItem doc)
    {
        if (!doc.IsOpen || !doc.IsModified)
            return;
        var path = doc.FullPath;
        if (string.IsNullOrEmpty(path) || IsPreviewableImagePath(path))
            return;

        var raw = GetDocumentTextForAutoSave(doc);
        if (Services.DiffVirtualLineHelper.ContainsVirtualLines(raw))
            raw = Services.DiffVirtualLineHelper.RemoveVirtualLines(raw);
        var text = NormalizeOrderedLists(raw);

        try
        {
            if (!File.Exists(path))
            {
                var ask = AutoSaveAskRecreateMissingFileAsync;
                if (ask == null)
                    return;
                var recreate = await ask(path).ConfigureAwait(true);
                if (!recreate)
                    return;
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }

            var enc = GetEncodingByName(doc.EncodingName);
            File.WriteAllText(path, text, enc);
            var w = File.GetLastWriteTimeUtc(path);

            doc.CachedMarkdown = text;
            doc.IsModified = false;
            doc.LastKnownWriteTimeUtc = w;

            if (ReferenceEquals(_activeDocument, doc))
            {
                _isModified = false;
                OnPropertyChanged(nameof(IsModified));
                OnPropertyChanged(nameof(CenterTitle));
            }
        }
        catch (Exception ex)
        {
            var showFail = AutoSaveShowFailureAsync;
            if (showFail != null)
                await showFail($"无法自动保存「{Path.GetFileName(path)}」：{ex.Message}").ConfigureAwait(true);
        }
    }
}

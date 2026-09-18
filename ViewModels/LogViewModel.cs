using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Data;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>日志等级过滤项。</summary>
public sealed class LogLevelFilter
{
    public string Label { get; init; } = string.Empty;
    public LogLevel? Minimum { get; init; }
    public string Hint { get; init; } = string.Empty;
}

/// <summary>
/// 运行日志页。
///
/// 现场排障时最怕"报了个错误码但不知道发生了什么"，所以这一页要满足三件事：
///   1. 能按等级和关键字快速筛（几十秒就能定位到那一条失败）；
///   2. 能把完整堆栈展开看（错误详情单独显示，不挤在列表里）；
///   3. 能一键导出成文本发给技术支持。
/// </summary>
public sealed class LogViewModel : PageViewModel
{
    private const string Source = "日志";

    private string _keyword = string.Empty;
    private LogLevelFilter _selectedLevel;
    private LogEntry? _selectedEntry;
    private bool _autoScroll = true;
    private bool _autoScrollSuspended;
    private string _summary = string.Empty;

    private RelayCommand? _clearCommand;
    private RelayCommand? _exportCommand;
    private RelayCommand? _openLogFolderCommand;
    private RelayCommand? _copySelectedCommand;
    private RelayCommand? _copyAllCommand;
    private RelayCommand? _pauseAutoScrollCommand;
    private RelayCommand? _reloadCommand;

    public LogViewModel()
    {
        LevelFilters =
        [
            new LogLevelFilter { Label = "全部等级", Minimum = null, Hint = "显示所有日志" },
            new LogLevelFilter { Label = "信息以上", Minimum = LogLevel.Info, Hint = "隐藏调试日志" },
            new LogLevelFilter { Label = "成功以上", Minimum = LogLevel.Success, Hint = "只看成功与异常" },
            new LogLevelFilter { Label = "仅警告与错误", Minimum = LogLevel.Warn, Hint = "排障时最常用" },
            new LogLevelFilter { Label = "仅错误", Minimum = LogLevel.Error, Hint = "只看失败" },
        ];
        _selectedLevel = LevelFilters[0];

        // 直接把日志服务的集合包装成可过滤视图，避免每次筛选都做一次全量拷贝
        View = CollectionViewSource.GetDefaultView(Log.Entries);
        View.Filter = FilterEntry;

        Log.EntryAdded += OnEntryAdded;
    }

    public ICollectionView View { get; }

    public IReadOnlyList<LogLevelFilter> LevelFilters { get; }

    public override string Title => "运行日志";

    public override string Glyph => "IconTerminal";

    public override string Description => "查看程序与 SDK 的完整运行记录，支持筛选、展开堆栈与导出";

    public override string Usage => "设备报错时先来这里按「仅警告与错误」筛一遍，错误码含义与处置建议都写在详情里。";

    // ==================== 筛选 ====================

    public string Keyword
    {
        get => _keyword;
        set
        {
            if (!Set(ref _keyword, value)) return;
            RefreshView();
        }
    }

    public LogLevelFilter SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (!Set(ref _selectedLevel, value)) return;
            RefreshView();
        }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public LogEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!Set(ref _selectedEntry, value)) return;
            OnPropertyChanged(nameof(HasSelectedEntry));
            // HasDetail 依赖当前选中项，必须跟着一起通知，
            // 否则"有堆栈的日志"点开也看不到详情框。
            OnPropertyChanged(nameof(HasDetail));
            _copySelectedCommand?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedEntry => SelectedEntry != null;

    public bool HasDetail => !string.IsNullOrEmpty(SelectedEntry?.Detail);

    /// <summary>自动滚到最新一条；用户主动往上翻时会自动暂停，避免被"拽"回去。</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set
        {
            if (!Set(ref _autoScroll, value)) return;
            AutoScrollSuspended = false;
        }
    }

    public bool AutoScrollSuspended
    {
        get => _autoScrollSuspended;
        private set => Set(ref _autoScrollSuspended, value);
    }

    private bool FilterEntry(object obj)
    {
        if (obj is not LogEntry entry) return false;

        if (SelectedLevel.Minimum is { } min && entry.Level < min) return false;

        if (!string.IsNullOrWhiteSpace(Keyword))
        {
            var k = Keyword.Trim();
            bool hit =
                entry.Message.Contains(k, StringComparison.OrdinalIgnoreCase)
                || entry.Source.Contains(k, StringComparison.OrdinalIgnoreCase)
                || (entry.Detail?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit) return false;
        }
        return true;
    }

    private void RefreshView()
    {
        View.Refresh();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int shown = View.Cast<object>().Count();
        int total = Log.Count;
        int errors = Log.Snapshot().Count(e => e.Level == LogLevel.Error);
        int warns = Log.Snapshot().Count(e => e.Level == LogLevel.Warn);

        Summary = shown == total
            ? $"共 {total} 条 · 错误 {errors} · 警告 {warns}"
            : $"筛选后 {shown} / {total} 条 · 错误 {errors} · 警告 {warns}";
    }

    private void OnEntryAdded(LogEntry entry)
    {
        // EntryAdded 保证在 UI 线程触发，可以安全地动界面状态
        AutoScrollSuspended = !AutoScroll;

        // 有新日志时刷新视图，让筛选结果与统计保持同步
        UpdateSummary();

        if (entry.Level == LogLevel.Error)
        {
            // 出现错误时把用户的注意力拉回来：如果当前筛选把它挡住了就提示一下
            if (!FilterEntry(entry)) Toast($"出现错误但被筛选条件隐藏：{entry.Message}", true);
        }
    }

    // ==================== 命令 ====================

    public RelayCommand ClearCommand => _clearCommand ??= new(() =>
    {
        Log.Clear();
        SelectedEntry = null;
        RefreshView();
        Log.Info("日志已清空（磁盘上的历史日志文件未被删除）", Source);
    });

    public RelayCommand ReloadCommand => _reloadCommand ??= new(() =>
    {
        RefreshView();
        Toast($"已刷新，当前 {Log.Count} 条");
    });

    public RelayCommand ExportCommand => _exportCommand ??= new(() =>
    {
        try
        {
            var path = Path.Combine(OutputDir, $"运行日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            Log.Export(path);
            Log.Success($"日志已导出：{path}", Source);
            Toast("日志已导出到 output 目录");
            RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            Log.Error("导出日志失败", ex, Source);
            Toast($"导出失败：{ex.Message}", true);
        }
    });

    public RelayCommand OpenLogFolderCommand => _openLogFolderCommand ??= new(() =>
        EnvironmentCheck.OpenFolder(AppPaths.LogDir));

    public RelayCommand CopySelectedCommand => _copySelectedCommand ??= new(
        () =>
        {
            if (SelectedEntry == null) return;
            var text = Format(SelectedEntry);
            if (TrySetClipboard(text)) Toast("已复制该条日志");
        },
        () => SelectedEntry != null);

    public RelayCommand CopyAllCommand => _copyAllCommand ??= new(() =>
    {
        var sb = new StringBuilder();
        foreach (var e in View.Cast<LogEntry>()) sb.AppendLine(Format(e));
        if (sb.Length == 0) { Toast("当前筛选结果为空", true); return; }
        if (TrySetClipboard(sb.ToString())) Toast("已复制当前筛选出的全部日志");
    });

    public RelayCommand PauseAutoScrollCommand => _pauseAutoScrollCommand ??= new(() =>
    {
        AutoScroll = !AutoScroll;
        Toast(AutoScroll ? "已恢复自动滚动" : "已暂停自动滚动");
    });

    private static string Format(LogEntry e)
    {
        var sb = new StringBuilder();
        sb.Append($"{e.Time:yyyy-MM-dd HH:mm:ss.fff}\t[{e.LevelText}]\t[{e.Source}]\t{e.Message}");
        if (!string.IsNullOrEmpty(e.Detail)) sb.Append(Environment.NewLine).Append(e.Detail);
        return sb.ToString();
    }

    private static bool TrySetClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"写剪贴板失败：{ex.Message}", Source);
            return false;
        }
    }

    private static void RevealInExplorer(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器不影响导出结果
        }
    }

    private static string OutputDir
        => string.IsNullOrWhiteSpace(App.Settings.OutputDir) ? AppPaths.OutputDir : App.Settings.OutputDir;

    public override void OnEnter()
    {
        RefreshView();
        OnPropertyChanged(nameof(HasDetail));
    }

    private static LogService Log => App.Log;
}

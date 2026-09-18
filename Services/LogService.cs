using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using HikDeployTool.Models;

namespace HikDeployTool.Services;

/// <summary>
/// 运行日志中心：内存保留最近 N 条，同时落盘到 logs/日期.log。
///
/// 线程模型（这是本类最关键的设计）：
/// 日志的来源五花八门——SADP 回调线程、网络 SDK 回调线程、Task.Run 的后台线程、UI 线程。
/// 而 ObservableCollection 一旦被界面绑定，就只允许在创建它的线程（UI 线程）上修改，
/// 否则会抛 "Collection was modified from a different thread" 甚至直接把进程带崩。
///
/// 所以这里做两层：
///   1. _buffer：加锁的环形缓冲，任何线程都能安全写，作为唯一事实来源（导出用）；
///   2. Entries：界面绑定的集合，只由 UI 线程写入；后台线程先入 _pending 队列，
///      再用一次 BeginInvoke 批量冲刷（而不是每条日志一次 Invoke，避免刷屏风暴把消息队列打爆）。
/// </summary>
public sealed class LogService
{
    private const int MaxInMemory = 3000;

    private readonly object _gate = new();
    private readonly List<LogEntry> _buffer = [];
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private int _dispatchScheduled;

    public static LogService Current { get; } = new();

    /// <summary>绑定到界面日志页的集合（只在 UI 线程写入）。</summary>
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>记录日志时同步落盘。</summary>
    public bool PersistToFile { get; set; } = true;

    /// <summary>新日志回调。订阅者可以放心直接更新界面——事件保证在 UI 线程触发。</summary>
    public event Action<LogEntry>? EntryAdded;

    public void Debug(string message, string source = "App") => Write(LogLevel.Debug, message, null, source);
    public void Info(string message, string source = "App") => Write(LogLevel.Info, message, null, source);
    public void Success(string message, string source = "App") => Write(LogLevel.Success, message, null, source);
    public void Warn(string message, string source = "App") => Write(LogLevel.Warn, message, null, source);

    public void Error(string message, Exception? ex = null, string source = "App")
        => Write(LogLevel.Error, message, ex?.ToString(), source);

    public void Error(string message, string detail, string source = "App")
        => Write(LogLevel.Error, message, detail, source);

    private void Write(LogLevel level, string message, string? detail, string source)
    {
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Source = source,
            Message = message,
            Detail = detail,
        };

        lock (_gate)
        {
            _buffer.Add(entry);
            while (_buffer.Count > MaxInMemory) _buffer.RemoveAt(0);
        }

        PersistToDisk(entry);
        Publish(entry);
    }

    private void PersistToDisk(LogEntry entry)
    {
        if (!PersistToFile) return;
        try
        {
            var dir = AppPaths.LogDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{entry.LevelText}] [{entry.Source}] {entry.Message}"
                       + (string.IsNullOrEmpty(entry.Detail) ? string.Empty : Environment.NewLine + entry.Detail);
            File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志落盘失败不能影响主流程
        }
    }

    /// <summary>把日志投递到 UI 线程；已在 UI 线程或没有 UI 时直接追加。</summary>
    private void Publish(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.CheckAccess() || dispatcher.HasShutdownStarted)
        {
            AppendToView(entry);
            return;
        }

        _pending.Enqueue(entry);

        // 只允许一个待执行的冲刷任务，避免每条日志都产生一次调度
        if (Interlocked.CompareExchange(ref _dispatchScheduled, 1, 0) != 0) return;

        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainPending));
        }
        catch
        {
            // 程序正在退出，dispatcher 已不再接收任务；退回当前线程写入
            Interlocked.Exchange(ref _dispatchScheduled, 0);
            DrainPending();
        }
    }

    private void DrainPending()
    {
        Interlocked.Exchange(ref _dispatchScheduled, 0);
        while (_pending.TryDequeue(out var entry)) AppendToView(entry);
    }

    private void AppendToView(LogEntry entry)
    {
        try
        {
            Entries.Add(entry);
            while (Entries.Count > MaxInMemory) Entries.RemoveAt(0);
        }
        catch (Exception)
        {
            // 界面集合不应因为任何原因（含订阅者异常之外的边界情况）把调用方拖垮
            return;
        }

        try
        {
            EntryAdded?.Invoke(entry);
        }
        catch
        {
            // 订阅者的异常不该影响写日志的人
        }
    }

    public void Clear()
    {
        lock (_gate) _buffer.Clear();
        Entries.Clear();
    }

    /// <summary>当前内存中的日志快照（可在任意线程调用）。</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return [.. _buffer];
    }

    public int Count { get { lock (_gate) return _buffer.Count; } }

    /// <summary>导出当前内存中的日志为文本。</summary>
    public void Export(string path)
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot())
        {
            sb.AppendLine($"{e.Time:yyyy-MM-dd HH:mm:ss.fff}\t[{e.LevelText}]\t[{e.Source}]\t{e.Message}");
            if (!string.IsNullOrEmpty(e.Detail)) sb.AppendLine(e.Detail);
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}

/// <summary>程序运行期用到的目录，统一在此推导，避免各处硬编码路径。</summary>
public static class AppPaths
{
    /// <summary>exe 所在目录（所有 SDK DLL 必须在这里）。</summary>
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    public static string LogDir => Path.Combine(BaseDir, "logs");

    public static string OutputDir => Path.Combine(BaseDir, "output");

    public static string SettingsFile => Path.Combine(BaseDir, "settings.json");

    public static string AssetsFile => Path.Combine(BaseDir, "assets.json");

    /// <summary>通道管理偏好（每台设备哪些通道显示/隐藏、OSD 名称缓存）。</summary>
    public static string ChannelPrefsFile => Path.Combine(BaseDir, "channel_prefs.json");

    /// <summary>通道缩略图缓存（通道管理弹窗抓的 JPEG）。</summary>
    public static string ThumbDir => Path.Combine(OutputDir, "thumbs");

    public static string SnapshotDir => Path.Combine(OutputDir, "snapshots");

    public static string ReportDir => Path.Combine(OutputDir, "reports");

    public static string SdkLogDirDefault => Path.Combine(BaseDir, "sdk_log");

    /// <summary>确保所有输出目录存在。</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(SnapshotDir);
        Directory.CreateDirectory(ReportDir);
        Directory.CreateDirectory(SdkLogDirDefault);
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>
/// 批量设备操作页的公共基类（激活 / 网络 / 密码 / 巡检都长一个样）：
///  左栏"待处理清单"，右栏"执行结果 + 进度"。
///
/// 并发策略：SADP 的每个操作都是"发广播包 + 等设备应答"，
/// 并发数太高会互相抢占网卡队列，反而不稳，所以默认 4，可在设置页调整。
/// 这里用 SemaphoreSlim 限流，而不是 Parallel.ForEach——后者对 IO 等待型任务不合适。
/// </summary>
public abstract class DeviceTaskPageViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private int _done;
    private int _total;
    private double _progress;

    // 命令只创建一次：每次绑定都 new 的话，CanExecuteChanged 订阅会失效，
    // 按钮的禁用/启用状态就跟不上了。
    private readonly RelayCommand _loadTargetsCommand;
    private readonly RelayCommand _useAllDevicesCommand;
    private readonly RelayCommand _useNotActivatedCommand;
    private readonly RelayCommand _clearTargetsCommand;
    private readonly RelayCommand _removeTargetCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly AsyncRelayCommand _runCommand;

    protected DeviceTaskPageViewModel()
    {
        Targets = [];
        Results = [];

        _loadTargetsCommand = new RelayCommand(LoadTargets);
        _useAllDevicesCommand = new RelayCommand(UseAllDevices);
        _useNotActivatedCommand = new RelayCommand(UseNotActivated);
        _clearTargetsCommand = new RelayCommand(() => { Targets.Clear(); OnTargetsChanged(); });
        _removeTargetCommand = new RelayCommand(obj =>
        {
            if (obj is DiscoveredDevice d) { Targets.Remove(d); OnTargetsChanged(); }
        });
        _cancelCommand = new RelayCommand(CancelRun);
        _runCommand = new AsyncRelayCommand(RunAsyncCore, () => !IsRunning && Targets.Count > 0);

        Targets.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (DiscoveredDevice d in e.NewItems) d.IsSelected = true;
            OnTargetsChanged();
            _runCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<DiscoveredDevice> Targets { get; }

    public ObservableCollection<OperationResult> Results { get; }

    // ==================== 待处理清单 ====================

    public int TargetCount => Targets.Count;

    public string TargetSummary => Targets.Count == 0
        ? "尚未载入设备，请先在「设备发现」页勾选后投递，或点「载入选中设备」"
        : $"待处理 {Targets.Count} 台：{string.Join("、", Targets.Take(3).Select(d => d.IpMaskText))}{(Targets.Count > 3 ? " 等" : string.Empty)}";

    public RelayCommand LoadTargetsCommand => _loadTargetsCommand;

    public RelayCommand UseAllDevicesCommand => _useAllDevicesCommand;

    public RelayCommand UseNotActivatedCommand => _useNotActivatedCommand;

    public RelayCommand ClearTargetsCommand => _clearTargetsCommand;

    public RelayCommand RemoveTargetCommand => _removeTargetCommand;

    private void LoadTargets()
    {
        var picked = App.SelectedDevices();
        if (picked.Count == 0)
        {
            Toast("「设备发现」页还没有勾选任何设备", true);
            return;
        }
        Targets.Clear();
        foreach (var d in picked) Targets.Add(d);
        Toast($"已载入 {Targets.Count} 台设备");
    }

    private void UseAllDevices()
    {
        if (App.Devices.Count == 0) { Toast("设备列表为空，请先搜索", true); return; }
        Targets.Clear();
        foreach (var d in App.Devices) Targets.Add(d);
        OnTargetsChanged();
        Toast($"已载入全部 {Targets.Count} 台设备");
    }

    private void UseNotActivated()
    {
        var list = App.Devices.Where(d => d.Activate == ActivateState.NotActivated).ToList();
        if (list.Count == 0) { Toast("当前没有未激活设备", true); return; }
        Targets.Clear();
        foreach (var d in list) Targets.Add(d);
        OnTargetsChanged();
        Toast($"已载入 {Targets.Count} 台未激活设备");
    }

    protected virtual void OnTargetsChanged()
    {
        OnPropertyChanged(nameof(TargetCount));
        OnPropertyChanged(nameof(TargetSummary));
        _runCommand?.RaiseCanExecuteChanged();
    }

    // ==================== 执行状态 ====================

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(RunButtonText));
        }
    }

    public virtual string RunButtonText => IsRunning ? "执行中…" : "开始执行";

    public int Total { get => _total; private set => Set(ref _total, value); }

    public int Done
    {
        get => _done;
        private set
        {
            if (Set(ref _done, value)) OnPropertyChanged(nameof(ProgressText));
        }
    }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public string ProgressText => Total == 0 ? "等待开始" : $"{Done} / {Total}";

    public int SuccessCount => Results.Count(r => r.Success);

    public int FailCount => Results.Count(r => !r.Success);

    public string ResultSummary => Results.Count == 0
        ? "还没有执行记录"
        : $"成功 {SuccessCount} 台 · 失败 {FailCount} 台";

    public AsyncRelayCommand RunCommand => _runCommand;

    public RelayCommand CancelCommand => _cancelCommand;

    private void CancelRun()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        Log.Warn("用户请求取消当前批量任务，已完成的部分会保留", LogSource);
        Toast("已发出取消请求，正在等待当前设备处理完成");
    }

    private async Task RunAsyncCore()
    {
        var validation = Validate();
        if (!string.IsNullOrEmpty(validation))
        {
            Toast(validation, true);
            return;
        }

        var list = Targets.ToList();

        // 允许子类在启动前固定"执行计划"（例如网络页的 IP 分配表），
        // 避免执行过程中用户改参数导致设备与计划错位
        OnBeforeRun(list);

        Results.Clear();
        NotifyResultCounters();
        Total = list.Count;
        Done = 0;
        Progress = 0;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        using (Busy($"{BusyTextPrefix}：共 {list.Count} 台…"))
        {
            int parallel = Math.Clamp(App.Settings.MaxParallelism, 1, 32);
            Log.Info($"开始{BusyTextPrefix}，共 {list.Count} 台，并发 {parallel}", LogSource);

            using var semaphore = new SemaphoreSlim(parallel, parallel);
            var tasks = list.Select(async device =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested) return;

                    var sw = Stopwatch.StartNew();
                    var (ok, message) = Execute(device);
                    sw.Stop();

                    var result = new OperationResult
                    {
                        Target = string.IsNullOrWhiteSpace(device.SerialNo) ? device.UniformDevID : device.SerialNo,
                        Address = device.IpMaskText,
                        Success = ok,
                        Message = message,
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };

                    int n = Interlocked.Increment(ref _done);
                    UiInvoke(() =>
                    {
                        Results.Add(result);
                        Done = n;
                        Progress = list.Count == 0 ? 0 : n * 100.0 / list.Count;
                        NotifyResultCounters();
                    });

                    OnDeviceProcessed(device, result);
                }
                catch (Exception ex)
                {
                    // 单台设备异常不能让整批停摆
                    int n = Interlocked.Increment(ref _done);
                    var result = new OperationResult
                    {
                        Target = device.UniformDevID,
                        Address = device.IpMaskText,
                        Success = false,
                        Message = $"未预期的异常：{ex.Message}",
                    };
                    UiInvoke(() =>
                    {
                        Results.Add(result);
                        Done = n;
                        Progress = list.Count == 0 ? 0 : n * 100.0 / list.Count;
                        NotifyResultCounters();
                    });
                    Log.Error($"处理 {device.IpMaskText} 时出现未预期异常", ex, LogSource);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Log.Error("批量任务整体异常", ex, LogSource);
            }
        }

        IsRunning = false;
        _cts?.Dispose();
        _cts = null;

        var summary = $"{BusyTextPrefix}完成：成功 {SuccessCount} 台，失败 {FailCount} 台";
        Log.Success(summary, LogSource);
        Toast(summary, FailCount > 0 && SuccessCount == 0);
        AfterRun();
    }

    /// <summary>参数校验，返回非空字符串表示校验不通过（内容即提示语）。</summary>
    protected virtual string? Validate() => null;

    /// <summary>批量开始前的钩子（UI 线程，已完成校验）。用于固化执行计划。</summary>
    protected virtual void OnBeforeRun(IReadOnlyList<DiscoveredDevice> targets) { }

    /// <summary>针对单台设备执行实际操作（在后台线程调用，内部不得直接碰 UI）。</summary>
    protected abstract (bool ok, string message) Execute(DiscoveredDevice device);

    /// <summary>单台处理完成后的回调（后台线程），用于刷新设备缓存字段。</summary>
    protected virtual void OnDeviceProcessed(DiscoveredDevice device, OperationResult result) { }

    /// <summary>整批结束后的回调（后台线程）。</summary>
    protected virtual void AfterRun() { }

    protected abstract string BusyTextPrefix { get; }

    protected virtual string LogSource => "批量";

    /// <summary>
    /// 本页的日志器。protected 而非 private：激活/网络/密码/验收这些子类
    /// 都需要写自己来源（LogSource）的日志。
    /// </summary>
    protected LogService Log => App.Log;

    private void NotifyResultCounters()
    {
        OnPropertyChanged(nameof(SuccessCount));
        OnPropertyChanged(nameof(FailCount));
        OnPropertyChanged(nameof(ResultSummary));
    }

    /// <summary>把动作投递到 UI 线程执行（已在 UI 线程时直接执行）。</summary>
    protected static void UiInvoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { action(); return; }
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, action);
    }

    /// <summary>导出执行结果 CSV，交付时随工单一起存档。</summary>
    private RelayCommand? _exportResultsCommand;
    public RelayCommand ExportResultsCommand => _exportResultsCommand ??= new(() =>
    {
        if (Results.Count == 0) { Toast("没有可导出的执行记录", true); return; }
        try
        {
            var dir = string.IsNullOrWhiteSpace(App.Settings.OutputDir) ? AppPaths.OutputDir : App.Settings.OutputDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{Title}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            CsvUtil.Write(file,
                ["目标", "地址", "结果", "说明", "耗时(ms)", "时间"],
                Results.Select(r => (IReadOnlyList<string>)new[]
                {
                    r.Target, r.Address, r.StatusText, r.Message,
                    r.ElapsedMs.ToString(), r.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                }).ToList());
            Log.Success($"执行结果已导出：{file}", LogSource);
            Toast($"执行结果已导出：{Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Log.Error("导出执行结果失败", ex, LogSource);
            Toast($"导出失败：{ex.Message}", true);
        }
    });
}

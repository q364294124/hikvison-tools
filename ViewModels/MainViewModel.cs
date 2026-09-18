using System.Collections.ObjectModel;
using System.Windows.Threading;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>
/// 主窗口视图模型：页面导航、全局状态栏、Toast 提示、后台忙碌标记。
///
/// 这里同时充当各功能页面的"宿主"（PageViewModel.Host），
/// 页面通过它弹提示、切页面、显示忙碌状态。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _clockTimer;

    private int _busyDepth;
    private string _busyText = string.Empty;
    private string _toastMessage = string.Empty;
    private bool _toastVisible;
    private bool _toastIsError;
    private PageViewModel? _currentPage;
    private string _clock = string.Empty;
    private int _envIssueCount;

    /// <summary>标题条"环境自检待处理"按钮的跳转目标。按类型取，不再按 Pages[7] 硬编码
    /// ——页面顺序按现场动线重排过，硬编码索引迟早指错。</summary>
    public PageViewModel SettingsPage => Pages.OfType<SettingsViewModel>().First();

    public MainViewModel()
    {
        var app = Services.AppState.Current;

        Pages =
        [
            // 页面顺序 = 现场部署动线：搜到设备 → 激活 → 配网 → 改密 →
            // 入库 → 预览/门禁（装完就看效果）→ 验收 → 日志 → 设置。
            new DeviceDiscoveryViewModel(),
            new ActivationViewModel(),
            new NetworkConfigViewModel(),
            new PasswordResetViewModel(),
            new AssetViewModel(),
            new PreviewViewModel(),
            new DoorControlViewModel(),
            new AcceptanceViewModel(),
            new LogViewModel(),
            new SettingsViewModel(),
            new AboutViewModel(),
        ];

        foreach (var page in Pages) page.Host = this;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastVisible = false; };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => Clock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Clock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clockTimer.Start();

        app.DevicesChanged += RefreshCounters;

        // 网段扫描的回调同样来自 SDK 线程，这里统一转到 UI 线程再入集合
        var discovery = Pages.OfType<DeviceDiscoveryViewModel>().FirstOrDefault();
        if (discovery != null)
        {
            app.Sadp.SubnetDeviceFound += item =>
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                try { dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => discovery.PushProbeItem(item))); }
                catch { /* 程序退出过程中忽略 */ }
            };
        }

        CurrentPage = Pages[0];
    }

    public ObservableCollection<PageViewModel> Pages { get; }

    public PageViewModel? CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!Set(ref _currentPage, value) || value == null) return;
            foreach (var p in Pages) p.IsSelected = ReferenceEquals(p, value);
            value.OnEnter();
        }
    }

    // ==================== 状态栏 ====================

    public string AppTitle => AppInfo.ProductName;

    public string AppSubtitle => "SADP + HCNetSDK 双引擎 · 批量部署 · 资产盘点 · 项目验收";

    /// <summary>交付单位。标题栏与关于页共用，取自 CompanyInfo，不在这里另写一份。</summary>
    public string CompanyName => CompanyInfo.Name;

    public string Copyright => CompanyInfo.Copyright;

    /// <summary>状态栏右侧时钟。</summary>
    public string Clock { get => _clock; private set => Set(ref _clock, value); }

    public string SadpVersionText
    {
        get
        {
            var v = Services.AppState.Current.Sadp.VersionText;
            return string.IsNullOrWhiteSpace(v) || v == "未知" ? "SADP 未就绪" : $"SADP {v}";
        }
    }

    public bool IsSadpRunning => Services.AppState.Current.Sadp.IsRunning;

    public bool IsAdministrator { get; private set; }

    public string PrivilegeText => IsAdministrator ? "管理员权限" : "普通权限";

    public int DeviceCount => Services.AppState.Current.Devices.Count;

    public int SelectedCount => Services.AppState.Current.Devices.Count(d => d.IsSelected);

    public int NotActivatedCount => Services.AppState.Current.Devices.Count(d => d.Activate == Models.ActivateState.NotActivated);

    public string CountSummary =>
        $"共 {DeviceCount} 台 · 已选 {SelectedCount} 台 · 未激活 {NotActivatedCount} 台";

    public int EnvIssueCount
    {
        get => _envIssueCount;
        private set
        {
            if (Set(ref _envIssueCount, value)) OnPropertyChanged(nameof(HasEnvIssue));
        }
    }

    // ==================== 忙碌状态 ====================

    public bool IsBusy => _busyDepth > 0;

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    /// <summary>进入"忙碌"状态；使用 using 或手动 Dispose 退出。支持嵌套。</summary>
    public IDisposable BeginBusy(string text)
    {
        _busyDepth++;
        BusyText = text;
        OnPropertyChanged(nameof(IsBusy));
        return new BusyScope(this);
    }

    private sealed class BusyScope(MainViewModel owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._busyDepth = Math.Max(0, owner._busyDepth - 1);
            owner.OnPropertyChanged(nameof(MainViewModel.IsBusy));
            if (owner._busyDepth == 0) owner.BusyText = string.Empty;
        }
    }

    // ==================== Toast ====================

    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }

    public bool ToastIsError { get => _toastIsError; private set => Set(ref _toastIsError, value); }

    public string ToastMessage { get => _toastMessage; private set => Set(ref _toastMessage, value); }

    public void Notify(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ToastMessage = message;
        ToastIsError = isError;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void DismissToast()
    {
        _toastTimer.Stop();
        ToastVisible = false;
    }

    // ==================== 导航 ====================

    public void NavigateTo(PageViewModel page)
    {
        if (page == null) return;
        CurrentPage = page;
    }

    public void NavigateTo<T>() where T : PageViewModel
    {
        var page = Pages.OfType<T>().FirstOrDefault();
        if (page != null) CurrentPage = page;
    }

    private RelayCommand? _navigateCommand;
    public RelayCommand NavigateCommand => _navigateCommand ??= new(obj =>
    {
        if (obj is PageViewModel p) NavigateTo(p);
    });

    // ==================== 计数刷新 ====================

    public void RefreshCounters()
    {
        OnPropertyChanged(nameof(DeviceCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(NotActivatedCount));
        OnPropertyChanged(nameof(CountSummary));
        OnPropertyChanged(nameof(IsSadpRunning));
        OnPropertyChanged(nameof(SadpVersionText));
    }

    public void RefreshPrivilege()
    {
        IsAdministrator = EnvironmentCheck.IsAdministrator();
        OnPropertyChanged(nameof(IsAdministrator));
        OnPropertyChanged(nameof(PrivilegeText));
    }

    public void SetEnvIssueCount(int count) => EnvIssueCount = count;

    public bool HasEnvIssue => EnvIssueCount > 0;

    /// <summary>
    /// 启动后的自检：权限 + 依赖库。
    /// 放在窗口显示之后调用，用户先看到界面再看到结果，不会觉得"程序打不开"；
    /// 而问题在动手操作之前就已经摆在状态栏上了。
    /// </summary>
    public void RunStartupChecks()
    {
        RefreshPrivilege();

        var settings = Pages.OfType<SettingsViewModel>().FirstOrDefault();
        if (settings == null) return;

        int issues = settings.RunCheck();
        SetEnvIssueCount(issues);

        if (settings.HasCritical)
        {
            Notify($"自检发现 {settings.CriticalCount} 项必需依赖缺失，请到「设置与自检」页查看", true);
        }
        else if (!IsAdministrator)
        {
            Notify("当前为普通权限，SADP 搜索可能失败（错误码 2040），建议以管理员身份重启", true);
        }
    }

    public void Dispose()
    {
        _toastTimer.Stop();
        _clockTimer.Stop();
        Services.AppState.Current.DevicesChanged -= RefreshCounters;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using HikDeployTool.Models;
using HikDeployTool.Native;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>过滤规则下拉项。</summary>
public sealed class FilterOption
{
    public string Label { get; init; } = string.Empty;
    public uint Rule { get; init; }
    public string Hint { get; init; } = string.Empty;
}

/// <summary>
/// 设备发现页：SADP 广播搜索 + 跨网段主动探测。
///
/// 设计要点：
///  1. 设备表格直接绑定 AppState.Devices，回调在 AppState 里已做去重与就地更新；
///  2. "投递"按钮不做数据搬运，而是切到目标页——目标页自己读 AppState 的勾选状态，
///     这样只有一份"选中"事实，不会出现两处状态不一致；
///  3. 网段扫描是"广播搜不到"时的补救手段（跨网段、跨 VLAN 的存量设备）。
/// </summary>
public sealed class DeviceDiscoveryViewModel : PageViewModel
{
    private bool _isSearching;
    private string _filterSummary = string.Empty;
    private FilterOption _selectedFilter;
    private uint _searchIntervalSeconds;
    private string _keyword = string.Empty;
    private DiscoveredDevice? _selectedDevice;

    // 网段扫描
    private string _startIp = string.Empty;
    private string _stopIp = string.Empty;
    private bool _ipProbeEnabled = true;
    private bool _portProbeEnabled;
    private ushort _probeSdkPort = 8000;
    private ushort _probeHttpPort = 80;
    private bool _isProbing;
    private byte _probeProgress;
    private string _probeStatus = "未开始";
    private DispatcherTimer? _probeTimer;

    // 命令一律"只创建一次"并缓存。
    // WPF 只对绑定时拿到的那一个 ICommand 实例订阅 CanExecuteChanged，
    // 若属性每次取值都 new 一个，按钮的可用状态就再也不会刷新（还会丢失执行中防连点）。
    private AsyncRelayCommand? _toggleSearchCommand;
    private RelayCommand? _refreshCommand;
    private RelayCommand? _clearCommand;
    private RelayCommand? _selectAllCommand;
    private RelayCommand? _invertSelectionCommand;
    private RelayCommand? _clearSelectionCommand;
    private RelayCommand? _selectNotActivatedCommand;
    private RelayCommand? _goActivateCommand;
    private RelayCommand? _goNetworkCommand;
    private RelayCommand? _goPasswordCommand;
    private RelayCommand? _goAcceptanceCommand;
    private AsyncRelayCommand? _verifyAndAddCommand;
    private RelayCommand? _generateDeviceCodeCommand;
    private RelayCommand? _copyDeviceCodeCommand;
    private RelayCommand? _guessSubnetCommand;
    private AsyncRelayCommand? _toggleProbeCommand;
    private RelayCommand? _copyProbeToClipboardCommand;
    private RelayCommand? _toggleAssetPasswordCommand;

    // 入库验证凭据：设备要登录验证过才允许进台账——台账里的设备
    // 预览/验收页都直接信，一台密码错的设备混进去会污染整本账。
    private string _assetUserName = "admin";
    private string _assetPassword = string.Empty;
    private bool _showAssetPassword;
    private bool _isVerifyingAssets;
    private string _assetVerifyStatus = string.Empty;

    public DeviceDiscoveryViewModel()
    {
        FilterOptions =
        [
            new FilterOption { Label = "显示全部设备", Rule = SadpConst.SADP_DISPLAY_ALL, Hint = "不做任何过滤" },
            new FilterOption { Label = "过滤萤石设备", Rule = SadpConst.SADP_FILTER_EZVIZ, Hint = "隐藏萤石（EZVIZ）系列" },
            new FilterOption { Label = "过滤 OEM 设备", Rule = SadpConst.SADP_FILTER_OEM, Hint = "隐藏 OEM 贴牌设备" },
            new FilterOption { Label = "过滤萤石与 OEM", Rule = SadpConst.SADP_FILTER_EZVIZ_OEM, Hint = "只保留海康自有设备" },
            new FilterOption { Label = "仅显示 OEM 设备", Rule = SadpConst.SADP_ONLY_DISPLAY_OEM, Hint = "只看贴牌设备" },
            new FilterOption { Label = "仅显示萤石设备", Rule = SadpConst.SADP_ONLY_DISPLAY_EZVIZ, Hint = "只看萤石设备" },
        ];
        _selectedFilter = FilterOptions[0];

        ProbeResults = [];
        _searchIntervalSeconds = 0;

        if (!string.IsNullOrWhiteSpace(App.Settings.DefaultUserName)) _assetUserName = App.Settings.DefaultUserName;

        // 设备勾选变化要反映到状态栏计数
        App.Devices.CollectionChanged += OnDevicesCollectionChanged;
        foreach (var d in App.Devices) Hook(d);
    }

    public ObservableCollection<DiscoveredDevice> Devices => App.Devices;

    public ObservableCollection<SubnetProbeItem> ProbeResults { get; }

    public IReadOnlyList<FilterOption> FilterOptions { get; }

    public override string Title => "设备发现";

    public override string Glyph => "IconSearch";

    public override string Description => "搜索局域网内在线的海康设备，查看型号、固件、IP 与激活状态";

    public override string Usage => "现场开工第一步：先让设备出现在列表里，再勾选后投递到激活 / 网络 / 密码 / 验收页。";

    // ==================== 搜索控制 ====================

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (Set(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(SearchButtonText));
                OnPropertyChanged(nameof(SearchStateText));
                Host?.RefreshCounters();
            }
        }
    }

    public string SearchButtonText => IsSearching ? "停止搜索" : "开始搜索";

    public string SearchStateText => IsSearching ? "搜索中…" : "未启动";

    public string FilterSummary { get => _filterSummary; private set => Set(ref _filterSummary, value); }

    public FilterOption SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!Set(ref _selectedFilter, value)) return;
            ApplyFilter();
        }
    }

    /// <summary>自动搜索间隔（秒），0 表示使用 SDK 默认（60 秒）。</summary>
    public uint SearchIntervalSeconds
    {
        get => _searchIntervalSeconds;
        set
        {
            if (!Set(ref _searchIntervalSeconds, value)) return;
            App.Sadp.SetAutoRequestInterval(value);
            Log.Info($"自动搜索间隔已设置为 {(value == 0 ? "默认（60 秒）" : value + " 秒")}", LogSource);
        }
    }

    public DiscoveredDevice? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    // ==================== 搜索命令 ====================

    public AsyncRelayCommand ToggleSearchCommand => _toggleSearchCommand ??= new(ToggleSearchAsync);

    private async Task ToggleSearchAsync()
    {
        if (App.Sadp.IsRunning)
        {
            using (Busy("正在停止搜索…"))
            {
                await Task.Run(() => App.Sadp.Stop());
            }
            IsSearching = false;
            Toast("搜索已停止");
            Host?.RefreshCounters();
            return;
        }

        using (Busy("正在启动 SADP 服务…"))
        {
            var result = await Task.Run(() => App.Sadp.Start(App.Settings.SdkLogLevel, App.Settings.SdkLogDir));
            if (result != 0)
            {
                IsSearching = false;
                var hint = result switch
                {
                    2030 => "缺少 wpcap.dll，请安装 Npcap（勾选 WinPcap 兼容模式）后重启程序。",
                    2040 => "需要管理员权限，点左下角「以管理员身份重启」。",
                    2002 => "Sadp.dll 加载失败，请确认 Sadp.dll、HCNetSDK.dll 与 HCSadpSDK.xml 已放在程序目录。",
                    _ => "请查看「运行日志」页的详细错误信息。",
                };
                Log.Error($"SADP 启动失败（错误码 {result}）：{SadpError.Describe((uint)result)}", hint, LogSource);
                Toast($"启动搜索失败：{SadpError.Describe((uint)result)}（{hint}）", true);
                return;
            }

            // 启动成功后立刻下发一次搜索报文，不用等 SDK 的定时周期
            await Task.Run(() => App.Sadp.SendInquiry());
        }

        IsSearching = true;
        ApplyFilter();
        App.Sadp.SetAutoRequestInterval(SearchIntervalSeconds);
        Log.Success("SADP 搜索已启动，正在监听设备广播…", LogSource);
        Toast("搜索已启动，新上线设备会自动出现在列表中");
        Host?.RefreshCounters();
    }

    public RelayCommand RefreshCommand => _refreshCommand ??= new(() =>
    {
        if (!App.Sadp.IsRunning)
        {
            Toast("请先开始搜索", true);
            return;
        }
        var (ok, msg) = App.Sadp.SendInquiry();
        if (!ok) { Toast($"发送搜索请求失败：{msg}", true); return; }
        Log.Info("已手动下发一次搜索请求", LogSource);
        Toast("已发送搜索请求，稍候即可看到设备");
    });

    /// <summary>清空列表并让 SDK 忘记旧设备，常用于换网段后重新盘点。</summary>
    public RelayCommand ClearCommand => _clearCommand ??= new(() =>
    {
        App.ClearDevices(clearSdkCache: true);
        ProbeResults.Clear();
        SelectedDevice = null;
        Host?.RefreshCounters();
        Toast("设备列表已清空");
    });

    private void ApplyFilter()
    {
        if (!App.Sadp.IsRunning)
        {
            FilterSummary = "搜索未启动，过滤规则将在开始搜索时生效";
            return;
        }
        var (ok, msg) = App.Sadp.SetFilterRule(SelectedFilter.Rule);
        FilterSummary = ok ? $"当前规则：{SelectedFilter.Label}" : $"设置过滤规则失败：{msg}";
        if (!ok) Toast(FilterSummary, true);
        else Log.Info($"过滤规则已切换为「{SelectedFilter.Label}」", LogSource);
    }

    // ==================== 勾选 ====================

    private void OnDevicesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (DiscoveredDevice d in e.NewItems) Hook(d);
        if (e.OldItems != null)
            foreach (DiscoveredDevice d in e.OldItems) Unhook(d);
        Host?.RefreshCounters();
    }

    private void Hook(DiscoveredDevice d)
    {
        d.PropertyChanged -= OnDevicePropertyChanged;
        d.PropertyChanged += OnDevicePropertyChanged;
    }

    private void Unhook(DiscoveredDevice d) => d.PropertyChanged -= OnDevicePropertyChanged;

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoveredDevice.IsSelected)) Host?.RefreshCounters();
    }

    public RelayCommand SelectAllCommand => _selectAllCommand ??= new(() =>
    {
        foreach (var d in Devices) d.IsSelected = true;
        Host?.RefreshCounters();
    });

    public RelayCommand InvertSelectionCommand => _invertSelectionCommand ??= new(() =>
    {
        foreach (var d in Devices) d.IsSelected = !d.IsSelected;
        Host?.RefreshCounters();
    });

    public RelayCommand ClearSelectionCommand => _clearSelectionCommand ??= new(() =>
    {
        foreach (var d in Devices) d.IsSelected = false;
        Host?.RefreshCounters();
    });

    /// <summary>只勾选未激活设备（交付现场最常用的一步）。</summary>
    public RelayCommand SelectNotActivatedCommand => _selectNotActivatedCommand ??= new(() =>
    {
        int n = 0;
        foreach (var d in Devices)
        {
            d.IsSelected = d.Activate == ActivateState.NotActivated;
            if (d.IsSelected) n++;
        }
        Host?.RefreshCounters();
        Toast(n > 0 ? $"已勾选 {n} 台未激活设备" : "当前列表中没有未激活设备", n == 0);
    });

    // ==================== 投递到其它页面 ====================

    private bool RequireSelection()
    {
        if (PickedDevices.Count > 0) return true;
        Toast("请先在列表中勾选需要处理的设备", true);
        return false;
    }

    public RelayCommand GoActivateCommand => _goActivateCommand ??= new(() =>
    {
        if (!RequireSelection()) return;
        GoTo<ActivationViewModel>();
        Toast($"已投递 {PickedDevices.Count} 台设备到「批量激活」");
    });

    public RelayCommand GoNetworkCommand => _goNetworkCommand ??= new(() =>
    {
        if (!RequireSelection()) return;
        GoTo<NetworkConfigViewModel>();
        Toast($"已投递 {PickedDevices.Count} 台设备到「网络配置」");
    });

    public RelayCommand GoPasswordCommand => _goPasswordCommand ??= new(() =>
    {
        if (!RequireSelection()) return;
        GoTo<PasswordResetViewModel>();
        Toast($"已投递 {PickedDevices.Count} 台设备到「密码重置」");
    });

    public RelayCommand GoAcceptanceCommand => _goAcceptanceCommand ??= new(() =>
    {
        if (!RequireSelection()) return;
        GoTo<AcceptanceViewModel>();
        Toast($"已投递 {PickedDevices.Count} 台设备到「项目验收」");
    });

    // ==================== 入库凭据与验证入库 ====================

    /// <summary>入库验证用的用户名（现场设备通常统一都是 admin）。</summary>
    public string AssetUserName
    {
        get => _assetUserName;
        set => Set(ref _assetUserName, value);
    }

    /// <summary>入库验证用的口令。验证通过即认为凭据正确，随记录写入台账。</summary>
    public string AssetPassword
    {
        get => _assetPassword;
        set => Set(ref _assetPassword, value);
    }

    public bool ShowAssetPassword
    {
        get => _showAssetPassword;
        set => Set(ref _showAssetPassword, value);
    }

    public RelayCommand ToggleAssetPasswordCommand =>
        _toggleAssetPasswordCommand ??= new(() => ShowAssetPassword = !ShowAssetPassword);

    public bool IsVerifyingAssets
    {
        get => _isVerifyingAssets;
        private set
        {
            if (!Set(ref _isVerifyingAssets, value)) return;
            // 注意：这里**不能**写成 VerifyAndAddCommand.RaiseCanExecuteChanged()。
            // 该属性用 ??= 惰性创建命令，而 AsyncRelayCommand 构造时会调一次
            // CanExecute（读 IsVerifyingAssets），若首次访问发生在 setter 里
            // 就会形成 属性 → 建命令 → 读属性 的自引用，表现为切换页面时挂死。
            // 用字段判空，命令真被创建过才通知。
            _verifyAndAddCommand?.RaiseCanExecuteChanged();
        }
    }

    public string AssetVerifyStatus { get => _assetVerifyStatus; private set => Set(ref _assetVerifyStatus, value); }

    /// <summary>
    /// 验证并入库：勾选的设备逐台登录验证，通过的才并入台账。
    /// 未激活设备登录必然失败，直接拦下来提示先去激活——不混进验证队列
    /// （一台一台等 5 秒超时很折磨人），也不允许只凭 SADP 信息入库。
    /// </summary>
    public AsyncRelayCommand VerifyAndAddCommand => _verifyAndAddCommand ??=
        new AsyncRelayCommand(VerifyAndAddToAssetsAsync, () => !IsVerifyingAssets);

    private async Task VerifyAndAddToAssetsAsync()
    {
        if (!RequireSelection()) return;

        var picked = PickedDevices.ToList();

        // 分拣：未激活的拦下，不进验证队列
        var notActivated = picked.Where(d => d.Activate == ActivateState.NotActivated).ToList();
        var toVerify = picked.Where(d => d.Activate != ActivateState.NotActivated).ToList();

        if (notActivated.Count > 0)
        {
            // 保持勾选状态，用户点「批量激活」直接带过去
            var msg = $"{notActivated.Count} 台设备未激活，不能入库：先到「批量激活」处理，激活后再回来入库";
            Log.Warn(msg, LogSource);
            Toast(msg, true);
        }

        if (toVerify.Count == 0)
        {
            if (notActivated.Count > 0) GoTo<ActivationViewModel>();
            return;
        }

        if (string.IsNullOrWhiteSpace(AssetPassword))
        {
            Toast("请先填写入库验证口令（设备登录密码）", true);
            return;
        }

        IsVerifyingAssets = true;
        var user = _assetUserName;
        var password = _assetPassword;

        var passed = new List<DiscoveredDevice>();
        var failures = new List<string>();
        int index = 0;

        foreach (var device in toVerify)
        {
            index++;
            AssetVerifyStatus = $"正在验证 {index}/{toVerify.Count} · {device.IPv4Address} …";
            var d = device;
            var (ok, message) = await Task.Run<(bool, string)>(() =>
            {
                var session = App.NetSdk.Login(d.IPv4Address, d.SdkPort, user, password, d.HttpPort);
                if (!session.Ok) return (false, session.Message);
                // 验证完就登出：预览/验收页用时会自己登录，这里只验明凭据
                App.NetSdk.Logout(session.UserId);
                return (true, string.Empty);
            }).ConfigureAwait(true);

            if (ok) passed.Add(d);
            else failures.Add($"{device.IPv4Address}（{device.ModelText}）：{message}");
        }

        // 通过的设备写台账，同时把验证过的用户名/标记带上。
        // 口令也随台账保存（DPAPI 加密，assets.json 只落密文）——
        // 预览页凭它实现"入库设备免密直连"，不用每次再输密码。
        int added = 0, updated = 0;
        if (passed.Count > 0)
        {
            (added, updated) = App.MergeIntoAssets(passed, verified: true, userName: user);
            foreach (var d in passed)
            {
                d.AdminUserName = user;
                var asset = FindAsset(d);
                if (asset != null) asset.Password = password;
            }
            App.SaveAssets();   // MergeIntoAssets 内部存过一次，但口令是之后才写上的
        }

        AssetVerifyStatus = string.Empty;
        IsVerifyingAssets = false;

        if (passed.Count > 0)
        {
            var okMsg = $"已验证并入台账：新增 {added} 条，更新 {updated} 条";
            Log.Success(okMsg, "台账");
            if (failures.Count == 0) Toast(okMsg);
        }

        if (failures.Count > 0)
        {
            var failMsg = $"{failures.Count} 台验证失败未入库：\n{string.Join("\n", failures)}";
            Log.Warn(failMsg, LogSource);
            Toast(failMsg, true);
        }
    }

    /// <summary>按序列号优先、IP 兜底找回台账里对应的记录（与 MergeIntoAssets 的匹配口径一致）。</summary>
    private static AssetRecord? FindAsset(DiscoveredDevice d)
    {
        if (!string.IsNullOrWhiteSpace(d.SerialNo))
        {
            var bySerial = App.Assets.FirstOrDefault(a =>
                string.Equals(a.SerialNo, d.SerialNo, StringComparison.OrdinalIgnoreCase));
            if (bySerial != null) return bySerial;
        }
        return App.Assets.FirstOrDefault(a =>
            string.Equals(a.Ip, d.IPv4Address, StringComparison.OrdinalIgnoreCase));
    }

    public RelayCommand GenerateDeviceCodeCommand => _generateDeviceCodeCommand ??= new(() =>
    {
        if (SelectedDevice == null) { Toast("请先在列表中选择一台设备", true); return; }
        var d = SelectedDevice;

        // 三路兜底：V31 → 旧命令 → MAC 寻址，内部已经封装好，失败会回最具体的 SADP 错误码
        var r = App.Sadp.GetDeviceCode(d.UniformDevID, d.Mac);

        if (!r.ok) { Log.Warn($"读取设备码失败：{r.message}", LogSource); Toast($"读取设备码失败：{r.message}", true); return; }

        LastDeviceCode = r.code;
        OnPropertyChanged(nameof(LastDeviceCode));
        Log.Success($"设备码读取成功（{d.IpMaskText}）：{r.code}", LogSource);
        Toast("设备码读取成功，已显示在下方");
    });

    private string _lastDeviceCode = string.Empty;

    public string LastDeviceCode { get => _lastDeviceCode; private set => Set(ref _lastDeviceCode, value); }

    public RelayCommand CopyDeviceCodeCommand => _copyDeviceCodeCommand ??= new(() =>
    {
        if (string.IsNullOrWhiteSpace(LastDeviceCode)) return;
        try
        {
            System.Windows.Clipboard.SetText(LastDeviceCode);
            Toast("设备码已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Toast($"复制失败：{ex.Message}", true);
        }
    });

    // ==================== 网段扫描 ====================

    public string StartIp { get => _startIp; set => Set(ref _startIp, value); }

    public string StopIp { get => _stopIp; set => Set(ref _stopIp, value); }

    public bool IpProbeEnabled { get => _ipProbeEnabled; set => Set(ref _ipProbeEnabled, value); }

    public bool PortProbeEnabled { get => _portProbeEnabled; set => Set(ref _portProbeEnabled, value); }

    public ushort ProbeSdkPort { get => _probeSdkPort; set => Set(ref _probeSdkPort, value); }

    public ushort ProbeHttpPort { get => _probeHttpPort; set => Set(ref _probeHttpPort, value); }

    public bool IsProbing
    {
        get => _isProbing;
        private set
        {
            if (Set(ref _isProbing, value))
            {
                OnPropertyChanged(nameof(ProbeButtonText));
                OnPropertyChanged(nameof(ProbeProgressText));
            }
        }
    }

    public string ProbeButtonText => IsProbing ? "停止扫描" : "开始扫描";

    public byte ProbeProgress
    {
        get => _probeProgress;
        private set
        {
            if (Set(ref _probeProgress, value)) OnPropertyChanged(nameof(ProbeProgressText));
        }
    }

    public string ProbeProgressText => $"{ProbeProgress}%";

    public string ProbeStatus { get => _probeStatus; private set => Set(ref _probeStatus, value); }

    /// <summary>按本机网卡自动填一个合理的扫描范围。</summary>
    public RelayCommand GuessSubnetCommand => _guessSubnetCommand ??= new(() =>
    {
        var local = FindLocalIPv4();
        if (local == null) { Toast("未找到可用的本机 IPv4 地址", true); return; }
        var parts = local.Split('.');
        StartIp = $"{parts[0]}.{parts[1]}.{parts[2]}.1";
        StopIp = $"{parts[0]}.{parts[1]}.{parts[2]}.254";
        Toast($"已按本机地址 {local} 填充扫描范围");
    });

    private static string? FindLocalIPv4()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    return ip;
                }
            }
        }
        catch { /* 忽略 */ }
        return null;
    }

    public AsyncRelayCommand ToggleProbeCommand => _toggleProbeCommand ??= new(ToggleProbeAsync);

    private async Task ToggleProbeAsync()
    {
        if (IsProbing)
        {
            StopProbe();
            return;
        }

        if (!IsValidIp(StartIp) || !IsValidIp(StopIp))
        {
            Toast("请填写合法的起始 / 结束 IP（例如 192.168.1.1）", true);
            return;
        }
        if (!IpProbeEnabled && !PortProbeEnabled)
        {
            Toast("至少启用「IP 探测」或「端口探测」中的一项", true);
            return;
        }

        var options = new SubnetProbeOptions
        {
            StartIp = StartIp.Trim(),
            StopIp = StopIp.Trim(),
            IpProbeEnabled = IpProbeEnabled,
            PortProbeEnabled = PortProbeEnabled,
            SdkPort = ProbeSdkPort,
            HttpPort = ProbeHttpPort,
        };

        ProbeResults.Clear();
        ProbeProgress = 0;
        ProbeStatus = "正在扫描…";

        var (ok, msg) = await Task.Run(() => App.Sadp.StartSubnetProbe(options));
        if (!ok)
        {
            ProbeStatus = "启动失败";
            Log.Error($"网段扫描启动失败：{msg}", LogSource);
            Toast($"网段扫描启动失败：{msg}", true);
            return;
        }

        IsProbing = true;
        Log.Info($"开始网段扫描 {options.StartIp} ~ {options.StopIp}", LogSource);

        _probeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _probeTimer.Tick -= OnProbeTick;
        _probeTimer.Tick += OnProbeTick;
        _probeTimer.Start();
    }

    private void OnProbeTick(object? sender, EventArgs e)
    {
        var (status, progress) = App.Sadp.GetSubnetProbeStatus();
        ProbeProgress = progress;
        ProbeStatus = status switch
        {
            0 => "未开始",
            1 => "正在扫描…",
            2 => "扫描完成",
            3 => "已停止",
            _ => $"状态 {status}",
        };
        if (status is 2 or 3) StopProbe();
    }

    private void StopProbe()
    {
        _probeTimer?.Stop();
        if (IsProbing) App.Sadp.StopSubnetProbe();
        IsProbing = false;
        ProbeStatus = ProbeProgress >= 100 ? "扫描完成" : "已停止";
        Log.Info($"网段扫描结束，命中 {ProbeResults.Count} 个存活点", LogSource);
        Toast($"网段扫描结束，命中 {ProbeResults.Count} 个存活点");
    }

    private static bool IsValidIp(string? ip)
        => !string.IsNullOrWhiteSpace(ip) && System.Net.IPAddress.TryParse(ip.Trim(), out _);

    /// <summary>把网段扫描结果复制成待处理清单（只有 IP，没有序列号）。</summary>
    public RelayCommand CopyProbeToClipboardCommand => _copyProbeToClipboardCommand ??= new(() =>
    {
        if (ProbeResults.Count == 0) { Toast("扫描结果为空", true); return; }
        try
        {
            var text = string.Join(Environment.NewLine, ProbeResults.Select(p => $"{p.IP}:{p.Port}\t{p.Protocol}"));
            System.Windows.Clipboard.SetText(text);
            Toast($"已复制 {ProbeResults.Count} 条扫描结果");
        }
        catch (Exception ex)
        {
            Toast($"复制失败：{ex.Message}", true);
        }
    });

    public void PushProbeItem(SubnetProbeItem item)
    {
        // 同 IP 只保留一条
        if (ProbeResults.Any(p => p.IP == item.IP && p.Port == item.Port)) return;
        ProbeResults.Add(item);
    }

    public override void OnEnter()
    {
        Host?.RefreshCounters();
    }

    private static LogService Log => App.Log;

    private const string LogSource = "发现";
}

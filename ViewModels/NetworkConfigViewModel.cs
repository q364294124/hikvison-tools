using System.Collections.ObjectModel;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>分配计划中的一行：某台设备将改成哪个 IP。</summary>
public sealed class IpAssignment
{
    public string Target { get; init; } = string.Empty;
    public string OldIp { get; set; } = string.Empty;
    public string NewIp { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 网络配置页：批量改 IP / 掩码 / 网关 / 端口。
///
/// 现场最典型的动作是"新设备全是 192.168.1.64，按顺序排到 192.168.1.101 开始"，
/// 所以这里做了"起始 IP + 步长 + 顺序分配"的计划预览：
/// 执行前先看到"谁要改成什么"，比执行完再排查要省事得多。
/// </summary>
public sealed class NetworkConfigViewModel : DeviceTaskPageViewModel
{
    private bool _sequential;
    private string _startIp = string.Empty;
    private int _step = 1;
    private string _subnetMask = "255.255.255.0";
    private string _gateway = string.Empty;
    private ushort _sdkPort = 8000;
    private ushort _httpPort = 80;
    private uint _sdkOverTlsPort;
    private bool _dhcpEnabled;
    private string _password = string.Empty;

    /// <summary>执行计划快照：开始执行时固定下来，执行过程中用户改参数也不会错位。</summary>
    private readonly Dictionary<string, IpAssignment> _plan = new(StringComparer.OrdinalIgnoreCase);

    public NetworkConfigViewModel()
    {
        Assignments = [];
        RebuildPlanCommand = new RelayCommand(RebuildPlan);
        GuessGatewayCommand = new RelayCommand(() =>
        {
            if (!IpHelper.TryToUInt(StartIp, out var v)) { Toast("请先填写合法的起始 IP", true); return; }
            // 默认把网关猜成网段内的 .1（绝大多数工程的做法）
            var parts = StartIp.Trim().Split('.');
            Gateway = $"{parts[0]}.{parts[1]}.{parts[2]}.1";
            Toast($"网关已填为 {Gateway}，请按现场实际情况确认");
        });

        Targets.CollectionChanged += (_, _) => RebuildPlan();
    }

    public ObservableCollection<IpAssignment> Assignments { get; }

    public RelayCommand RebuildPlanCommand { get; }

    public RelayCommand GuessGatewayCommand { get; }

    public override string Title => "网络配置";

    public override string Glyph => "IconNetwork";

    public override string Description => "批量修改设备 IP、子网掩码、网关与端口，支持按顺序自动分配";

    public override string Usage => "改完 IP 设备会立刻断链重启，务必确认计划无误。改动前的 IP 仍可在「资产台账」里查到。";

    protected override string BusyTextPrefix => "批量改网络参数";

    protected override string LogSource => "网络";

    // ==================== 参数 ====================

    /// <summary>true = 按顺序从起始 IP 递增分配；false = 只改掩码/网关/端口，IP 保持不变。</summary>
    public bool Sequential
    {
        get => _sequential;
        set
        {
            if (!Set(ref _sequential, value)) return;
            OnPropertyChanged(nameof(PlanTitle));
            RebuildPlan();
        }
    }

    public string StartIp
    {
        get => _startIp;
        set
        {
            if (Set(ref _startIp, value)) RebuildPlan();
        }
    }

    public int Step
    {
        get => _step;
        set
        {
            if (Set(ref _step, Math.Clamp(value, 1, 64))) RebuildPlan();
        }
    }

    public string SubnetMask
    {
        get => _subnetMask;
        set
        {
            if (!Set(ref _subnetMask, value)) return;
            OnPropertyChanged(nameof(MaskHint));
            RebuildPlan();
        }
    }

    public string MaskHint
    {
        get
        {
            int prefix = IpHelper.MaskToPrefixLength(SubnetMask);
            return prefix < 0
                ? "掩码格式不正确（应为连续 1 的掩码，例如 255.255.255.0）"
                : $"/{prefix} · 可用地址 {Math.Max(0, (1L << (32 - prefix)) - 2)} 个";
        }
    }

    public string Gateway
    {
        get => _gateway;
        set
        {
            if (!Set(ref _gateway, value)) return;
            OnPropertyChanged(nameof(SubnetCheckText));
        }
    }

    public ushort SdkPort { get => _sdkPort; set => Set(ref _sdkPort, value); }

    public ushort HttpPort { get => _httpPort; set => Set(ref _httpPort, value); }

    public uint SdkOverTlsPort { get => _sdkOverTlsPort; set => Set(ref _sdkOverTlsPort, value); }

    public bool DhcpEnabled { get => _dhcpEnabled; set => Set(ref _dhcpEnabled, value); }

    public string Password { get => _password; set => Set(ref _password, value); }

    public string PlanTitle => Sequential ? "分配计划（按顺序自动分配）" : "分配计划（IP 保持不变）";

    public string PlanSummary => Assignments.Count == 0
        ? "载入设备后这里会显示每台设备的目标 IP"
        : $"共 {Assignments.Count} 台：" +
          $"{Assignments.FirstOrDefault()?.OldIp} → {Assignments.FirstOrDefault()?.NewIp}" +
          (Assignments.Count > 1 ? $" … {Assignments.Last().OldIp} → {Assignments.Last().NewIp}" : string.Empty);

    /// <summary>与本机网段的对照提示——设备跨网段会改完就失联。</summary>
    public string SubnetCheckText
    {
        get
        {
            if (!IpHelper.IsValid(StartIp) || IpHelper.MaskToPrefixLength(SubnetMask) < 0) return string.Empty;
            var local = FindLocalIPv4();
            if (local == null) return string.Empty;
            return IpHelper.IsSameSubnet(local, StartIp, SubnetMask)
                ? $"目标网段与本机（{local}）在同一网段，改完仍可搜索到"
                : $"注意：目标网段与本机（{local}）不在同一网段，改完 IP 后将无法通过广播搜索，请确认路由可达";
        }
    }

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

    // ==================== 计划 ====================

    private void RebuildPlan()
    {
        Assignments.Clear();

        long offset = 0;
        foreach (var d in Targets)
        {
            string newIp = d.IPv4Address;
            string note = string.Empty;

            if (Sequential)
            {
                if (!IpHelper.TryAddOffset(StartIp, offset, out newIp))
                {
                    newIp = "（起始 IP 无效）";
                    note = "请检查起始 IP";
                }
                offset += Math.Max(1, Step);
            }
            else if (!IpHelper.IsValid(d.IPv4Address))
            {
                note = "设备当前 IP 无效";
            }

            if (DhcpEnabled) note = string.IsNullOrEmpty(note) ? "将改为 DHCP 自动获取" : note + "；将改为 DHCP";

            Assignments.Add(new IpAssignment
            {
                Target = string.IsNullOrWhiteSpace(d.SerialNo) ? d.UniformDevID : d.SerialNo,
                OldIp = d.IpMaskText,
                NewIp = DhcpEnabled ? "（DHCP）" : newIp,
                Note = note,
            });
        }

        // 撞车检测：计划里出现了重复 IP，或者新 IP 和设备原有 IP 冲突
        var dup = Assignments.Where(a => IpHelper.IsValid(a.NewIp))
                             .GroupBy(a => a.NewIp)
                             .Where(g => g.Count() > 1)
                             .Select(g => g.Key).ToList();
        foreach (var a in Assignments.Where(a => dup.Contains(a.NewIp)))
            a.Note = string.IsNullOrEmpty(a.Note) ? "计划内 IP 重复" : a.Note + "；计划内 IP 重复";

        var existing = App.Devices.Where(d => !Targets.Contains(d)).Select(d => d.IPv4Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Assignments.Where(a => IpHelper.IsValid(a.NewIp) && existing.Contains(a.NewIp)))
            a.Note = string.IsNullOrEmpty(a.Note) ? "与列表中其它设备 IP 冲突" : a.Note + "；与列表中其它设备 IP 冲突";

        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(SubnetCheckText));
    }

    protected override void OnTargetsChanged()
    {
        base.OnTargetsChanged();
        RebuildPlan();
    }

    public override void OnEnter()
    {
        // 首次进入时给一个合理的默认起始 IP，省得用户手敲
        if (!Sequential && string.IsNullOrWhiteSpace(StartIp))
        {
            var local = FindLocalIPv4();
            if (local != null && IpHelper.TryAddOffset(local, 100, out var guess)) StartIp = guess;
        }
        RebuildPlan();
    }

    // ==================== 执行 ====================

    protected override string? Validate()
    {
        if (string.IsNullOrEmpty(Password)) return "请输入设备当前口令（修改网络参数需要身份校验）";

        if (IpHelper.MaskToPrefixLength(SubnetMask) < 0) return "子网掩码不合法，请填写连续 1 的掩码";

        if (Sequential)
        {
            if (!IpHelper.IsValid(StartIp)) return "起始 IP 不合法";
            if (!string.IsNullOrEmpty(Gateway) && !IpHelper.IsValid(Gateway)) return "网关地址不合法";
            if (Assignments.Any(a => a.Note.Contains("重复"))) return "分配计划中存在重复 IP，请调整起始 IP 或步长";
        }

        if (!DhcpEnabled && Assignments.Any(a => a.NewIp.StartsWith("（")))
            return "分配计划不完整，请检查起始 IP 与步长";

        if (SdkPort == 0 && HttpPort == 0) return "SDK 端口与 HTTP 端口不能同时为 0";

        return null;
    }

    protected override (bool ok, string message) Execute(DiscoveredDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.UniformDevID))
            return (false, "设备唯一标识为空，无法下发网络参数");

        var input = new NetParamInput
        {
            SubnetMask = SubnetMask.Trim(),
            Gateway = Gateway.Trim(),
            SdkPort = SdkPort,
            HttpPort = HttpPort,
            SdkOverTlsPort = SdkOverTlsPort,
            DhcpEnabled = DhcpEnabled,
            IPv6Address = device.IPv6Address,
            IPv6Gateway = device.IPv6Gateway,
            IPv6MaskLen = device.IPv6MaskLen,
        };

        if (Sequential && _plan.TryGetValue(device.Key, out var plan) && IpHelper.IsValid(plan.NewIp))
        {
            input.IPv4Address = plan.NewIp;
        }
        else
        {
            // 不启用顺序分配时，保持设备原 IP 不变（只改掩码/网关/端口）
            input.IPv4Address = device.IPv4Address;
            if (!DhcpEnabled && !IpHelper.IsValid(input.IPv4Address))
                return (false, "设备当前 IP 无效，无法在不指定新 IP 的情况下下发参数");
        }

        var (ok, msg, retry, lockMin) = App.Sadp.ModifyNetParam(device.UniformDevID, Password, input);

        if (ok) return (true, $"已改为 {(DhcpEnabled ? "DHCP" : input.IPv4Address)}，设备即将重启");

        // 把"剩余可尝试次数/锁定时间"原样透出，避免用户反复试错把设备锁死
        if (retry > 0 || lockMin > 0)
            msg += $"（剩余尝试 {retry} 次，锁定 {lockMin} 分钟）";

        return (false, msg);
    }

    protected override void OnDeviceProcessed(DiscoveredDevice device, OperationResult result)
    {
        if (!result.Success) return;
        if (!_plan.TryGetValue(device.Key, out var plan)) return;

        UiInvoke(() =>
        {
            // 只是本地的乐观更新：设备重启后 SADP 会用真实值覆盖
            if (!DhcpEnabled && IpHelper.IsValid(plan.NewIp)) device.IPv4Address = plan.NewIp;
            device.IPv4SubnetMask = SubnetMask.Trim();
            device.IPv4Gateway = Gateway.Trim();
            device.HttpPort = HttpPort;
            device.DhcpEnabled = DhcpEnabled;
        });
    }

    protected override void AfterRun()
    {
        if (SuccessCount > 0)
            Toast($"已下发 {SuccessCount} 台设备的新网络参数。设备重启后请回到「设备发现」页搜索确认。");
    }

    /// <summary>执行前固化计划：把"设备 → 目标 IP"的映射固定下来，
    /// 这样执行过程中即使用户改了起始 IP，已发出的请求也不会错位。</summary>
    protected override void OnBeforeRun(IReadOnlyList<DiscoveredDevice> targets)
    {
        _plan.Clear();
        RebuildPlan();

        int i = 0;
        foreach (var d in targets)
        {
            if (i < Assignments.Count) _plan[d.Key] = Assignments[i];
            i++;
        }
        Log.Info($"已固化网络变更计划：{_plan.Count} 台", LogSource);
    }
}

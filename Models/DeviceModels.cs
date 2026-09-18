using HikDeployTool.ViewModels;

namespace HikDeployTool.Models;

/// <summary>设备在 SADP 列表中的状态（对应 SADP_DEVICE_INFO.iResult）。</summary>
public enum DeviceLinkState
{
    Online,      // 在线
    New,         // 新上线
    Updated,     // 参数更新
    Offline,     // 已下线
    UpdateFail,  // 更新失败
}

/// <summary>设备激活状态。</summary>
public enum ActivateState
{
    Unknown,
    NotActivated,
    Activated,
}

/// <summary>
/// 一台被 SADP 发现的设备。字段基本一一对应 SADP_DEVICE_INFO_V40，
/// 额外增加 UI 需要的选择态、采集时间和备注。
/// </summary>
public sealed class DiscoveredDevice : ObservableObject
{
    private bool _isSelected;

    // ---- 唯一标识（接口传参必须用它，不要用 IP） ----
    public string UniformDevID { get; set; } = string.Empty;

    // ---- 基础信息 ----
    public string SerialNo { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string IPv4Address { get; set; } = string.Empty;
    public string IPv4SubnetMask { get; set; } = string.Empty;
    public string IPv4Gateway { get; set; } = string.Empty;
    public string IPv6Address { get; set; } = string.Empty;
    public string IPv6Gateway { get; set; } = string.Empty;
    public byte IPv6MaskLen { get; set; }

    public uint DeviceType { get; set; }
    public string DeviceDesc { get; set; } = string.Empty;
    public string DeviceDescEx { get; set; } = string.Empty;
    public string BaseDesc { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string OemInfo { get; set; } = string.Empty;
    public uint DetailOemCode { get; set; }

    public ushort SdkPort { get; set; } = 8000;
    public ushort HttpPort { get; set; }
    public ushort HttpsPort { get; set; }
    public uint SdkOverTlsPort { get; set; }
    public ushort OemCommandPort { get; set; }
    public ushort CmsPort { get; set; }
    public string CmsIPv4 { get; set; } = string.Empty;

    public uint EncoderCount { get; set; }
    public ushort DigitalChannelCount { get; set; }
    public uint HardDiskCount { get; set; }

    public string SoftwareVersion { get; set; } = string.Empty;
    public string DspVersion { get; set; } = string.Empty;
    public string BootTime { get; set; } = string.Empty;
    public string EhomeVersion { get; set; } = string.Empty;
    public string SecuritySuite { get; set; } = string.Empty;
    public string AdminUserName { get; set; } = string.Empty;

    public bool DhcpEnabled { get; set; }
    public bool IsEzViz { get; set; }
    public bool IsOem { get; set; }
    public bool IsLicenseMissing { get; set; }
    public bool IsDiscoveryOnly { get; set; }
    public bool IsMulticast { get; set; }
    public bool SupportModifyIpv6 { get; set; }
    public bool SupportResetPasswd { get; set; }
    public bool SupportResetPasswdByCode { get; set; }
    public bool SupportGuidReset { get; set; }
    public bool SupportQuestionReset { get; set; }
    public bool SupportMailReset { get; set; }
    public bool SupportPhoneReset { get; set; }
    public bool SupportFactoryReset { get; set; }
    public bool SupportSyncIpcPasswd { get; set; }

    public ActivateState Activate { get; set; } = ActivateState.Unknown;
    public DeviceLinkState LinkState { get; set; } = DeviceLinkState.New;

    /// <summary>本机收到该设备最后一帧信息的时刻。</summary>
    public DateTime LastSeen { get; set; } = DateTime.Now;

    public string? Note { get; set; }

    /// <summary>表格里的勾选状态（不参与设备信息比较）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    // ---- 供界面直接绑定的派生属性 ----
    public string LinkStateText => LinkState switch
    {
        DeviceLinkState.Online => "在线",
        DeviceLinkState.New => "新上线",
        DeviceLinkState.Updated => "已更新",
        DeviceLinkState.Offline => "已离线",
        DeviceLinkState.UpdateFail => "更新失败",
        _ => "未知",
    };

    public string ActivateText => Activate switch
    {
        ActivateState.NotActivated => "未激活",
        ActivateState.Activated => "已激活",
        _ => "未知",
    };

    /// <summary>设备型号显示：优先扩展描述，其次基线描述，最后类型码。</summary>
    public string ModelText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DeviceDescEx)) return DeviceDescEx;
            if (!string.IsNullOrWhiteSpace(DeviceDesc)) return DeviceDesc;
            if (!string.IsNullOrWhiteSpace(BaseDesc)) return BaseDesc;
            return $"类型码 {DeviceType}";
        }
    }

    public string MultiCastText => IsMulticast ? "多播" : "链路";

    /// <summary>
    /// 是否门禁（ACS）设备。海康门禁/人脸门禁/梯控的型号都是 DS-K 开头，
    /// 以此为启发式；误判的代价只是验收页跳过抓图项，不会误伤其他功能。
    /// </summary>
    public bool IsAccessControl
    {
        get
        {
            var model = ModelText;
            return model.StartsWith("DS-K", StringComparison.OrdinalIgnoreCase)
                || model.Contains("门禁", StringComparison.Ordinal);
        }
    }

    /// <summary>设备类别标签：门禁设备标「门禁」，其他返回空（界面里空值不占位）。</summary>
    public string CategoryText => IsAccessControl ? "门禁" : string.Empty;

    public string IpMaskText => string.IsNullOrEmpty(IPv4SubnetMask)
        ? IPv4Address
        : $"{IPv4Address}/{MaskToPrefix(IPv4SubnetMask)}";

    private static int MaskToPrefix(string mask)
    {
        if (!System.Net.IPAddress.TryParse(mask, out var ip)) return 0;
        var bytes = ip.GetAddressBytes();
        int bits = 0;
        foreach (var b in bytes)
        {
            if (b == 255) { bits += 8; continue; }
            for (int i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) != 0) bits++;
                else return bits;
            }
        }
        return bits;
    }

    /// <summary>用于去重与同步的唯一键。</summary>
    public string Key => !string.IsNullOrWhiteSpace(SerialNo)
        ? SerialNo.ToUpperInvariant()
        : (!string.IsNullOrWhiteSpace(Mac) ? Mac.ToUpperInvariant() : UniformDevID);

    /// <summary>把当前设备复制成一个可独立编辑的"网络参数修改"目标。</summary>
    public DiscoveredDevice Clone() => (DiscoveredDevice)MemberwiseClone();

    /// <summary>
    /// 用最新一帧设备信息就地更新本对象。
    /// 就地更新可以保留 DataGrid 的行对象与选中状态，避免每次刷新都整表重建导致闪烁。
    /// </summary>
    public void CopyFrom(DiscoveredDevice o)
    {
        UniformDevID = o.UniformDevID;
        SerialNo = o.SerialNo;
        Mac = o.Mac;
        IPv4Address = o.IPv4Address;
        IPv4SubnetMask = o.IPv4SubnetMask;
        IPv4Gateway = o.IPv4Gateway;
        IPv6Address = o.IPv6Address;
        IPv6Gateway = o.IPv6Gateway;
        IPv6MaskLen = o.IPv6MaskLen;
        DeviceType = o.DeviceType;
        DeviceDesc = o.DeviceDesc;
        DeviceDescEx = o.DeviceDescEx;
        BaseDesc = o.BaseDesc;
        Manufacturer = o.Manufacturer;
        OemInfo = o.OemInfo;
        DetailOemCode = o.DetailOemCode;
        SdkPort = o.SdkPort;
        HttpPort = o.HttpPort;
        HttpsPort = o.HttpsPort;
        SdkOverTlsPort = o.SdkOverTlsPort;
        OemCommandPort = o.OemCommandPort;
        CmsPort = o.CmsPort;
        CmsIPv4 = o.CmsIPv4;
        EncoderCount = o.EncoderCount;
        DigitalChannelCount = o.DigitalChannelCount;
        HardDiskCount = o.HardDiskCount;
        SoftwareVersion = o.SoftwareVersion;
        DspVersion = o.DspVersion;
        BootTime = o.BootTime;
        EhomeVersion = o.EhomeVersion;
        SecuritySuite = o.SecuritySuite;
        if (!string.IsNullOrWhiteSpace(o.AdminUserName)) AdminUserName = o.AdminUserName;
        DhcpEnabled = o.DhcpEnabled;
        IsEzViz = o.IsEzViz;
        IsOem = o.IsOem;
        IsLicenseMissing = o.IsLicenseMissing;
        IsDiscoveryOnly = o.IsDiscoveryOnly;
        IsMulticast = o.IsMulticast;
        SupportModifyIpv6 = o.SupportModifyIpv6;
        SupportResetPasswd = o.SupportResetPasswd;
        SupportResetPasswdByCode = o.SupportResetPasswdByCode;
        SupportGuidReset = o.SupportGuidReset;
        SupportQuestionReset = o.SupportQuestionReset;
        SupportMailReset = o.SupportMailReset;
        SupportFactoryReset = o.SupportFactoryReset;
        SupportSyncIpcPasswd = o.SupportSyncIpcPasswd;
        Activate = o.Activate;
        LinkState = o.LinkState;
        LastSeen = o.LastSeen;

        // 离线时只标记状态，保留最后一次已知的网络参数，便于现场排查
        OnPropertyChanged(nameof(LinkStateText));
        OnPropertyChanged(nameof(ActivateText));
        OnPropertyChanged(nameof(ModelText));
        OnPropertyChanged(nameof(IpMaskText));
        OnPropertyChanged(nameof(MultiCastText));
        OnPropertyChanged(nameof(IsAccessControl));
        OnPropertyChanged(nameof(CategoryText));
    }
}

/// <summary>批量操作的单条结果。</summary>
public sealed class OperationResult : ObservableObject
{
    public string Target { get; set; } = string.Empty;         // 序列号或 IP
    public string Address { get; set; } = string.Empty;         // IP:端口
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    public string StatusText => Success ? "成功" : "失败";
}

/// <summary>验收结果中的单项检查。</summary>
public sealed class AcceptanceCheck
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public bool Skipped { get; set; }
    public string Detail { get; set; } = string.Empty;

    public string StatusText => Skipped ? "跳过" : Passed ? "通过" : "未通过";
}

/// <summary>单台设备的验收结论。</summary>
public sealed class AcceptanceRecord : ObservableObject
{
    public string Target { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public ushort Port { get; set; }
    public bool Online { get; set; }
    public bool LoggedIn { get; set; }
    public string SerialNo { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public string FirmwareDate { get; set; } = string.Empty;
    public int ChannelCount { get; set; }
    public int StartChannel { get; set; }
    public string DeviceTime { get; set; } = string.Empty;
    public double DeviceTimeOffsetSeconds { get; set; }
    public List<string> Snapshots { get; } = [];
    public List<AcceptanceCheck> Checks { get; } = [];
    public string? Error { get; set; }
    public long ElapsedMs { get; set; }

    public bool AllPassed => Checks.Count > 0 && Checks.All(c => c.Passed || c.Skipped) && Checks.Any(c => !c.Skipped);

    public string ResultText => !string.IsNullOrEmpty(Error) ? "失败" : AllPassed ? "通过" : "部分未通过";

    public string CheckSummary
    {
        get
        {
            int pass = Checks.Count(c => c.Passed);
            int fail = Checks.Count(c => !c.Passed && !c.Skipped);
            int skip = Checks.Count(c => c.Skipped);
            var parts = new List<string>();
            if (pass > 0) parts.Add($"通过 {pass}");
            if (fail > 0) parts.Add($"失败 {fail}");
            if (skip > 0) parts.Add($"跳过 {skip}");
            return parts.Count > 0 ? string.Join(" / ", parts) : "-";
        }
    }
}

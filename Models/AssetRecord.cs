using System.Text.Json.Serialization;
using HikDeployTool.Services;
using HikDeployTool.ViewModels;

namespace HikDeployTool.Models;

/// <summary>资产台账中的一条记录（交付验收后会长期保存，可导出 CSV 交给甲方）。</summary>
public sealed class AssetRecord : ObservableObject
{
    private string _name = string.Empty;
    private string _location = string.Empty;
    private string _note = string.Empty;
    private string _ip = string.Empty;
    private string _online = "未知";

    /// <summary>台账编号（自动生成，如 HJ-0001）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>设备名称（可自定义，如"北门球机"）。</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>安装位置。</summary>
    public string Location { get => _location; set => Set(ref _location, value); }

    public string SerialNo { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Ip { get => _ip; set => Set(ref _ip, value); }
    public ushort Port { get; set; } = 8000;
    public ushort HttpPort { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public string DspVersion { get; set; } = string.Empty;
    public uint EncoderCount { get; set; }
    public ushort DigitalChannelCount { get; set; }
    public string Manufacturer { get; set; } = string.Empty;

    public string ActivateState { get; set; } = "未知";
    public string AdminUserName { get; set; } = string.Empty;

    /// <summary>
    /// 设备登录口令，**DPAPI 加密后落盘**（CurrentUser 范围）——assets.json 里
    /// 只有密文，文件拷到别的机器/账号也解不开。入库"验证并入库"时写入，
    /// 预览页凭它实现"入库设备免密直连"。
    /// </summary>
    public string PasswordEnc { get; set; } = string.Empty;

    /// <summary>口令的明文存取视图（只存在于内存，序列化忽略）。</summary>
    [JsonIgnore]
    public string Password
    {
        get => SecretStore.Unprotect(PasswordEnc);
        set => PasswordEnc = SecretStore.Protect(value);
    }

    /// <summary>凭据验证标记（发现页"验证并入库"时写入，如"已验证 09-16 10:20"）。</summary>
    public string Verified { get; set; } = string.Empty;

    /// <summary>最近一次发现的在线状态文本。</summary>
    public string Online { get => _online; set => Set(ref _online, value); }

    public DateTime FirstSeen { get; set; } = DateTime.Now;
    public DateTime LastSeen { get; set; } = DateTime.Now;

    /// <summary>最近一次验收结论（通过 / 部分未通过 / 未验收）。</summary>
    public string Acceptance { get; set; } = "未验收";

    public string Note { get => _note; set => Set(ref _note, value); }

    public string Endpoint => Port == 0 ? Ip : $"{Ip}:{Port}";

    public static AssetRecord FromDevice(DiscoveredDevice d, string id) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(d.ModelText) ? d.IpMaskText : d.ModelText,
        SerialNo = d.SerialNo,
        Mac = d.Mac,
        Ip = d.IPv4Address,
        Port = d.SdkPort,
        HttpPort = d.HttpPort,
        Gateway = d.IPv4Gateway,
        SubnetMask = d.IPv4SubnetMask,
        Model = d.ModelText,
        FirmwareVersion = d.SoftwareVersion,
        DspVersion = d.DspVersion,
        EncoderCount = d.EncoderCount,
        DigitalChannelCount = d.DigitalChannelCount,
        Manufacturer = string.IsNullOrWhiteSpace(d.Manufacturer) ? d.OemInfo : d.Manufacturer,
        ActivateState = d.ActivateText,
        AdminUserName = d.AdminUserName,
        Online = d.LinkState == DeviceLinkState.Offline ? "离线" : "在线",
        FirstSeen = d.LastSeen,
        LastSeen = d.LastSeen,
        Note = d.Note ?? string.Empty,
    };

    /// <summary>用最新的发现结果刷新动态字段（保留用户填写的名称/位置/备注）。</summary>
    public void UpdateFrom(DiscoveredDevice d)
    {
        SerialNo = d.SerialNo;
        Mac = d.Mac;
        Ip = d.IPv4Address;
        Port = d.SdkPort;
        HttpPort = d.HttpPort;
        Gateway = d.IPv4Gateway;
        SubnetMask = d.IPv4SubnetMask;
        Model = d.ModelText;
        FirmwareVersion = d.SoftwareVersion;
        DspVersion = d.DspVersion;
        EncoderCount = d.EncoderCount;
        DigitalChannelCount = d.DigitalChannelCount;
        Manufacturer = string.IsNullOrWhiteSpace(d.Manufacturer) ? d.OemInfo : d.Manufacturer;
        ActivateState = d.ActivateText;
        if (!string.IsNullOrWhiteSpace(d.AdminUserName)) AdminUserName = d.AdminUserName;
        Online = d.LinkState == DeviceLinkState.Offline ? "离线" : "在线";
        LastSeen = d.LastSeen;
    }
}

using System.Runtime.InteropServices;
using HikDeployTool.Models;
using HikDeployTool.Native;

namespace HikDeployTool.Services;

/// <summary>网段扫描命中的一项（只有 IP 和端口，没有序列号）。</summary>
public sealed class SubnetProbeItem
{
    public string IP { get; set; } = string.Empty;
    public uint Port { get; set; }
    public string Protocol { get; set; } = string.Empty;
}

/// <summary>
/// SADP 服务封装。
///
/// 职责：
///  1. 管理 SADP_Start_V40 / SADP_Stop 生命周期，并把原生回调安全地转成托管事件；
///  2. 暴露激活、改网络参数、密码救援、设备配置读取等一次性调用；
///  3. 把返回的 BOOL/错误码统一翻译成中文。
///
/// 线程模型：SADP 的回调来自 SDK 自己的线程，本类只负责解析结构体并发事件，
/// 由上层（ViewModel）负责封送到 UI 线程。
/// </summary>
public sealed class SadpService : IDisposable
{
    private readonly object _gate = new();

    // 必须保留委托的强引用，否则会被 GC 回收导致回调时进程崩溃
    private SadpNative.DeviceFindCallbackV40? _deviceCallback;
    private SadpNative.SubnetDeviceFindCallbackV20? _subnetCallback;
    private GCHandle _selfHandle;

    private bool _started;
    private bool _disposed;

    /// <summary>发现到设备（新增/更新/下线/重启都会触发）。</summary>
    public event Action<DiscoveredDevice>? DeviceFound;

    /// <summary>网段扫描命中一台设备。</summary>
    public event Action<SubnetProbeItem>? SubnetDeviceFound;

    /// <summary>子线程上的原始消息（用于日志）。</summary>
    public event Action<string>? Trace;

    public bool IsRunning
    {
        get { lock (_gate) return _started; }
    }

    // ==================== 生命周期 ====================

    /// <summary>启动 SADP 服务。返回 0 表示成功，非 0 为 SADP 错误码。</summary>
    public int Start(int logLevel = 3, string? logDir = null)
    {
        lock (_gate)
        {
            if (_started) return 0;
            if (_disposed) return 2002;

            if (!string.IsNullOrEmpty(logDir))
            {
                try
                {
                    using var dir = new AnsiString(logDir);
                    SadpNative.SADP_SetLogToFile(logLevel, dir.Pointer, 1);
                }
                catch (Exception ex) { LogService.Current.Warn($"设置 SADP 日志失败：{ex.Message}", "SADP"); }
            }

            _deviceCallback = OnDeviceFound;
            _subnetCallback = OnSubnetDeviceFound;
            // 把 this 传给原生层，回调里再取回来
            _selfHandle = GCHandle.Alloc(this);

            int ret = SadpNative.SADP_Start_V40(_deviceCallback, 0, GCHandle.ToIntPtr(_selfHandle));
            if (ret == 0)
            {
                int err = (int)SadpNative.SADP_GetLastError();
                _deviceCallback = null;
                _subnetCallback = null;
                if (_selfHandle.IsAllocated) _selfHandle.Free();
                LogService.Current.Error($"SADP 启动失败：{SadpError.Describe((uint)err)}", "SADP");
                return err == 0 ? 2002 : err;
            }

            _started = true;
            LogService.Current.Info($"SADP 服务已启动（内置版本 {FormatSadpVersion(SadpNative.SADP_GetSadpVersion())}）", "SADP");
            return 0;
        }
    }

    /// <summary>停止 SADP 服务。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            try
            {
                SadpNative.SADP_Stop();
            }
            catch (Exception ex)
            {
                LogService.Current.Warn($"停止 SADP 时异常：{ex.Message}", "SADP");
            }
            finally
            {
                _started = false;
                _deviceCallback = null;
                _subnetCallback = null;
                if (_selfHandle.IsAllocated) _selfHandle.Free();
                LogService.Current.Info("SADP 服务已停止", "SADP");
            }
        }
    }

    private static string FormatSadpVersion(uint version)
    {
        // 返回值为 4 个字节拼成的无符号整数，按大端拆成 4 段更易读
        var b = BitConverter.GetBytes(version);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return string.Join(".", b);
    }

    // ==================== 原生回调 ====================

    private void OnDeviceFound(IntPtr lpDeviceInfo, IntPtr pUserData)
    {
        try
        {
            if (lpDeviceInfo == IntPtr.Zero) return;
            var native = Marshal.PtrToStructure<SADP_DEVICE_INFO_V40>(lpDeviceInfo);
            var device = MapDevice(native);
            DeviceFound?.Invoke(device);
        }
        catch (Exception ex)
        {
            // 回调里绝不能抛异常穿透回原生栈
            Trace?.Invoke($"解析设备信息失败：{ex.Message}");
        }
    }

    private void OnSubnetDeviceFound(IntPtr lpDeviceInfo, IntPtr pUserData)
    {
        try
        {
            if (lpDeviceInfo == IntPtr.Zero) return;
            var native = Marshal.PtrToStructure<SADP_SUBNET_DEVICE_INFO_V20>(lpDeviceInfo);
            var item = new SubnetProbeItem
            {
                IP = NativeText.FromAnsi(native.szIPv4Address),
                Port = native.dwPort,
                Protocol = native.byProtocolType switch
                {
                    0 => "私有协议",
                    1 => "ISAPI",
                    2 => "OTAP",
                    _ => "未知",
                },
            };
            if (!string.IsNullOrWhiteSpace(item.IP)) SubnetDeviceFound?.Invoke(item);
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"解析网段扫描结果失败：{ex.Message}");
        }
    }

    /// <summary>把 SADP_DEVICE_INFO_V40 映射成托管模型。</summary>
    private static DiscoveredDevice MapDevice(SADP_DEVICE_INFO_V40 n)
    {
        var d = n.struSadpDeviceInfo;

        byte bySupport = d.bySupport;
        byte bySupport1 = d.bySupport1;
        byte bySupport2 = d.bySupport2;

        var device = new DiscoveredDevice
        {
            UniformDevID = NativeText.FromAnsi(n.szUniformDevID),
            SerialNo = NativeText.FromAnsi(d.szSerialNO),
            Mac = NativeText.FromAnsi(d.szMAC),
            IPv4Address = NativeText.FromAnsi(d.szIPv4Address),
            IPv4SubnetMask = NativeText.FromAnsi(d.szIPv4SubnetMask),
            IPv4Gateway = NativeText.FromAnsi(d.szIPv4Gateway),
            IPv6Address = NativeText.FromAnsi(d.szIPv6Address),
            IPv6Gateway = NativeText.FromAnsi(d.szIPv6Gateway),
            IPv6MaskLen = d.byIPv6MaskLen,
            DeviceType = d.dwDeviceType,
            DeviceDesc = NativeText.FromAnsi(d.szDevDesc),
            DeviceDescEx = NativeText.FromAnsi(n.szDevDescEx),
            BaseDesc = NativeText.FromAnsi(d.szBaseDesc),
            Manufacturer = NativeText.FromAnsi(n.szManufacturer),
            OemInfo = NativeText.FromAnsi(d.szOEMinfo),
            DetailOemCode = d.dwDetailOEMCode,
            SdkPort = d.dwPort == 0 ? (ushort)8000 : (ushort)Math.Min(d.dwPort, ushort.MaxValue),
            HttpPort = d.wHttpPort,
            HttpsPort = n.wHttpsPort,
            SdkOverTlsPort = n.dwSDKOverTLSPort,
            OemCommandPort = d.wOEMCommandPort,
            CmsPort = d.wCmsPort,
            CmsIPv4 = NativeText.FromAnsi(d.szCmsIPv4),
            EncoderCount = d.dwNumberOfEncoders,
            DigitalChannelCount = d.wDigitalChannelNum,
            HardDiskCount = d.dwNumberOfHardDisk,
            SoftwareVersion = NativeText.FromAnsi(d.szDeviceSoftwareVersion),
            DspVersion = NativeText.FromAnsi(d.szDSPVersion),
            BootTime = NativeText.FromAnsi(d.szBootTime),
            EhomeVersion = NativeText.FromAnsi(n.szEhmoeVersion),
            SecuritySuite = NativeText.FromAnsi(n.szSecuritySuite),
            AdminUserName = NativeText.FromAnsi(n.szUserName),
            DhcpEnabled = d.byDhcpEnabled == 1,
            IsEzViz = d.byEZVIZCode == 1,
            IsOem = d.byOEMCode == 1 || d.dwDetailOEMCode > 1,
            IsLicenseMissing = n.byLicensed == 1,
            IsDiscoveryOnly = n.byDiscoveryOnly == 1,
            IsMulticast = n.byDataFromMulticast == 1,
            SupportModifyIpv6 = (bySupport & 0x02) != 0,
            SupportResetPasswd = (bySupport & 0x40) != 0,
            SupportSyncIpcPasswd = (bySupport & 0x80) != 0,
            SupportResetPasswdByCode = (bySupport1 & 0x01) != 0,
            SupportGuidReset = (bySupport1 & 0x04) != 0,
            SupportQuestionReset = (bySupport1 & 0x08) != 0,
            SupportFactoryReset = (bySupport1 & 0x40) != 0,
            SupportMailReset = (bySupport2 & 0x02) != 0,
            // byActivated: 0-已激活, 1-未激活（老设备固定为已激活）
            Activate = d.byActivated == 1 && d.byDeviceAbility != 0
                ? ActivateState.NotActivated
                : ActivateState.Activated,
            LinkState = d.iResult switch
            {
                SadpConst.SADP_ADD => DeviceLinkState.New,
                SadpConst.SADP_UPDATE => DeviceLinkState.Updated,
                SadpConst.SADP_DEC => DeviceLinkState.Offline,
                SadpConst.SADP_RESTART => DeviceLinkState.Online,
                SadpConst.SADP_UPDATEFAIL => DeviceLinkState.UpdateFail,
                _ => DeviceLinkState.Updated,
            },
            LastSeen = DateTime.Now,
        };

        // 兜底：少数设备不返回 szUniformDevID，用 MAC 代替（SDK 两者都可作为唯一标识）
        if (string.IsNullOrWhiteSpace(device.UniformDevID))
            device.UniformDevID = device.Mac;

        return device;
    }

    // ==================== 设备操作 ====================

    private static int LastError() => (int)SadpNative.SADP_GetLastError();

    /// <summary>激活设备。sCommand 为激活密码。</summary>
    public (bool ok, string message) Activate(string uniformDevId, string password)
    {
        try
        {
            int ret = SadpNative.SADP_ActivateDevice(uniformDevId, password);
            if (ret != 0) return (true, "激活成功");
            return (false, SadpError.Describe((uint)LastError()));
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}");
        }
    }

    /// <summary>修改设备网络参数。</summary>
    public (bool ok, string message, byte retryTimes, byte lockMinutes) ModifyNetParam(
        string uniformDevId, string password, NetParamInput input)
    {
        var native = new SADP_DEV_NET_PARAM
        {
            szIPv4Address = new byte[16],
            szIPv4SubNetMask = new byte[16],
            szIPv4Gateway = new byte[16],
            szIPv6Address = new byte[128],
            szIPv6Gateway = new byte[128],
            byRes = new byte[122],
        };

        NativeText.WriteFixed(native.szIPv4Address, input.IPv4Address);
        NativeText.WriteFixed(native.szIPv4SubNetMask, input.SubnetMask);
        NativeText.WriteFixed(native.szIPv4Gateway, input.Gateway);
        NativeText.WriteFixed(native.szIPv6Address, input.IPv6Address);
        NativeText.WriteFixed(native.szIPv6Gateway, input.IPv6Gateway);
        native.byIPv6MaskLen = input.IPv6MaskLen;
        native.byDhcpEnable = (byte)(input.DhcpEnabled ? 1 : 0);
        native.wPort = input.SdkPort;
        native.wHttpPort = input.HttpPort;
        native.dwSDKOverTLSPort = input.SdkOverTlsPort;

        try
        {
            int ret = SadpNative.SADP_ModifyDeviceNetParam_V40(uniformDevId, password, ref native,
                out var retParam, (uint)Marshal.SizeOf<SADP_DEV_RET_NET_PARAM>());

            if (ret != 0)
                return (true, "修改成功（设备将重启网络）", retParam.byRetryModifyTime, retParam.bySurplusLockTime);

            int err = LastError();
            string msg = SadpError.Describe((uint)err);
            // 错误时 SDK 也会回填剩余次数/锁定时间，一并提示
            if (retParam.byRetryModifyTime > 0) msg += $"，剩余可尝试 {retParam.byRetryModifyTime} 次";
            if (retParam.bySurplusLockTime > 0) msg += $"，设备剩余锁定 {retParam.bySurplusLockTime} 分钟";

            // 2009 = SADP_DEVICE_DENY：设备端明确拒绝，原因至少有两种，
            // 处理方式完全不同，不能原样丢给用户让他猜：
            //   a) 口令不对 —— 部分固件对"改网络参数时密码错"就回 DENY 而不是 2024；
            //   b) 口令正确但设备有安全策略 —— 常见于绑定了萤石云/Hik-Connect 平台、
            //      网页里关掉了 SADP 修改权限、或设备正在重启。
            // 这里用 V50 登录接口单独验一次口令，把两种情况分开。
            if (err == 2009)
            {
                var (loginOk, loginMsg, _) = LoginDevice(uniformDevId, "admin", password);
                if (!loginOk)
                {
                    msg = $"口令未通过设备校验（{loginMsg}）。请先在设备网页或 iVMS-4200 上验证设备当前密码，" +
                          "注意连续输错会导致设备锁定";
                }
                else
                {
                    LogoutDevice(uniformDevId);
                    msg = "口令正确，但设备拒绝了修改请求。最常见原因：" +
                          "① 设备已绑定萤石云/Hik-Connect 平台（需先解绑或在平台输入设备验证码）；" +
                          "② 设备网页里关闭了 SADP 修改网络参数的权限；" +
                          "③ 设备刚重启还没就绪，等一分钟再试";
                    LogService.Current.Warn($"修改网络参数被拒（口令已验证正确）：{uniformDevId}", "SADP");
                }
            }

            return (false, msg, retParam.byRetryModifyTime, retParam.bySurplusLockTime);
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}", 0, 0);
        }
    }

    /// <summary>
    /// 恢复设备到未激活状态（需要设备安全码/设备码）。
    /// 交付现场最常见的"密码救援"手段：把设备变回未激活，再用 SADP 重新激活。
    /// </summary>
    public (bool ok, string message) ResetDefaultPasswd(string uniformDevId, string deviceCode)
    {
        try
        {
            int ret = SadpNative.SADP_ResetDefaultPasswd(uniformDevId, deviceCode);
            if (ret != 0) return (true, "已恢复为未激活状态");
            return (false, SadpError.Describe((uint)LastError()));
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}");
        }
    }

    /// <summary>重置密码（V40）。resetType：2-文件，3-二维码/设备码，4-GUID，5-安全问题，6-邮箱，7-手机扫码。</summary>
    public (bool ok, string message, byte retryTimes, byte lockMinutes) ResetPasswd(
        string uniformDevId, ResetInput input)
    {
        // 优先用 V50：密码字段 128 字节，能容纳现代设备的复杂密码；
        // 若设备/固件只支持旧协议，再回退到 V40（16 字节密码字段）。
        var v50 = ResetPasswdV50(uniformDevId, input);
        if (v50.ok) return (v50.ok, v50.message, v50.retryTimes, v50.lockMinutes);

        // 方式 6（邮箱）/ 7（手机扫码）的重置凭据在 SADP_RESET_PARAM_V40 里**没有对应字段**，
        // 降级过去只会把邮箱/手机号丢掉，然后回一个莫名其妙的"参数错误"。
        // 与其让用户以为是自己填错了，不如直说。
        if (input.ResetType is SadpConst.RESET_BY_MAILBOX or SadpConst.RESET_BY_PHONE)
            return (false, v50.message + "；该方式仅新版 V50 接口支持，无法降级重试", v50.retryTimes, v50.lockMinutes);

        // 只有在"接口层面不被支持"时才回退，密码错误之类的业务失败直接返回，避免重复尝试把设备锁死
        if (!v50.retryWithV40) return (false, v50.message, v50.retryTimes, v50.lockMinutes);

        LogService.Current.Warn($"V50 重置接口不可用（{v50.message}），改用 V40 重试", "SADP");
        var v40 = ResetPasswdV40(uniformDevId, input);
        return v40;
    }

    private (bool ok, string message, byte retryTimes, byte lockMinutes, bool retryWithV40) ResetPasswdV50(
        string uniformDevId, ResetInput input)
    {
        var native = new SADP_RESET_PARAM_V50
        {
            dwSize = (uint)Marshal.SizeOf<SADP_RESET_PARAM_V50>(),
            szPassword = new byte[SadpConst.MAX_PASS_LEN_V31],
            szCode = new byte[SadpConst.MAX_ENCRYPT_CODE_V31],
            szAuthFile = new byte[SadpConst.MAX_FILE_PATH_LEN],
            szGUID = new byte[SadpConst.MAX_GUID_LEN_V31],
            struSecurityQuestionCfg = NewSecurityQuestionCfg(),
            byResetType = input.ResetType,
            byEnableSyncIPCPW = (byte)(input.SyncIpcPassword ? 1 : 0),
            wGUIDLen = (ushort)Math.Min(input.Guid.Length, SadpConst.MAX_GUID_LEN_V31),
            szMailBoxAddr = new byte[SadpConst.MAX_MAILBOX_LEN],
            szPhoneNo = new byte[SadpConst.MAX_PHONE_NUMBER_LEN],
            byRes = new byte[364],
        };

        NativeText.WriteFixed(native.szPassword, input.NewPassword);
        NativeText.WriteFixed(native.szCode, input.Code);
        NativeText.WriteFixed(native.szAuthFile, input.AuthFile);
        NativeText.WriteFixed(native.szGUID, input.Guid);
        // byResetType=6（预留邮箱）与 7（手机扫码）各有专属字段，
        // 漏填的后果是设备直接判"参数错误"，而不是回一条能看懂的提示。
        // 见 Sadp.h:467-468 的字段注释。
        NativeText.WriteFixed(native.szMailBoxAddr, input.MailBox);
        NativeText.WriteFixed(native.szPhoneNo, input.PhoneNo);
        FillSecurityAnswers(native.struSecurityQuestionCfg, input);

        var lockInfo = new SADP_DEV_LOCK_INFO { byRes = new byte[126] };
        try
        {
            int ret = SadpNative.SADP_ResetPasswd_V50(uniformDevId, ref native, ref lockInfo);
            if (ret != 0)
                return (true, "密码重置成功", lockInfo.byRetryTime, lockInfo.bySurplusLockTime, false);

            uint err = SadpNative.SADP_GetLastError();
            string msg = SadpError.Describe(err);
            // 2005 参数错误 / 2049 找不到设备 之外，把"不支持"也当作可回退信号
            bool retry = err is 2005 or 2002 or 2051 or 2054;
            return (false, msg, lockInfo.byRetryTime, lockInfo.bySurplusLockTime, retry);
        }
        catch (EntryPointNotFoundException)
        {
            return (false, "当前 Sadp.dll 未导出 V50 重置接口", 0, 0, true);
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}", 0, 0, false);
        }
    }

    private (bool ok, string message, byte retryTimes, byte lockMinutes) ResetPasswdV40(
        string uniformDevId, ResetInput input)
    {
        var native = new SADP_RESET_PARAM_V40
        {
            dwSize = (uint)Marshal.SizeOf<SADP_RESET_PARAM_V40>(),
            byResetType = input.ResetType,
            byEnableSyncIPCPW = (byte)(input.SyncIpcPassword ? 1 : 0),
            byRes2 = new byte[2],
            szPassword = new byte[SadpConst.MAX_PASS_LEN],
            szCode = new byte[SadpConst.MAX_ENCRYPT_CODE],
            szAuthFile = new byte[SadpConst.MAX_FILE_PATH_LEN],
            szGUID = new byte[SadpConst.MAX_GUID_LEN],
            struSecurityQuestionCfg = NewSecurityQuestionCfg(),
            byRes = new byte[512],
        };

        NativeText.WriteFixed(native.szPassword, Truncate(input.NewPassword, SadpConst.MAX_PASS_LEN));
        NativeText.WriteFixed(native.szCode, input.Code);
        NativeText.WriteFixed(native.szAuthFile, input.AuthFile);
        NativeText.WriteFixed(native.szGUID, input.Guid);
        FillSecurityAnswers(native.struSecurityQuestionCfg, input);

        try
        {
            int ret = SadpNative.SADP_ResetPasswd_V40(uniformDevId, ref native, out var retInfo);
            if (ret != 0) return (true, "密码重置成功", retInfo.byRetryGUIDTime, retInfo.bySurplusLockTime);

            string msg = SadpError.Describe(SadpNative.SADP_GetLastError());
            if (retInfo.bRetryTimeValid != 0) msg += $"，剩余可尝试 {retInfo.byRetryGUIDTime} 次";
            if (retInfo.bLockTimeValid != 0) msg += $"，设备剩余锁定 {retInfo.bySurplusLockTime} 分钟";
            return (false, msg, retInfo.byRetryGUIDTime, retInfo.bySurplusLockTime);
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}", 0, 0);
        }
    }

    private static SADP_SECURITY_QUESTION_CFG NewSecurityQuestionCfg()
    {
        var cfg = new SADP_SECURITY_QUESTION_CFG
        {
            dwSize = (uint)Marshal.SizeOf<SADP_SECURITY_QUESTION_CFG>(),
            struSecurityQuestion = new SADP_SINGLE_SECURITY_QUESTION_CFG[SadpConst.MAX_QUESTION_LIST_LEN],
            szPassword = new byte[SadpConst.MAX_PASS_LEN],
            byRes = new byte[512],
        };
        for (int i = 0; i < cfg.struSecurityQuestion.Length; i++)
        {
            cfg.struSecurityQuestion[i] = new SADP_SINGLE_SECURITY_QUESTION_CFG
            {
                dwSize = (uint)Marshal.SizeOf<SADP_SINGLE_SECURITY_QUESTION_CFG>(),
                dwId = (uint)i,
                szAnswer = new byte[SadpConst.MAX_ANSWER_LEN],
                byRes = new byte[127],
            };
        }
        return cfg;
    }

    private static void FillSecurityAnswers(SADP_SECURITY_QUESTION_CFG cfg, ResetInput input)
    {
        if (input.ResetType != 5) return;
        NativeText.WriteFixed(cfg.szPassword, Truncate(input.NewPassword, SadpConst.MAX_PASS_LEN));
        for (int i = 0; i < input.Answers.Count && i < cfg.struSecurityQuestion.Length; i++)
        {
            var q = cfg.struSecurityQuestion[i];
            NativeText.WriteFixed(q.szAnswer, input.Answers[i]);
            q.byMark = 1;
            cfg.struSecurityQuestion[i] = q;
        }
    }

    private static string Truncate(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var raw = NativeText.ToAnsi(value);
        return raw.Length <= maxBytes - 1 ? value : NativeText.FromAnsi(raw[..(maxBytes - 1)]);
    }

    /// <summary>
    /// 调用 <c>SADP_GetDeviceConfig</c>，入参与出参都是托管结构体。
    ///
    /// 统一走托管结构体的理由：这一族命令的首字段是"长度/尺寸"、末尾是定长保留区，
    /// 手算偏移很容易把 <c>byRes</c> 的长度记错，而记错的后果不是报错、是**静默错位**——
    /// 读出来的字符串看着像模像样，其实是从隔壁字段串过来的。
    /// 结构体尺寸另有 <see cref="StructLayoutCheck"/> 在启动时逐条校验，比人眼可靠。
    /// </summary>
    private (bool ok, string message) ReadConfig<TIn, TOut>(string uniformDevId, uint command, TIn input, out TOut output)
        where TIn : struct
        where TOut : struct
    {
        output = default;
        int inSize = Marshal.SizeOf<TIn>();
        int outSize = Marshal.SizeOf<TOut>();
        IntPtr pin = IntPtr.Zero, pout = IntPtr.Zero;
        try
        {
            pin = AllocZeroed(inSize);
            pout = AllocZeroed(outSize);
            Marshal.StructureToPtr(input, pin, false);

            int ret = SadpNative.SADP_GetDeviceConfig(uniformDevId, command, pin, (uint)inSize, pout, (uint)outSize);
            if (ret == 0) return (false, SadpError.Describe((uint)LastError()));

            output = Marshal.PtrToStructure<TOut>(pout);
            return (true, "读取成功");
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}");
        }
        finally
        {
            if (pin != IntPtr.Zero) Marshal.FreeHGlobal(pin);
            if (pout != IntPtr.Zero) Marshal.FreeHGlobal(pout);
        }
    }

    /// <summary>
    /// 同 <see cref="ReadConfig{TIn,TOut}"/>，但改用 <c>SADP_GetDeviceConfigByMAC</c>。
    /// 少数固件/协议只认 MAC 寻址，UniformDevID 递进去会直接回"不支持"。
    /// </summary>
    private (bool ok, string message) ReadConfigByMac<TIn, TOut>(string mac, uint command, TIn input, out TOut output)
        where TIn : struct
        where TOut : struct
    {
        output = default;
        int inSize = Marshal.SizeOf<TIn>();
        int outSize = Marshal.SizeOf<TOut>();
        IntPtr pin = IntPtr.Zero, pout = IntPtr.Zero;
        try
        {
            pin = AllocZeroed(inSize);
            pout = AllocZeroed(outSize);
            Marshal.StructureToPtr(input, pin, false);

            int ret = SadpNative.SADP_GetDeviceConfigByMAC(mac, command, pin, (uint)inSize, pout, (uint)outSize);
            if (ret == 0) return (false, SadpError.Describe((uint)LastError()));

            output = Marshal.PtrToStructure<TOut>(pout);
            return (true, "读取成功");
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}");
        }
        finally
        {
            if (pin != IntPtr.Zero) Marshal.FreeHGlobal(pin);
            if (pout != IntPtr.Zero) Marshal.FreeHGlobal(pout);
        }
    }

    private static IntPtr AllocZeroed(int size)
    {
        var p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }

    /// <summary>
    /// 读取设备码——密码救援最核心的凭据。
    ///
    /// 依次尝试三条路：
    ///   ① <c>SADP_GET_DEVICE_CODE_V31</c>（512 字节，现代设备）；
    ///   ② <c>SADP_GET_DEVICE_CODE</c>（128 字节，老固件）；
    ///   ③ <c>SADP_GetDeviceConfigByMAC</c>（少数设备只认 MAC 寻址）。
    ///
    /// 全部失败时返回**最具体**的那条错误，而不是笼统一句"读不到"：
    /// 现场排障全靠这个错误码区分"设备未激活 / 响应超时 / 设备拒绝 / 链路不支持"，
    /// 这四种情况的处理办法完全不同（前一种去激活，后三种分别换网络、查绑定、换工具）。
    /// </summary>
    public (bool ok, string code, string message) GetDeviceCode(string uniformDevId, string? mac = null)
    {
        if (string.IsNullOrWhiteSpace(uniformDevId))
            return (false, string.Empty, "设备唯一标识为空，无法读取设备码");

        var v31In = new SADP_SAFE_CODE_V31
        {
            szDeviceCode = new byte[SadpConst.MAX_DEVICE_CODE_V31],
            byRes = new byte[512],
        };
        var r31 = ReadConfig(uniformDevId, SadpConst.SADP_GET_DEVICE_CODE_V31, v31In, out SADP_SAFE_CODE_V31 v31Out);
        if (r31.ok)
        {
            string code = NativeText.FromAnsi(v31Out.szDeviceCode);
            if (!string.IsNullOrWhiteSpace(code)) return (true, code, "读取成功");
        }

        var legacyIn = new SADP_SAFE_CODE
        {
            szDeviceCode = new byte[SadpConst.MAX_DEVICE_CODE],
            byRes = new byte[128],
        };
        var rOld = ReadConfig(uniformDevId, SadpConst.SADP_GET_DEVICE_CODE, legacyIn, out SADP_SAFE_CODE oldOut);
        if (rOld.ok)
        {
            string code = NativeText.FromAnsi(oldOut.szDeviceCode);
            if (!string.IsNullOrWhiteSpace(code)) return (true, code, "读取成功（旧版设备码）");
        }

        string byMacNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(mac))
        {
            var rMac = ReadConfigByMac(mac, SadpConst.SADP_GET_DEVICE_CODE_V31, v31In, out SADP_SAFE_CODE_V31 macOut);
            if (rMac.ok)
            {
                string code = NativeText.FromAnsi(macOut.szDeviceCode);
                if (!string.IsNullOrWhiteSpace(code)) return (true, code, "读取成功（按 MAC 寻址）");
            }
            byMacNote = $"；按 MAC 重试：{rMac.message}";
        }

        // 三条路都断，把"最像根因"的那条放最前面
        string primary = !r31.ok ? r31.message : "设备返回内容为空";
        string detail = rOld.ok ? string.Empty : $"；旧版命令：{rOld.message}";
        return (false, string.Empty, $"{primary}{detail}{byMacNote}");
    }

    /// <summary>
    /// 读取 GUID（V31）。
    /// 返回值里额外带"剩余可导出次数 / 设备锁定分钟数"——
    /// 这两个数字是决定"还能不能试"的依据，必须让用户看到。
    /// </summary>
    public (bool ok, string guid, byte retryTimes, byte lockMinutes, string message) GetGuid(string uniformDevId)
    {
        var input = new SADP_GUID_FILE_V31
        {
            szGUID = new byte[SadpConst.MAX_GUID_LEN_V31],
            struDevLockInfo = new SADP_DEV_LOCK_INFO { byRes = new byte[126] },
            byRes = new byte[256],
        };
        var r = ReadConfig(uniformDevId, SadpConst.SADP_GET_GUID_V31, input, out SADP_GUID_FILE_V31 output);
        if (!r.ok) return (false, string.Empty, 0, 0, r.message);

        string guid = NativeText.FromAnsi(output.szGUID);
        return string.IsNullOrWhiteSpace(guid)
            ? (false, string.Empty, output.struDevLockInfo.byRetryTime, output.struDevLockInfo.bySurplusLockTime, "设备返回的 GUID 为空")
            : (true, guid, output.struDevLockInfo.byRetryTime, output.struDevLockInfo.bySurplusLockTime, "读取成功");
    }

    /// <summary>
    /// 读取"设备到底配过哪些找回方式"（SADP_GET_PASSWORD_RESET_TYPE）。
    ///
    /// 这是本页最省事的诊断：能在下发重置之前就告诉用户
    /// "这台设备没配过安全问题、也没绑邮箱，别试了"，
    /// 避免白白消耗一次尝试机会（部分设备错几次就锁半小时）。
    /// </summary>
    public (bool ok, PasswordResetAbility ability, string message) GetPasswordResetAbility(string uniformDevId)
    {
        var input = new SADP_PASSWORD_RESET_TYPE_PARAM
        {
            dwSize = (uint)Marshal.SizeOf<SADP_PASSWORD_RESET_TYPE_PARAM>(),
            byRes1 = new byte[3],
            byRes = new byte[64],
        };
        var r = ReadConfig(uniformDevId, SadpConst.SADP_GET_PASSWORD_RESET_TYPE, input, out SADP_PASSWORD_RESET_TYPE_PARAM output);
        if (!r.ok) return (false, new PasswordResetAbility(), r.message);

        return (true, new PasswordResetAbility
        {
            AnyConfigured = output.byEnable == 1,
            GuidExported = output.byGuidEnabled == 1,
            SecurityQuestionConfigured = output.bySecurityQuestionEnabled == 1,
            SecurityMailboxConfigured = output.bySecurityMailBoxEnabled == 1,
            HikConnectBound = output.byHikConnectEnabled == 1,
        }, "读取成功");
    }

    /// <summary>
    /// 读取管理员预留邮箱（<c>SADP_GET_USER_MAILBOX</c>，免认证协议，不需要密码）。
    /// 拿到邮箱才能继续走"预留邮箱"重置。
    /// </summary>
    public (bool ok, string mailbox, string message) GetUserMailbox(string uniformDevId)
    {
        var input = new SADP_USER_MAILBOX
        {
            dwSize = (uint)Marshal.SizeOf<SADP_USER_MAILBOX>(),
            szPassword = new byte[SadpConst.MAX_PASS_LEN],
            szMailBoxAddr = new byte[SadpConst.MAX_MAILBOX_LEN],
            byRes = new byte[128],
        };
        var r = ReadConfig(uniformDevId, SadpConst.SADP_GET_USER_MAILBOX, input, out SADP_USER_MAILBOX output);
        if (!r.ok) return (false, string.Empty, r.message);

        string box = NativeText.FromAnsi(output.szMailBoxAddr);
        return string.IsNullOrWhiteSpace(box)
            ? (false, string.Empty, "设备未预留邮箱（SADP 2038）")
            : (true, box, "读取成功");
    }

    /// <summary>
    /// 读取"手机扫码重置"的二维码原料（<c>SADP_GET_PHONE_QR_CODES</c>）。
    ///
    /// 入参必须先把 <c>szPhoneNo</c> 填上：ISAPI/OTAP 协议设备要靠它去查对应的二维码，
    /// 不填就会返回空。
    /// </summary>
    public (bool ok, QrPayload? payload, string message) GetPhoneQrCodes(string uniformDevId, string phoneNo)
    {
        if (string.IsNullOrWhiteSpace(phoneNo))
            return (false, null, "请先填写用于接收重置信息的手机号，设备需要用它查询二维码");

        var input = new SADP_PHONE_QR_CODES
        {
            dwSize = (uint)Marshal.SizeOf<SADP_PHONE_QR_CODES>(),
            szDomainName = new byte[SadpConst.MAX_QR_CODES],
            szDevModel = new byte[32],
            szQrCodes = new byte[SadpConst.MAX_QR_CODES_V31],
            szPhoneNo = new byte[SadpConst.MAX_PHONE_NUMBER_LEN],
            byRes = new byte[108],
        };
        NativeText.WriteFixed(input.szPhoneNo, phoneNo);

        var r = ReadConfig(uniformDevId, SadpConst.SADP_GET_PHONE_QR_CODES, input, out SADP_PHONE_QR_CODES output);
        if (!r.ok) return (false, null, r.message);

        string raw = NativeText.FromAnsi(output.szQrCodes);
        if (string.IsNullOrWhiteSpace(raw))
            return (false, null, "设备未返回二维码数据（该设备可能不支持手机扫码重置，或该手机号未在设备上预留）");

        string domain = NativeText.FromAnsi(output.szDomainName);
        string model = NativeText.FromAnsi(output.szDevModel);
        var payload = new QrPayload
        {
            Kind = QrKind.PhoneScan,
            RawQr = raw,
            DomainName = domain,
            DeviceModel = model,
            PhoneNo = NativeText.FromAnsi(output.szPhoneNo),
            ValidSeconds = output.dwValidTime,
            // 拼接规则来自 demo/win/DlgResetPWPhone.cpp:110：
            //   sprintf("%s?code=D:%s**%s", szDomainName, szDevModel, szQrCodes)
            Content = string.IsNullOrWhiteSpace(domain)
                ? raw
                : $"{domain}?code=D:{model}**{raw}",
        };
        return (true, payload, "读取成功");
    }

    /// <summary>
    /// 读取"预留邮箱重置"的二维码原料（<c>SADP_GET_QR_CODES_V31</c>，自动回退旧命令）。
    ///
    /// 与手机扫码不同，邮箱方式的二维码内容就是 <c>szQrCodes</c> 原文，不需要拼接
    /// （demo/win/MailBoxResetPW.cpp:211）。
    /// </summary>
    public (bool ok, QrPayload? payload, string message) GetMailQrCodes(string uniformDevId, string mailbox)
    {
        var input = new SADP_USER_MAILBOX
        {
            dwSize = (uint)Marshal.SizeOf<SADP_USER_MAILBOX>(),
            szPassword = new byte[SadpConst.MAX_PASS_LEN],
            szMailBoxAddr = new byte[SadpConst.MAX_MAILBOX_LEN],
            byRes = new byte[128],
        };
        NativeText.WriteFixed(input.szMailBoxAddr, mailbox);

        var r31 = ReadConfig(uniformDevId, SadpConst.SADP_GET_QR_CODES_V31, input, out SADP_QR_CODES_V31 out31);
        if (r31.ok)
        {
            string raw = NativeText.FromAnsi(out31.szQrCodes);
            if (!string.IsNullOrWhiteSpace(raw))
                return (true, BuildMailPayload(raw, out31.szMailBoxAddr, out31.szServiceMailBoxAddr), "读取成功");
        }

        var rOld = ReadConfig(uniformDevId, SadpConst.SADP_GET_QR_CODES, input, out SADP_QR_CODES outOld);
        if (rOld.ok)
        {
            string raw = NativeText.FromAnsi(outOld.szQrCodes);
            if (!string.IsNullOrWhiteSpace(raw))
                return (true, BuildMailPayload(raw, outOld.szMailBoxAddr, outOld.szServiceMailBoxAddr), "读取成功（旧版二维码）");
        }

        string primary = r31.ok ? "设备未返回二维码数据" : r31.message;
        string detail = rOld.ok ? string.Empty : $"；旧版命令：{rOld.message}";
        return (false, null, $"{primary}{detail}");
    }

    private static QrPayload BuildMailPayload(string raw, byte[] mailbox, byte[] serviceMailbox) => new()
    {
        Kind = QrKind.Mailbox,
        RawQr = raw,
        Content = raw,
        MailBox = NativeText.FromAnsi(mailbox),
        ServiceMailBox = NativeText.FromAnsi(serviceMailbox),
    };

    /// <summary>设置设备过滤规则（是否过滤萤石 / OEM 设备）。</summary>
    public (bool ok, string message) SetFilterRule(uint rule)
    {
        try
        {
            int ret = SadpNative.SADP_SetDeviceFilterRule(rule, IntPtr.Zero, 0);
            return ret != 0 ? (true, "过滤规则已生效") : (false, SadpError.Describe((uint)LastError()));
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}");
        }
    }

    /// <summary>主动发一次搜索报文（SADP 启动后会定时发，这里用于"手动刷新"）。</summary>
    public (bool ok, string message) SendInquiry()
    {
        try
        {
            int ret = SadpNative.SADP_SendInquiry();
            return ret != 0 ? (true, "已发出搜索请求") : (false, SadpError.Describe((uint)LastError()));
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 单独设置 SDK 日志开关（可随时调用，不必重启 SADP）。
    /// 目录为空则关闭写文件。
    /// </summary>
    public (bool ok, string message) SetSdkLogFile(int logLevel, string? logDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logDir))
            {
                // 传空目录即关闭落盘
                SadpNative.SADP_SetLogToFile(0, IntPtr.Zero, 1);
                return (true, "已关闭 SADP 日志落盘");
            }

            Directory.CreateDirectory(logDir);
            using var dir = new AnsiString(logDir);
            int ret = SadpNative.SADP_SetLogToFile(Math.Clamp(logLevel, 0, 6), dir.Pointer, 1);
            return ret != 0
                ? (true, $"SADP 日志等级 {logLevel}，目录 {logDir}")
                : (false, SadpError.Describe(SadpNative.SADP_GetLastError()));
        }
        catch (Exception ex)
        {
            return (false, $"设置 SADP 日志失败：{ex.Message}");
        }
    }

    /// <summary>SDK 内置版本号文本（4 段）。取不到时返回"未知"。</summary>
    public string VersionText
    {
        get
        {
            try { return FormatSadpVersion(SadpNative.SADP_GetSadpVersion()); }
            catch { return "未知"; }
        }
    }

    /// <summary>调整自动请求间隔（秒），0 表示恢复默认 60 秒。</summary>
    public void SetAutoRequestInterval(uint seconds)
    {
        try { SadpNative.SADP_SetAutoRequestInterval(seconds); }
        catch (Exception ex) { LogService.Current.Warn($"设置搜索间隔失败：{ex.Message}", "SADP"); }
    }

    /// <summary>清空 SDK 内部设备缓存。</summary>
    public void Clearup()
    {
        try { SadpNative.SADP_Clearup(); }
        catch (Exception ex) { LogService.Current.Warn($"清空缓存失败：{ex.Message}", "SADP"); }
    }

    // ==================== 网段扫描（跨网段搜存量设备） ====================

    /// <summary>对指定 IP 段做主动探测（不依赖广播，可跨网段）。</summary>
    public (bool ok, string message) StartSubnetProbe(SubnetProbeOptions options)
    {
        try
        {
            var native = new SADP_SUBNET_INFO_V20
            {
                dwSize = (uint)Marshal.SizeOf<SADP_SUBNET_INFO_V20>(),
                byIPType = 0,
                byIPProbeEnable = (byte)(options.IpProbeEnabled ? 1 : 0),
                byPortProbeEnable = (byte)(options.PortProbeEnabled ? 1 : 0),
                byRes2 = 0,
                wSDKPort = options.SdkPort,
                wSDKOverTlsPort = options.SdkOverTlsPort,
                wHttpPort = options.HttpPort,
                wHttpsPort = options.HttpsPort,
                wStartPort = options.StartPort,
                wStopPort = options.StopPort,
                byRes1 = new byte[4],
                szStartSubnetIP = new byte[48],
                szStopSubnetIP = new byte[48],
                dwIPProbeThreadNum = 256,
                dwPortProbeThreadNum = 256,
                dwIPProbeInterval = 0,
                dwPortProbeInterval = 0,
                dwIPProbeTimeout = options.IpProbeTimeoutMs,
                dwPortProbeConnectTimeout = options.ConnectTimeoutMs,
                dwProtocolProbeTimeout = options.ProtocolTimeoutMs,
                byRes = new byte[128],
            };

            NativeText.WriteFixed(native.szStartSubnetIP, options.StartIp);
            NativeText.WriteFixed(native.szStopSubnetIP, options.StopIp);

            int ret = SadpNative.SADP_InquirySpecificSubnetAllDevice(ref native);
            return ret != 0 ? (true, "已开始网段扫描") : (false, SadpError.Describe((uint)LastError()));
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}");
        }
    }

    /// <summary>查询网段扫描进度（0~100）。</summary>
    public (byte status, byte progress) GetSubnetProbeStatus()
    {
        try
        {
            var native = new SADP_SUBNET_STATUS
            {
                dwSize = (uint)Marshal.SizeOf<SADP_SUBNET_STATUS>(),
                byRes = new byte[6],
            };
            int ret = SadpNative.SADP_GetInquirySpecificSubnetAllDeviceStatus(ref native);
            return ret != 0 ? (native.byStatus, native.byProgress) : ((byte)0, (byte)0);
        }
        catch
        {
            return (0, 0);
        }
    }

    public void StopSubnetProbe()
    {
        try { SadpNative.SADP_StopInquirySpecificSubnetAllDevice(); }
        catch (Exception ex) { LogService.Current.Warn($"停止网段扫描失败：{ex.Message}", "SADP"); }
    }

    // ==================== V50 认证接口 ====================

    /// <summary>
    /// 用 SADP 通道直接验证设备账号密码（不占用 NetSDK 的会话）。
    /// 返回的原生结构体是 P/Invoke 细节，因此本方法只在程序集内部可用。
    /// </summary>
    internal (bool ok, string message, SADP_LOGIN_RET_INFO info) LoginDevice(string uniformDevId, string user, string password, uint timeoutMs = 0)
    {
        var param = new SADP_LOGIN_PARAM_V50
        {
            dwSize = (uint)Marshal.SizeOf<SADP_LOGIN_PARAM_V50>(),
            szUserName = new byte[64],
            szPassword = new byte[64],
            dwTimeout = timeoutMs,
            byRes = new byte[128],
        };
        NativeText.WriteFixed(param.szUserName, user);
        NativeText.WriteFixed(param.szPassword, password);

        try
        {
            int ret = SadpNative.SADP_LoginDevice_V50(uniformDevId, ref param, out var info);
            if (ret != 0) return (true, "认证通过", info);
            return (false, SadpError.Describe((uint)LastError()), info);
        }
        catch (Exception ex)
        {
            return (false, $"调用失败：{ex.Message}", default);
        }
    }

    public void LogoutDevice(string uniformDevId)
    {
        try { SadpNative.SADP_LogoutDevice_V50(uniformDevId); }
        catch { /* 忽略 */ }
    }

    // ==================== 释放 ====================

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        try { SadpNative.SADP_Clearup(); } catch { /* 忽略 */ }
        _disposed = true;
    }
}

/// <summary>修改网络参数的输入。</summary>
public sealed class NetParamInput
{
    public string IPv4Address { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string Gateway { get; set; } = string.Empty;
    public string IPv6Address { get; set; } = string.Empty;
    public string IPv6Gateway { get; set; } = string.Empty;
    public byte IPv6MaskLen { get; set; }
    public bool DhcpEnabled { get; set; }
    public ushort SdkPort { get; set; } = 8000;
    public ushort HttpPort { get; set; } = 80;
    public uint SdkOverTlsPort { get; set; }
}

/// <summary>网段扫描选项。</summary>
public sealed class SubnetProbeOptions
{
    public string StartIp { get; set; } = string.Empty;
    public string StopIp { get; set; } = string.Empty;
    public bool IpProbeEnabled { get; set; } = true;
    public bool PortProbeEnabled { get; set; }
    public ushort SdkPort { get; set; } = 8000;
    public ushort SdkOverTlsPort { get; set; } = 8443;
    public ushort HttpPort { get; set; } = 80;
    public ushort HttpsPort { get; set; } = 443;
    public ushort StartPort { get; set; } = 1024;
    public ushort StopPort { get; set; } = 65535;
    public uint IpProbeTimeoutMs { get; set; }
    public uint ConnectTimeoutMs { get; set; }
    public uint ProtocolTimeoutMs { get; set; }
}

/// <summary>密码重置输入。</summary>
public sealed class ResetInput
{
    /// <summary>byResetType，取值见 <see cref="SadpConst.RESET_BY_FILE"/> ~ <see cref="SadpConst.RESET_BY_PHONE"/>。</summary>
    public byte ResetType { get; set; } = SadpConst.RESET_BY_FILE;

    public string NewPassword { get; set; } = string.Empty;
    public bool SyncIpcPassword { get; set; }

    /// <summary>重置口令。方式 3/6 填设备码；方式 7 填手机扫码后返回的串。</summary>
    public string Code { get; set; } = string.Empty;

    public string AuthFile { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;

    /// <summary>预留邮箱地址，方式 6 专用。</summary>
    public string MailBox { get; set; } = string.Empty;

    /// <summary>手机号，方式 7 专用。</summary>
    public string PhoneNo { get; set; } = string.Empty;

    public List<string> Answers { get; } = [];
}

/// <summary>二维码来源。</summary>
public enum QrKind
{
    /// <summary>二维码内容 = 设备码（配合 byResetType=3）。</summary>
    DeviceCode,

    /// <summary>手机扫码重置（byResetType=7），内容为"域名?code=D:型号**密文"。</summary>
    PhoneScan,

    /// <summary>预留邮箱重置（byResetType=6），内容为设备返回的二维码原文。</summary>
    Mailbox,
}

/// <summary>一份可以直接拿去编码成二维码的数据。</summary>
public sealed class QrPayload
{
    public QrKind Kind { get; set; }

    /// <summary>二维码最终承载的字符串。<b>编码器只认这个字段。</b></summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>设备返回的原始二维码密文（未拼接域名/型号）。</summary>
    public string RawQr { get; set; } = string.Empty;

    public string DomainName { get; set; } = string.Empty;
    public string DeviceModel { get; set; } = string.Empty;
    public string PhoneNo { get; set; } = string.Empty;
    public string MailBox { get; set; } = string.Empty;
    public string ServiceMailBox { get; set; } = string.Empty;

    /// <summary>二维码剩余有效时间（秒）。0 表示设备未告知。</summary>
    public uint ValidSeconds { get; set; }

    public string KindLabel => Kind switch
    {
        QrKind.DeviceCode => "设备码二维码",
        QrKind.PhoneScan => "手机扫码重置",
        QrKind.Mailbox => "预留邮箱重置",
        _ => "二维码",
    };
}

/// <summary>设备已配置的找回方式（SADP_GET_PASSWORD_RESET_TYPE 的解析结果）。</summary>
public sealed class PasswordResetAbility
{
    public bool AnyConfigured { get; set; }
    public bool GuidExported { get; set; }
    public bool SecurityQuestionConfigured { get; set; }
    public bool SecurityMailboxConfigured { get; set; }
    public bool HikConnectBound { get; set; }

    /// <summary>给用户看的一行结论。</summary>
    public string Describe()
    {
        if (!AnyConfigured) return "设备未配置任何自助找回方式（只能靠设备码/GUID 文件走官方工具）";

        var parts = new List<string>();
        if (GuidExported) parts.Add("已导出过 GUID");
        if (SecurityQuestionConfigured) parts.Add("已设安全问题");
        if (SecurityMailboxConfigured) parts.Add("已设安全邮箱");
        if (HikConnectBound) parts.Add("已绑定 Hik-Connect");
        return parts.Count == 0 ? "已配置找回方式" : "已配置：" + string.Join("、", parts);
    }
}

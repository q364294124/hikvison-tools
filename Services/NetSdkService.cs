using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using HikDeployTool.Native;

namespace HikDeployTool.Services;

/// <summary>一次设备登录的结果。</summary>
public sealed class LoginSession
{
    public bool Ok { get; init; }
    public int UserId { get; init; } = -1;

    /// <summary>失败时的 SDK 错误码（NET_DVR_GetLastError 原始值），成功为 0。</summary>
    public uint ErrorCode { get; init; }

    /// <summary>
    /// 设备返回的剩余锁定时间（秒）。连续多次密码错误会触发设备端账号锁定，
    /// 锁定期间登录会被拒。来自 NET_DVR_DEVICEINFO_V40.dwSurplusLockTime，
    /// 只有设备应答里带了才 &gt; 0。
    /// </summary>
    public uint SurplusLockTime { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>登录成功时走的通道：0-私有协议（SDK 端口），1-ISAPI（HTTP 端口）。失败为 0。</summary>
    public byte LoginMode { get; init; }

    /// <summary>登录通道的中文说明，界面可以直接显示。</summary>
    public string LoginModeText => LoginMode == 1 ? "ISAPI（HTTP）" : "私有协议（SDK 端口）";

    /// <summary>
    /// 原始登录应答结构体。
    /// 刻意是 internal：NET_DVR_DEVICEINFO_V30 属于 P/Invoke 的私有细节，
    /// 不对外暴露，上层要用哪个字段就从这里取到的会话字段里读（如 ChannelCount）。
    /// </summary>
    internal NET_DVR_DEVICEINFO_V30 Info { get; init; }

    public string SerialNo { get; init; } = string.Empty;

    /// <summary>模拟通道数（DVR / NVR 本地通道）。</summary>
    public int ChannelCount { get; init; }

    /// <summary>模拟通道起始号（通常为 1）。</summary>
    public int StartChannel { get; init; }

    /// <summary>IP（数字）通道数。IPC 通常为 0；NVR 接网络摄像头时 &gt; 0。</summary>
    public int IpChannelCount { get; init; }

    /// <summary>IP（数字）通道起始号，通常为 33；没有数字通道时为 0。</summary>
    public int StartDigitalChannel { get; init; }
    public ushort DeviceType { get; init; }
    public byte PasswordLevel { get; init; }
    public bool NeedChangePassword { get; init; }
}

/// <summary>
/// 通道实况扫描结果：哪些通道真的接了摄像机。
///
/// Ok=false 表示**没能判定**（老固件不开放这些 ISAPI / 非海康设备），
/// 调用方必须原样列出全部通道 —— 宁可多列几路，也不能把真画面藏起来。
/// </summary>
public sealed record ChannelScanResult(bool Ok, string Message, HashSet<int> Channels, bool AnalogKnown);

/// <summary>
/// HCNetSDK 服务封装。
///
/// 本工具用到的 SDK 能力：
///   NET_DVR_Init / SetSDKInitCfg / SetConnectTime → 环境准备
///   NET_DVR_Login_V40 / Logout                    → 设备可达性 + 身份校验
///   NET_DVR_STDXMLConfig                           → ISAPI 透传（读型号/固件/时间）
///   NET_DVR_CaptureJPEGPicture                     → 抓图存证
///   NET_DVR_RealPlay_V40 / StopRealPlay            → 实时预览（渲染到窗口句柄）
///
/// 预览采用 hPlayWnd 模式：SDK 内部用 PlayCtrl 解码渲染，
/// 上层只负责给一个窗口句柄，不需要自己 P/Invoke PlayM4_* 系列做解码。
/// </summary>
public sealed class NetSdkService : IDisposable
{
    private readonly object _gate = new();
    private bool _initialized;
    private bool _disposed;

    public bool IsInitialized
    {
        get { lock (_gate) return _initialized; }
    }

    /// <summary>初始化 SDK。返回 (是否成功, 提示信息)。</summary>
    public (bool ok, string message) Initialize(int connectTimeoutMs = 5000, uint retryTimes = 3, string? sdkLogDir = null, int sdkLogLevel = 3)
    {
        lock (_gate)
        {
            if (_initialized) return (true, "已初始化");
            if (_disposed) return (false, "SDK 已释放");

            try
            {
                // 显式告诉 SDK 依赖库所在目录，避免它去别处找 HCNetSDKCom / OpenSSL
                SetSdkPath(AppContext.BaseDirectory);

                int ret = HCNetSdkNative.NET_DVR_Init();
                if (ret == 0)
                {
                    uint err = HCNetSdkNative.NET_DVR_GetLastError();
                    var msg = NetSdkError.Describe(err);
                    LogService.Current.Error($"HCNetSDK 初始化失败：{msg}", "NetSDK");
                    return (false, msg);
                }

                HCNetSdkNative.NET_DVR_SetConnectTime((uint)Math.Max(1000, connectTimeoutMs), Math.Max(1u, retryTimes));
                HCNetSdkNative.NET_DVR_SetReconnect(10000, 1);

                if (!string.IsNullOrWhiteSpace(sdkLogDir))
                {
                    try
                    {
                        using var logDir = new AnsiString(sdkLogDir);
                        HCNetSdkNative.NET_DVR_SetLogToFile((uint)sdkLogLevel, logDir.Pointer, 1);
                    }
                    catch (Exception ex) { LogService.Current.Warn($"设置 NetSDK 日志失败：{ex.Message}", "NetSDK"); }
                }

                _initialized = true;
                uint version = 0;
                try { version = HCNetSdkNative.NET_DVR_GetSDKVersion(); } catch { /* 忽略 */ }
                LogService.Current.Info($"HCNetSDK 初始化成功（版本号 0x{version:X8}）", "NetSDK");
                return (true, "初始化成功");
            }
            catch (DllNotFoundException ex)
            {
                var msg = $"未找到 HCNetSDK.dll 或其依赖库，请确认程序目录下已部署完整运行库。{ex.Message}";
                LogService.Current.Error(msg, "NetSDK");
                return (false, msg);
            }
            catch (BadImageFormatException)
            {
                const string msg = "HCNetSDK.dll 位数与程序不匹配（本程序为 64 位，请使用 Win64 版 SDK）。";
                LogService.Current.Error(msg, "NetSDK");
                return (false, msg);
            }
            catch (Exception ex)
            {
                LogService.Current.Error("HCNetSDK 初始化异常", ex, "NetSDK");
                return (false, ex.Message);
            }
        }
    }

    /// <summary>
    /// 显式告诉 SDK 依赖库（HCNetSDKCom / OpenSSL）所在目录。
    ///
    /// ⚠️ 这里曾经是「SDK 29 登录失败」的根因：早期用 Encoding.ASCII 编码
    /// 路径，安装目录含中文（如 …\Desktop\海康\…）时被写成 "???"；中间一度
    /// 改用 Encoding.Default 也不对——.NET Core 起 Default 是 UTF-8 而不是
    /// 系统 ANSI。SDK 拿着坏路径加载 OpenSSL 失败（日志表现：
    /// m_fnSSLeayVersion[0x0] Unload），登录时生成不了 RSA 公钥，退化成
    /// 84 字节的旧式无密钥登录包——设备一律拒绝（私有协议报 29、ISAPI 报 11），
    /// 而海康自家客户端不受影响。已用本机假服务器抓包对比实锤：
    /// 路径坏 → 84 字节；路径对或不设 → 224 字节（带 RSA 公钥交换）。
    /// 修复：用 NativeText.ToAnsi（Win32 CP_ACP，真 GBK）编码；编码不了的
    /// 路径干脆不设置——SDK 会以 HCNetSDK.dll 自身所在目录为基准加载依赖。
    /// </summary>
    private static void SetSdkPath(string dir)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            // 路径必须能无损映射到系统 ANSI 代码页，否则写进去就是坑 SDK
            var raw = NativeText.ToAnsi(dir);
            if (raw.Length == 0 || NativeText.FromAnsi(raw) != dir)
            {
                LogService.Current.Warn("SDK 安装路径含系统 ANSI 无法表示的字符，改为由 SDK 自行定位依赖库目录", "NetSDK");
                return;
            }

            var cfg = new NET_DVR_LOCAL_SDK_PATH
            {
                sPath = new byte[NetSdkConst.NET_SDK_MAX_FILE_PATH],
                byRes = new byte[128],
            };
            int copy = Math.Min(raw.Length, NetSdkConst.NET_SDK_MAX_FILE_PATH - 1);
            Array.Copy(raw, cfg.sPath, copy);

            ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NET_DVR_LOCAL_SDK_PATH>());
            Marshal.StructureToPtr(cfg, ptr, false);
            HCNetSdkNative.NET_DVR_SetSDKInitCfg(NetSdkConst.NET_SDK_INIT_CFG_SDK_PATH, ptr);
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"设置 SDK 库路径失败（可忽略）：{ex.Message}", "NetSDK");
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// 登录设备。
    ///
    /// 用 NET_DVR_Login_V40（官方推荐，V30 老接口对部分新固件存在兼容问题，
    /// 会报出莫名其妙的 29/47）。登录链分三段，全部自动完成：
    ///
    ///   1. 私有协议（byLoginMode=0，SDK 端口 8000）—— 传统路径；
    ///   2. 失败且 29/7 → **ISAPI 回退（byLoginMode=1，HTTP 端口）**：
    ///      部分新固件/新激活设备会拒绝私有协议登录（报 29），但 HTTP 通道
    ///      是好的——海康自家新客户端（iVMS-4200 新版、海康互联）走的
    ///      正是 ISAPI。这一步还能"借 ISAPI 的口"问出真实原因：
    ///      ISAPI 若报 1/5/6（凭据错），说明密码确实有问题，直接如实上报；
    ///   3. ISAPI 也不通 → 按 1.5s / 4s 再重试私有协议两次（设备端释放
    ///      连接有快有慢），仍 29 给出完整排查清单。
    /// </summary>
    public LoginSession Login(string ip, ushort port, string user, string password, ushort httpPort = 80)
    {
        if (!_initialized)
        {
            var init = Initialize();
            if (!init.ok)
                return new LoginSession { Ok = false, Message = init.message };
        }

        // ---- 第一段：私有协议 ----
        var first = LoginOnce(ip, port, user, password, loginMode: 0);
        if (first.Ok) return first;

        if (first.ErrorCode is 29 or 7)
        {
            // ---- 第二段：ISAPI 回退（HTTP 端口）----
            ushort http = httpPort == 0 ? (ushort)80 : httpPort;
            var viaIsapi = LoginOnce(ip, http, user, password, loginMode: 1);
            if (viaIsapi.Ok)
            {
                LogService.Current.Warn(
                    $"登录 {ip} 私有协议（{port}）被拒（SDK {first.ErrorCode}），改走 ISAPI（HTTP {http}）登录成功", "NetSDK");
                return new LoginSession
                {
                    Ok = true,
                    UserId = viaIsapi.UserId,
                    LoginMode = 1,
                    Message = "登录成功（ISAPI）",
                    Info = viaIsapi.Info,
                    SerialNo = viaIsapi.SerialNo,
                    ChannelCount = viaIsapi.ChannelCount,
                    StartChannel = viaIsapi.StartChannel,
                    IpChannelCount = viaIsapi.IpChannelCount,
                    StartDigitalChannel = viaIsapi.StartDigitalChannel,
                    DeviceType = viaIsapi.DeviceType,
                    PasswordLevel = viaIsapi.PasswordLevel,
                    NeedChangePassword = viaIsapi.NeedChangePassword,
                };
            }

            // ISAPI 问出了真话：凭据确实有问题（1-密码错 5-无此用户 6-密码错）
            // → 如实上报，别再折腾私有协议把原因搅浑
            if (viaIsapi.ErrorCode is 1 or 5 or 6)
            {
                LogService.Current.Warn($"登录 {ip} 私有协议被拒（SDK {first.ErrorCode}），ISAPI 确认凭据错误（SDK {viaIsapi.ErrorCode}）", "NetSDK");
                return viaIsapi;
            }

            // ---- 第三段：私有协议重试 ----
            bool portOpen = ProbeTcpPort(ip, port);
            if (!portOpen)
            {
                var portMsg = $"SDK 端口 {port} 无法建立 TCP 连接：设备可能改过 SDK 端口（网页端 配置→网络→高级配置 里查看），或网络/防火墙不可达（SDK {first.ErrorCode}）";
                LogService.Current.Warn($"登录 {ip}:{port} 失败：{portMsg}", "NetSDK");
                return new LoginSession { Ok = false, ErrorCode = first.ErrorCode, Message = portMsg };
            }

            int[] delays = [1500, 4000];
            for (int i = 0; i < delays.Length; i++)
            {
                Thread.Sleep(delays[i]);
                var retry = LoginOnce(ip, port, user, password, loginMode: 0);
                if (retry.Ok)
                {
                    LogService.Current.Info($"登录 {ip}:{port} 首次失败（SDK {first.ErrorCode}），第 {i + 1} 次重试（隔 {delays[i]}ms）成功", "NetSDK");
                    return retry;
                }
                if (retry.ErrorCode != 29 && retry.ErrorCode != 7)
                    return retry;   // 换了别的错误（如密码 1），按新错误走，别再重试
                LogService.Current.Warn($"登录 {ip}:{port} 第 {i + 1} 次重试仍失败（SDK {retry.ErrorCode}）", "NetSDK");
            }

            var last = LoginOnce(ip, port, user, password, loginMode: 0);   // 最后再试一次拿最新的错误详情
            if (last.Ok) return last;

            if (last.ErrorCode == 29)
            {
                var isapiHint = viaIsapi.ErrorCode == 7
                    ? $"设备 HTTP 端口 {http} 也不通，ISAPI 通道走不了；"
                    : $"ISAPI（HTTP {http}）同样被拒（SDK {viaIsapi.ErrorCode}）；";
                var busyMsg = $"设备 {port} 端口可达但拒绝登录（SDK 29），私有协议与 ISAPI 通道均未成功。{isapiHint}" +
                              "按顺序排查：① 手机 App（萤石云视 / Hik-Connect）正在看直播的先退掉；② 关掉 iVMS-4200、浏览器里打开的设备网页端；" +
                              "③ 其它电脑上的客户端也在连这台设备的都关掉；④ 本工具是否开了多个窗口（多开互挤）；" +
                              "⑤ 等 1 分钟再点登录。仍不行就把设备断电重启，起来后先别开其它客户端、直接用本工具连。" +
                              "（提示：绑定了萤石云/Hik-Connect 平台的设备，平台自身也会占一路连接）";
                if (last.SurplusLockTime > 0)
                    busyMsg = $"设备账号还在锁定中，约 {last.SurplusLockTime} 秒后自动解锁（连续密码错误会触发）。" + busyMsg;
                LogService.Current.Warn($"登录 {ip}:{port} 失败：{busyMsg}", "NetSDK");
                return new LoginSession { Ok = false, ErrorCode = last.ErrorCode, SurplusLockTime = last.SurplusLockTime, Message = busyMsg };
            }

            return last;
        }

        return first;
    }

    /// <summary>单次登录（V40，同步模式）。不做任何重试与诊断。</summary>
    /// <param name="loginMode">0-私有协议（SDK 端口），1-ISAPI（此时 port 传 HTTP 端口）。</param>
    private LoginSession LoginOnce(string ip, ushort port, string user, string password, byte loginMode)
    {
        try
        {
            var loginInfo = new NET_DVR_USER_LOGIN_INFO
            {
                sDeviceAddress = new byte[NetSdkConst.MAX_DEV_ADDR_LEN],
                sUserName = new byte[NetSdkConst.LOGIN_USERNAME_MAX_LEN],
                sPassword = new byte[NetSdkConst.LOGIN_PASSWD_MAX_LEN],
                // 保留字段必须全零 —— 头文件要求 memset 清零，凑合不得
                byRes1 = new byte[3],
                byRes3 = new byte[109],
                // 同步登录：回调传零指针，结果直接从返回值拿
                cbLoginResult = IntPtr.Zero,
                pUser = IntPtr.Zero,
                cbLoginResultV40 = IntPtr.Zero,
                bUseAsynLogin = 0,
                // 0 = 私有协议（SDK 端口，通常 8000）；1 = ISAPI（port 传 HTTP 端口）
                byLoginMode = loginMode,
                byHttps = 0,
            };
            WriteAnsi(loginInfo.sDeviceAddress, ip);
            WriteAnsi(loginInfo.sUserName, user);
            WriteAnsi(loginInfo.sPassword, password);
            loginInfo.wPort = port == 0 ? (ushort)(loginMode == 1 ? 80 : 8000) : port;

            var deviceInfo = new NET_DVR_DEVICEINFO_V40
            {
                struDeviceV30 = new NET_DVR_DEVICEINFO_V30 { sSerialNumber = new byte[NetSdkConst.SERIALNO_LEN] },
                byRes2 = new byte[233],
            };

            int userId = HCNetSdkNative.NET_DVR_Login_V40(ref loginInfo, ref deviceInfo);
            if (userId < 0)
            {
                uint err = HCNetSdkNative.NET_DVR_GetLastError();
                return new LoginSession
                {
                    Ok = false,
                    ErrorCode = err,
                    Message = NetSdkError.Describe(err),
                    Info = deviceInfo.struDeviceV30,
                    // 设备在密码锁定等场景会随失败应答回这个字段，带出去给诊断链用
                    SurplusLockTime = deviceInfo.dwSurplusLockTime,
                };
            }

            var v30 = deviceInfo.struDeviceV30;
            return new LoginSession
            {
                Ok = true,
                UserId = userId,
                LoginMode = loginMode,
                Message = "登录成功",
                Info = v30,
                SerialNo = NativeText.FromAnsi(v30.sSerialNumber),
                ChannelCount = v30.byChanNum,
                StartChannel = v30.byStartChan,
                IpChannelCount = v30.byIPChanNum | (v30.byHighDChanNum << 8),
                StartDigitalChannel = v30.byStartDChan,
                DeviceType = v30.wDevType,
                // V40 顶层才有密码强度：0-默认密码风险 1-低 2-中 3-高
                PasswordLevel = deviceInfo.byPasswordLevel,
                NeedChangePassword = deviceInfo.byPasswordLevel is 0 or 3,
            };
        }
        catch (Exception ex)
        {
            LogService.Current.Error($"登录 {ip} 时异常", ex, "NetSDK");
            return new LoginSession { Ok = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// 把字符串按系统 ANSI（Win32 CP_ACP，中文 Windows = GBK）写进定长 byte[]，自动截断并补 0。
    /// ⚠️ 不要用 Encoding.Default：.NET Core 起它是 UTF-8，写进 SDK 结构体的中文会变乱码。
    /// </summary>
    private static void WriteAnsi(byte[] target, string value)
    {
        var raw = NativeText.ToAnsi(value);
        int copy = Math.Min(raw.Length, target.Length - 1);
        Array.Clear(target, 0, target.Length);
        Array.Copy(raw, target, copy);
    }

    /// <summary>探测 TCP 端口能否建立连接（区分"网络不通"与"设备拒绝登录"）。</summary>
    private static bool ProbeTcpPort(string ip, ushort port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(ip, port == 0 ? 8000 : port);
            return task.Wait(2000) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public void Logout(int userId)
    {
        if (userId < 0) return;
        try { HCNetSdkNative.NET_DVR_Logout(userId); }
        catch (Exception ex) { LogService.Current.Warn($"登出失败：{ex.Message}", "NetSDK"); }
    }

    /// <summary>
    /// ISAPI 透传（GET）。url 例如 <c>/ISAPI/System/deviceInfo</c>。
    /// 返回设备应答的 XML 文本。
    /// </summary>
    public (bool ok, string xml, string message) IsapiGet(int userId, string url, int timeoutMs = 5000)
        => IsapiRequest(userId, url, null, timeoutMs);

    /// <summary>
    /// ISAPI 透传（PUT / POST）。url 必须以 <c>"PUT "</c> 或 <c>"POST "</c> 开头指定
    /// HTTP 方法（SDK 透传的约定，官方 Demo 同款写法），例如：
    ///   PUT /ISAPI/AccessControl/RemoteControl/door/1
    ///   POST /ISAPI/AccessControl/AcsEvent
    /// 不带前缀会被当成 GET，设备会应答 405。
    /// </summary>
    public (bool ok, string xml, string message) IsapiPut(int userId, string url, string body, int timeoutMs = 8000)
        => IsapiRequest(userId, url, body, timeoutMs);

    private (bool ok, string xml, string message) IsapiRequest(int userId, string url, string? body, int timeoutMs)
    {
        const int outSize = 512 * 1024;
        const int statusSize = 64 * 1024;

        // 方法前缀规范化：本机 SDK 的 STDXMLConfig 要求 URL 以 "GET "/"PUT "/"POST "
        // 开头显式指定方法——不带前缀的裸 URL 会直接报 SDK 17（参数错误），
        // 表现是"所有 GET 全军覆没、带前缀的 PUT/POST 却能到达设备"。
        string fullUrl = url.StartsWith("GET ", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("PUT ", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("POST ", StringComparison.OrdinalIgnoreCase)
            ? url
            : "GET " + url;

        IntPtr urlPtr = IntPtr.Zero, inPtr = IntPtr.Zero, outPtr = IntPtr.Zero, statusPtr = IntPtr.Zero;
        try
        {
            var urlBytes = NativeText.ToAnsi(fullUrl);
            var inBytes = body == null ? null : new UTF8Encoding(false).GetBytes(body);

            urlPtr = Marshal.AllocHGlobal(urlBytes.Length + 1);
            Marshal.Copy(urlBytes, 0, urlPtr, urlBytes.Length);
            Marshal.WriteByte(urlPtr, urlBytes.Length, 0);

            if (inBytes != null)
            {
                inPtr = Marshal.AllocHGlobal(inBytes.Length + 1);
                Marshal.Copy(inBytes, 0, inPtr, inBytes.Length);
                Marshal.WriteByte(inPtr, inBytes.Length, 0);
            }

            outPtr = Marshal.AllocHGlobal(outSize);
            statusPtr = Marshal.AllocHGlobal(statusSize);
            for (int i = 0; i < outSize; i += 4096) Marshal.WriteByte(outPtr, i, 0);
            Marshal.WriteByte(statusPtr, 0, 0);

            var input = new NET_DVR_XML_CONFIG_INPUT
            {
                dwSize = (uint)Marshal.SizeOf<NET_DVR_XML_CONFIG_INPUT>(),
                lpRequestUrl = urlPtr,
                dwRequestUrlLen = (uint)urlBytes.Length,                lpInBuffer = inPtr,
                dwInBufferSize = (uint)(inBytes?.Length ?? 0),
                dwRecvTimeOut = (uint)Math.Max(1000, timeoutMs),
                byForceEncrpt = 0,
                byNumOfMultiPart = 0,
                byMIMEType = 0,
                byRes1 = 0,
                dwSendTimeOut = (uint)Math.Max(1000, timeoutMs),
                sPassword = IntPtr.Zero,
                byRes = new byte[16],
            };

            var output = new NET_DVR_XML_CONFIG_OUTPUT
            {
                dwSize = (uint)Marshal.SizeOf<NET_DVR_XML_CONFIG_OUTPUT>(),
                lpOutBuffer = outPtr,
                dwOutBufferSize = outSize,
                dwReturnedXMLSize = 0,
                lpStatusBuffer = statusPtr,
                dwStatusSize = statusSize,
                lpDataBuffer = IntPtr.Zero,
                byNumOfMultiPart = 0,
                byRes = new byte[23],
            };

            int ret = HCNetSdkNative.NET_DVR_STDXMLConfig(userId, ref input, ref output);
            if (ret == 0)
            {
                // SDK 只给错误码；真正的失败原因（如 Invalid Operation / Bad Request）
                // 在状态缓冲的 XML 里，把它捞出来拼到错误信息，现场排障不用再抓包。
                string msg = DescribeFailure(statusPtr, NetSdkError.Describe(HCNetSdkNative.NET_DVR_GetLastError()));
                var rawStatus = RawStatusText(statusPtr);
                if (rawStatus != null)
                    LogService.Current.Warn($"ISAPI 透传被拒 {url}：{rawStatus}", "NetSDK");
                return (false, string.Empty, msg);
            }

            uint size = output.dwReturnedXMLSize;
            if (size == 0 || size > outSize) size = outSize;
            var raw = new byte[size];
            Marshal.Copy(outPtr, raw, 0, (int)size);

            // 去掉尾部填充的 0
            int end = Array.IndexOf(raw, (byte)0);
            if (end > 0) raw = raw[..end];

            var xml = DecodeIsapi(raw);
            return string.IsNullOrWhiteSpace(xml)
                ? (true, string.Empty, "命令成功但设备未返回内容")
                : (true, xml, "成功");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"透传调用失败：{ex.Message}");
        }
        finally
        {
            if (urlPtr != IntPtr.Zero) Marshal.FreeHGlobal(urlPtr);
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (outPtr != IntPtr.Zero) Marshal.FreeHGlobal(outPtr);
            if (statusPtr != IntPtr.Zero) Marshal.FreeHGlobal(statusPtr);
        }
    }

    /// <summary>ISAPI 应答按 UTF-8 解码；若含非法 UTF-8 序列则退回 ANSI(GBK)。</summary>
    private static string DecodeIsapi(byte[] raw)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return NativeText.FromAnsi(raw);
        }
    }

    /// <summary>
    /// 透传失败时读状态缓冲（ResponseStatus XML），取出 statusString 附加到错误信息。
    /// 失败不致命：缓冲读不出来就退回纯 SDK 错误码描述。
    /// </summary>
    private static string DescribeFailure(IntPtr statusPtr, string fallback)
    {
        try
        {
            int len = 0;
            while (len < 4096 && Marshal.ReadByte(statusPtr, len) != 0) len++;
            if (len == 0) return fallback;
            var raw = new byte[len];
            Marshal.Copy(statusPtr, raw, 0, len);
            var text = DecodeIsapi(raw);
            var doc = System.Xml.Linq.XDocument.Parse(text);
            string? statusString = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "statusString")?.Value.Trim();
            return string.IsNullOrWhiteSpace(statusString) ? fallback : $"{fallback}（{statusString}）";
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>设备拒绝时的原始应答文本（含 requestURL/subStatusCode），只进日志不进界面。</summary>
    private static string? RawStatusText(IntPtr statusPtr)
    {
        try
        {
            int len = 0;
            while (len < 8192 && Marshal.ReadByte(statusPtr, len) != 0) len++;
            if (len == 0) return null;
            var raw = new byte[len];
            Marshal.Copy(statusPtr, raw, 0, len);
            var text = DecodeIsapi(raw).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>抓取一路 JPEG 图片到文件。</summary>
    public (bool ok, string message, long bytes) CaptureJpeg(int userId, int channel, string filePath,
        ushort picSize = NetSdkConst.PIC_SIZE_AUTO, ushort picQuality = NetSdkConst.PIC_QUALITY_MID)
    {
        try
        {
            var para = new NET_DVR_JPEGPARA { wPicSize = picSize, wPicQuality = picQuality };
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fileAnsi = new AnsiString(filePath);
            int ret = HCNetSdkNative.NET_DVR_CaptureJPEGPicture(userId, channel, ref para, fileAnsi.Pointer);
            if (ret == 0)
                return (false, NetSdkError.Describe(HCNetSdkNative.NET_DVR_GetLastError()), 0);

            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0)
                return (false, "抓图命令成功但未生成有效文件", 0);
            return (true, "抓图成功", fi.Length);
        }
        catch (Exception ex)
        {
            return (false, $"抓图异常：{ex.Message}", 0);
        }
    }

    /// <summary>
    /// 开始实时预览。返回 (取流句柄, 失败原因)——句柄 &lt; 0 即失败。
    /// 注意：这是阻塞调用（连接失败要等约 5 秒才返回），别在 UI 线程上调用。
    /// </summary>
    public (int handle, string message) RealPlay(int userId, int channel, IntPtr playWnd, uint streamType = 0)
    {
        try
        {
            var info = NET_DVR_PREVIEWINFO.Create(channel, streamType, playWnd);
            int handle = HCNetSdkNative.NET_DVR_RealPlay_V40(userId, ref info, IntPtr.Zero, IntPtr.Zero);
            if (handle < 0)
                return (-1, NetSdkError.Describe(HCNetSdkNative.NET_DVR_GetLastError()));
            return (handle, "预览已建立");
        }
        catch (Exception ex)
        {
            LogService.Current.Error($"启动预览（通道 {channel}）异常", ex, "NetSDK");
            return (-1, $"启动预览异常：{ex.Message}");
        }
    }

    /// <summary>停止实时预览。句柄无效时静默忽略（页面切换 / 程序退出都会走到这里）。</summary>
    public void StopRealPlay(int handle)
    {
        if (handle < 0) return;
        try { HCNetSdkNative.NET_DVR_StopRealPlay(handle); }
        catch (Exception ex) { LogService.Current.Warn($"停止预览失败：{ex.Message}", "NetSDK"); }
    }

    /// <summary>最近一次 SDK 错误码（NET_DVR_GetLastError），给上层拼错误提示用。</summary>
    public uint LastError => HCNetSdkNative.NET_DVR_GetLastError();

    // ==================== 预览音频 ====================

    /// <summary>
    /// 打开预览音频（全局开关）。hPlayWnd 解码模式下声音不走 PlayM4
    /// 单通道，用 NET_DVR_ClientAudioStart：对所有在播画面生效，
    /// 同一时刻只混播一路。需先有画面在播，否则 SDK 打不开。
    /// </summary>
    public (bool ok, string message) StartAudio()
    {
        try
        {
            if (HCNetSdkNative.NET_DVR_ClientAudioStart())
            {
                LogService.Current.Info("预览音频已打开", "NetSDK");
                return (true, "音频已打开");
            }
            uint err = HCNetSdkNative.NET_DVR_GetLastError();
            LogService.Current.Warn($"打开预览音频失败：SDK {err}", "NetSDK");
            return (false, $"打开音频失败（错误码 {err}，需先有画面在播）");
        }
        catch (Exception ex)
        {
            LogService.Current.Error("打开预览音频异常", ex, "NetSDK");
            return (false, $"打开音频异常：{ex.Message}");
        }
    }

    /// <summary>关闭预览音频。重复调用无副作用。</summary>
    public void StopAudio()
    {
        try
        {
            if (HCNetSdkNative.NET_DVR_ClientAudioStop())
                LogService.Current.Info("预览音频已关闭", "NetSDK");
        }
        catch (Exception ex) { LogService.Current.Warn($"关闭预览音频失败：{ex.Message}", "NetSDK"); }
    }

    // ==================== 云台 ====================

    /// <summary>云台命令常量（HCNetSDK.h:1920，本版 SDK 的取值，别按老版记忆写错）。</summary>
    internal static class PtzCommand
    {
        public const uint ZoomIn = 11;        // 变倍 +（倍率变大）
        public const uint ZoomOut = 12;       // 变倍 -
        public const uint FocusNear = 13;     // 聚焦 +（前调）
        public const uint FocusFar = 14;      // 聚焦 -（后调）
        public const uint TiltUp = 21;        // 上仰
        public const uint TiltDown = 22;      // 下俯
        public const uint PanLeft = 23;       // 左转
        public const uint PanRight = 24;      // 右转
        public const uint UpLeft = 25;        // 上仰 + 左转
        public const uint UpRight = 26;
        public const uint DownLeft = 27;
        public const uint DownRight = 28;
    }

    /// <summary>
    /// 云台控制（"按住转动"模型）：stop=false 开始转、stop=true 停止。
    /// 速度固定 4（1-7 的中间值），够顺滑也不至于转过头。
    /// </summary>
    public bool PtzControl(int userId, int channel, uint command, bool stop, uint speed = 4)
    {
        try
        {
            return HCNetSdkNative.NET_DVR_PTZControlWithSpeed_Other(userId, channel, command, stop ? 1u : 0u, speed);
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"云台控制异常（通道 {channel}）：{ex.Message}", "NetSDK");
            return false;
        }
    }

    /// <summary>
    /// 查询通道是否支持云台：ISAPI 的 capabilities 应答里有 PTZCaps 结构就算支持。
    /// 不支持/非海康/门禁设备会拿到 404 或错误，统一返回 false（界面就不显示云台面板）。
    /// </summary>
    public (bool supported, string message) CheckPtzSupport(int userId, int channel)
    {
        var (ok, xml, msg) = IsapiGet(userId, $"/ISAPI/PTZCtrl/channels/{channel}/capabilities");
        if (ok && !string.IsNullOrWhiteSpace(xml) && xml.Contains("PTZCaps", StringComparison.OrdinalIgnoreCase))
            return (true, string.Empty);
        return (false, ok ? "设备能力描述中不含云台" : msg);
    }

    /// <summary>
    /// 扫描"真的有画面"的通道（录像机预览过滤空通道用）。
    ///
    /// 背景：录像机是按**机型**报通道数的 —— 8 路机器就算只接了 3 个摄像头，
    /// 登录应答照样说 8 路通道，预览页于是列出一串点开全黑的通道。
    /// 这里用 ISAPI 问设备"哪些通道真有视频源"：
    ///   ① `/ISAPI/System/Video/inputs/channels`            —— 本地（模拟）视频输入，看 videoInputEnabled；
    ///   ② `/ISAPI/ContentMgmt/InputProxy/channels/status`  —— 已接入的网络摄像机，看 online；
    ///   ③ `/ISAPI/ContentMgmt/InputProxy/channels`         —— ② 不支持时的退路：已配置的 IP 通道（离线也保留）。
    ///
    /// 通道号换算：本地通道就是 ISAPI 里的 id；IP 通道是 `StartDigitalChannel + id - 1`
    /// （通常是 33 起，即 ISAPI 的 id=1 对应 SDK 通道 33）。
    /// 全程只读、不改设备任何配置。
    /// </summary>
    public ChannelScanResult DetectVideoChannels(int userId, LoginSession session)
    {
        var found = new HashSet<int>();
        var notes = new List<string>();
        bool anyOk = false;
        bool analogKnown = false;
        int ipStart = session.StartDigitalChannel > 0 ? session.StartDigitalChannel : 33;

        // ① 本地 / 模拟通道
        var (ok1, xml1, msg1) = IsapiGet(userId, "/ISAPI/System/Video/inputs/channels");
        if (ok1)
        {
            var ids = ParseIsapiIds(xml1, "VideoInputChannel", "videoInputEnabled");
            if (ids.Count > 0)
            {
                anyOk = true;
                analogKnown = true;
                foreach (var id in ids) found.Add(id);
            }
            else notes.Add("本地通道列表为空");
        }
        else notes.Add($"本地通道查询失败（{msg1}）");

        // ② 网络摄像机在线状态
        var (ok2, xml2, msg2) = IsapiGet(userId, "/ISAPI/ContentMgmt/InputProxy/channels/status");
        bool ipKnown = false;
        if (ok2)
        {
            var ids = ParseIsapiIds(xml2, "InputProxyChannelStatus", "online");
            if (ids.Count > 0)
            {
                anyOk = true;
                ipKnown = true;
                foreach (var id in ids) found.Add(ipStart + id - 1);
            }
        }

        // ③ ② 不可用 / 没给出内容时，退一步用"已配置的 IP 通道"（离线摄像机也保留，避免误藏）
        if (!ipKnown)
        {
            var (ok3, xml3, msg3) = IsapiGet(userId, "/ISAPI/ContentMgmt/InputProxy/channels");
            if (ok3)
            {
                var ids = ParseIsapiIds(xml3, "InputProxyChannel", null);
                if (ids.Count > 0)
                {
                    anyOk = true;
                    foreach (var id in ids) found.Add(ipStart + id - 1);
                }
                else notes.Add("未配置网络摄像机");
            }
            else
            {
                notes.Add($"网络通道查询失败（{msg2}／{msg3}）");
            }
        }

        if (!anyOk)
            return new ChannelScanResult(false, string.Join("；", notes), [], false);

        return new ChannelScanResult(true, string.Join("；", notes), found, analogKnown);
    }

    /// <summary>
    /// 从 ISAPI 应答里取通道号。
    /// elementName = 通道节点名（VideoInputChannel / InputProxyChannelStatus …）；
    /// flagName = 该节点下必须为 true 的布尔子节点（videoInputEnabled / online），
    /// 传 null 表示"列出即算数"。
    /// 用 LocalName 匹配、忽略命名空间：不同固件的 xmlns 不一致，按全名匹配会静默取不到。
    /// </summary>
    private static List<int> ParseIsapiIds(string xml, string elementName, string? flagName)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(xml)) return list;

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var node in doc.Descendants().Where(e =>
                         e.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase)))
            {
                var idNode = node.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase));
                if (idNode == null || !int.TryParse(idNode.Value.Trim(), out var id) || id <= 0) continue;

                if (flagName != null)
                {
                    var flag = node.Elements().FirstOrDefault(e =>
                        e.Name.LocalName.Equals(flagName, StringComparison.OrdinalIgnoreCase));
                    // 节点缺失时不当作 false（有的固件干脆不返这个字段）
                    if (flag != null && bool.TryParse(flag.Value.Trim(), out var on) && !on) continue;
                }
                list.Add(id);
            }
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"解析通道列表失败：{ex.Message}", "NetSDK");
        }
        return list;
    }

    /// <summary>
    /// 同步通道 OSD 名称（登录后自动调用一次）。
    ///
    /// 来源（都是只读 ISAPI，不改设备配置）：
    ///   ① /ISAPI/ContentMgmt/InputProxy/channels —— 网络摄像机的名称（NVR 上配的，即画面 OSD 名）；
    ///   ② /ISAPI/System/Video/inputs/channels    —— 本地（模拟）通道的名称。
    /// 返回 SDK 通道号 → 名称。拉不到就返回空表，显示层回退成「通道 N」编号。
    /// </summary>
    public Dictionary<int, string> FetchChannelNames(int userId, LoginSession session)
    {
        var names = new Dictionary<int, string>();
        int ipStart = session.StartDigitalChannel > 0 ? session.StartDigitalChannel : 33;

        try
        {
            var (_, xmlIp, _) = IsapiGet(userId, "/ISAPI/ContentMgmt/InputProxy/channels", 4000);
            foreach (var (id, name) in ParseIsapiIdName(xmlIp, "InputProxyChannel"))
                names[ipStart + id - 1] = name;

            var (_, xmlLocal, _) = IsapiGet(userId, "/ISAPI/System/Video/inputs/channels", 4000);
            foreach (var (id, name) in ParseIsapiIdName(xmlLocal, "VideoInputChannel"))
                names[id] = name;
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"同步 OSD 名称失败：{ex.Message}", "NetSDK");
        }

        return names;
    }

    /// <summary>
    /// 从 ISAPI 应答里取 (id, name) 对。节点缺 name 子节点时跳过；
    /// 用 LocalName 匹配忽略命名空间（不同固件 xmlns 不一致，见 ParseIsapiIds 注释）。
    /// </summary>
    private static List<(int id, string name)> ParseIsapiIdName(string xml, string elementName)
    {
        var list = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(xml)) return list;

        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var node in doc.Descendants().Where(e =>
                         e.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase)))
            {
                var idNode = node.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase));
                if (idNode == null || !int.TryParse(idNode.Value.Trim(), out var id) || id <= 0) continue;

                var nameNode = node.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase));
                var name = nameNode?.Value.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                list.Add((id, name));
            }
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"解析通道名称失败：{ex.Message}", "NetSDK");
        }
        return list;
    }

    /// <summary>TCP 端口连通性检查（验收第一项，能快速区分"网络不通"和"密码错误"）。</summary>
    public static (bool ok, string message, long elapsedMs) CheckTcp(string host, int port, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(Math.Max(200, timeoutMs)))
            {
                sw.Stop();
                return (false, $"连接超时（>{timeoutMs}ms）", sw.ElapsedMilliseconds);
            }
            if (task.IsFaulted)
            {
                sw.Stop();
                var inner = task.Exception?.InnerException;
                return (false, inner?.Message ?? "连接失败", sw.ElapsedMilliseconds);
            }
            sw.Stop();
            return (client.Connected, client.Connected ? "端口可达" : "未建立连接", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, ex.InnerException?.Message ?? ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>ICMP Ping（设备可能禁 ping，失败不作为验收结论，仅作参考信息）。</summary>
    public static (bool ok, string message, long rtt) Ping(string host, int timeoutMs = 1500)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = ping.Send(host, timeoutMs);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? (true, $"往返 {reply.RoundtripTime}ms", reply.RoundtripTime)
                : (false, $"状态：{reply.Status}", 0);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_initialized)
            {
                try { HCNetSdkNative.NET_DVR_Cleanup(); }
                catch (Exception ex) { LogService.Current.Warn($"释放 SDK 失败：{ex.Message}", "NetSDK"); }
                _initialized = false;
            }
            _disposed = true;
        }
    }
}

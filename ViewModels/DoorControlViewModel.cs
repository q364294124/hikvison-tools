using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Xml.Linq;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>门禁设备上的一扇门。</summary>
public sealed class DoorItem : ObservableObject
{
    private string _statusText = "未知";
    private bool _isBusy;
    private string _activeMode = string.Empty;

    public int Number { get; init; }

    /// <summary>设备上报的门名称（capabilities 里的 doorName），没有就用「门 N」。</summary>
    public string Name { get; init; } = string.Empty;

    public string Label => string.IsNullOrWhiteSpace(Name) ? $"门 {Number}" : Name;

    /// <summary>门状态（doorState）：已关闭 / 开启 / 异常 / 未知。</summary>
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>
    /// 当前生效的模式（决定按钮高亮跟实际情况走）：
    /// open / close / alwaysOpen / alwaysClose；空 = 正常状态无高亮。
    /// 来源有二：设备支持状态查询时按 doorState 映射；
    /// 不支持时（人脸终端常态）按最后一次成功的控制命令记。
    /// </summary>
    public string ActiveMode { get => _activeMode; set => Set(ref _activeMode, value); }

    /// <summary>这条门正在执行命令（按钮防抖）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsBusy;
}

/// <summary>一条门禁通行（刷卡/人脸/指纹）记录。</summary>
public sealed class AcsEventRecord
{
    public string Time { get; init; } = string.Empty;
    public string DoorNo { get; init; } = string.Empty;
    public string EmployeeNo { get; init; } = string.Empty;
    public string PersonName { get; init; } = string.Empty;
    public string CardNo { get; init; } = string.Empty;
    public string VerifyKind { get; init; } = string.Empty;
    public string SerialNo { get; init; } = string.Empty;
}

/// <summary>
/// 门禁控制页。
///
/// 现场场景：门禁设备（DS-K 系列）装完之后，甲方验收时要当场验证
/// "刷卡能不能开门、远程能不能开门、记录能不能查到"。
/// 没有这一页之前只能靠 4200 客户端或手机 App，现场机器不一定装。
///
/// 实现要点：
///   1. 全部走 ISAPI 透传（NET_DVR_STDXMLConfig），不转录门禁巨型结构体：
///        门列表   GET  /ISAPI/AccessControl/capabilities        （DoorCap）
///        门状态   GET  /ISAPI/AccessControl/Door/{n}/status     （人脸终端不支持，标"不支持"）
///        远程控制 PUT  /ISAPI/AccessControl/RemoteControl/door/{n}
///                     请求体根节点必须是 RemoteControlDoor（写成 RemoteControl 会被拒 Invalid Content）
///        通行记录 POST /ISAPI/AccessControl/AcsEvent
///                     人脸终端只认 JSON（?format=json + major/minor 必填），
///                     老控制器走 XML（AcsEventCond）——JSON 先试、XML 兜底
///      注意透传 URL 要带 "PUT " / "POST " 方法前缀（SDK 约定，见 NetSdkService.IsapiPut）；
///   2. 验证方式 / 门状态这类枚举值做宽松映射：认识的翻译成中文，
///      不认识的原样显示——门禁固件版本太多，别假设字段一定在；
///   3. 与预览页不同：本页没有窗口句柄依赖，切页不强制断开，
///      会话一直保持到换设备 / 手动断开 / 程序退出；
///   4. 每次开门动作都写运行日志（谁、哪台设备、哪扇门、什么时间）——
///      远程开门是安防敏感操作，现场必须可追溯。
/// </summary>
public sealed class DoorControlViewModel : PageViewModel
{
    private const string Source = "门禁";

    /// <summary>串行化会话级操作（连接/断开/换设备），避免并发登录登出。</summary>
    private readonly object _gate = new();

    private LoginSession? _session;

    private DiscoveredDevice? _selectedDevice;
    private string _userName = "admin";
    private bool _isConnected;
    private bool _isConnecting;
    private bool _isQueryingEvents;
    private bool _autoConnectAttempted;
    private string _statusText = "未连接 · 选择门禁设备后自动免密连接";
    private string _eventSummary = "尚未查询";

    public DoorControlViewModel()
    {
        Candidates = [];
        Doors = [];
        Events = [];

        if (!string.IsNullOrWhiteSpace(App.Settings.DefaultUserName)) _userName = App.Settings.DefaultUserName;

        RefreshCommand = new RelayCommand(RefreshCandidates);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_isConnected && !_isConnecting);
        DisconnectCommand = new RelayCommand(Disconnect, () => _isConnected || _isConnecting);
        RefreshStatusCommand = new RelayCommand(RefreshAllDoorStatus, () => _isConnected);
        QueryEventsCommand = new AsyncRelayCommand(QueryEventsAsync, () => _isConnected && !_isQueryingEvents);

        // 五个控制命令共用一个入口，命令字按 ISAPI 规范传给设备
        OpenDoorCommand = new RelayCommand(p => ControlDoor(p, "open"), _ => _isConnected);
        CloseDoorCommand = new RelayCommand(p => ControlDoor(p, "close"), _ => _isConnected);
        AlwaysOpenCommand = new RelayCommand(p => ControlDoor(p, "alwaysOpen"), _ => _isConnected);
        AlwaysCloseCommand = new RelayCommand(p => ControlDoor(p, "alwaysClose"), _ => _isConnected);
        ResetDoorCommand = new RelayCommand(p => ControlDoor(p, "reset"), _ => _isConnected);
    }

    public override string Title => "门禁控制";

    public override string Glyph => "IconDoor";

    public override string Description => "门禁设备远程开门/常开常闭控制与刷卡通行记录查询";

    public override string Usage => "门禁设备（DS-K 系列）装完后当场验证用：选中设备自动免密连接（凭据来自入库时验证保存的口令）→ 逐门测试开门/常开/常闭 → 查最近通行记录。按钮高亮跟随门的实际状态（常开/常闭/开启）。每次远程开门都会写入运行日志备查。部分老固件不支持门状态查询，高亮按最后一次控制命令显示。";

    // ==================== 目标与凭据 ====================

    /// <summary>可连接的设备：已发现的 + 台账里存的（跨网段设备靠台账）。</summary>
    public ObservableCollection<DiscoveredDevice> Candidates { get; }

    public ObservableCollection<DoorItem> Doors { get; }

    public ObservableCollection<AcsEventRecord> Events { get; }

    public DiscoveredDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value))
            {
                // 换目标设备必须断开当前会话：旧设备的 userId 对新设备无意义
                if (_isConnected || _session != null) Disconnect();
                _autoConnectAttempted = false;
                OnPropertyChanged(nameof(DeviceSummary));
                // 换台存过口令的设备直接免密连上，不让现场再点一次
                _ = TryAutoConnectAsync();
            }
        }
    }

    public string DeviceSummary => _selectedDevice == null
        ? "未选择设备"
        : $"{_selectedDevice.IPv4Address}:{_selectedDevice.SdkPort} · {_selectedDevice.ModelText}";

    // ==================== 状态 ====================

    public bool IsConnected { get => _isConnected; private set { if (Set(ref _isConnected, value)) RaiseStates(); } }

    public bool IsConnecting { get => _isConnecting; private set { if (Set(ref _isConnecting, value)) RaiseStates(); } }

    public bool IsQueryingEvents { get => _isQueryingEvents; private set { if (Set(ref _isQueryingEvents, value)) RaiseStates(); } }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string EventSummary { get => _eventSummary; private set => Set(ref _eventSummary, value); }

    public RelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand RefreshStatusCommand { get; }

    public AsyncRelayCommand QueryEventsCommand { get; }

    public RelayCommand OpenDoorCommand { get; }

    public RelayCommand CloseDoorCommand { get; }

    public RelayCommand AlwaysOpenCommand { get; }

    public RelayCommand AlwaysCloseCommand { get; }

    public RelayCommand ResetDoorCommand { get; }

    private void RaiseStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        RefreshStatusCommand.RaiseCanExecuteChanged();
        QueryEventsCommand.RaiseCanExecuteChanged();
        OpenDoorCommand.RaiseCanExecuteChanged();
        CloseDoorCommand.RaiseCanExecuteChanged();
        AlwaysOpenCommand.RaiseCanExecuteChanged();
        AlwaysCloseCommand.RaiseCanExecuteChanged();
        ResetDoorCommand.RaiseCanExecuteChanged();
    }

    // ==================== 生命周期 ====================

    public override void OnEnter()
    {
        RefreshCandidates();
        _ = TryAutoConnectAsync();
    }

    public override void OnDevicesInjected(IReadOnlyList<DiscoveredDevice> devices)
    {
        RefreshCandidates();
        // 列表里只剩门禁设备了（非门禁已被过滤），优先选第一台
        var match = devices.FirstOrDefault(d => d.IsAccessControl && !string.IsNullOrWhiteSpace(d.IPv4Address))
                    ?? devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.IPv4Address));
        if (match != null && SelectedDevice == null) SelectedDevice = match;
    }

    /// <summary>
    /// 免密自动连接：台账里存过口令的门禁设备（入库时验证过的）选中即连，
    /// 现场不再二次输密码。没存口令的设备给出去处提示，不再摆一个登录框。
    /// 自检（--livecheck）模式跳过：不允许产生真实网络副作用。
    /// </summary>
    private async Task TryAutoConnectAsync()
    {
        if (PreviewViewModel.HeadlessMode) return;
        if (_isConnected || _isConnecting || _autoConnectAttempted) return;
        var device = _selectedDevice;
        if (device == null || GetStoredCredential(device) == null) return;

        _autoConnectAttempted = true;
        await ConnectAsync().ConfigureAwait(true);
    }

    private void RefreshCandidates()
    {
        var keep = _selectedDevice?.Key;

        Candidates.Clear();

        // 只列门禁设备：发现的按 DS-K 型号识别；台账里的按型号/序列号前缀判断。
        // 摄像机、NVR 混在列表里只会误导现场——选上去控制命令必然被拒。
        foreach (var d in App.Devices.Where(d => d.IsAccessControl))
            Candidates.Add(d);

        foreach (var a in App.Assets)
        {
            if (string.IsNullOrWhiteSpace(a.Ip)) continue;
            if (!IsAccessControlAsset(a)) continue;
            if (Candidates.Any(c => string.Equals(c.IPv4Address, a.Ip, StringComparison.OrdinalIgnoreCase))) continue;
            Candidates.Add(new DiscoveredDevice
            {
                UniformDevID = a.SerialNo,
                SerialNo = a.SerialNo,
                Mac = a.Mac,
                IPv4Address = a.Ip,
                SdkPort = a.Port == 0 ? (ushort)8000 : a.Port,
                DeviceDesc = a.Model,
                DeviceDescEx = a.Model,
                Manufacturer = a.Manufacturer,
                SoftwareVersion = a.FirmwareVersion,
                Activate = ActivateState.Activated,
            });
        }

        if (keep != null)
        {
            var again = Candidates.FirstOrDefault(c => string.Equals(c.Key, keep, StringComparison.OrdinalIgnoreCase));
            if (again != null) _selectedDevice = again;
        }

        _selectedDevice ??= Candidates.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(DeviceSummary));

        if (Candidates.Count == 0)
            StatusText = "暂无门禁设备：门禁主机（DS-K 系列）验证入库后会自动出现在这里";
    }

    /// <summary>台账记录是否门禁设备：型号或序列号 DS-K 开头（海康门禁/人脸终端的固定前缀）。</summary>
    private static bool IsAccessControlAsset(AssetRecord a)
        => a.Model.StartsWith("DS-K", StringComparison.OrdinalIgnoreCase)
           || a.SerialNo.StartsWith("DS-K", StringComparison.OrdinalIgnoreCase)
           || a.Model.Contains("门禁", StringComparison.Ordinal)
           || a.Model.Contains("人脸", StringComparison.Ordinal);

    // ==================== 连接 / 断开 ====================

    /// <summary>
    /// 找台账里存过的登录凭据：序列号优先、IP 兜底（与预览页同一口径）。
    /// 返回 null = 台账没这台或没存过口令。
    /// </summary>
    private (string user, string password)? GetStoredCredential(DiscoveredDevice device)
    {
        AssetRecord? asset = null;
        if (!string.IsNullOrWhiteSpace(device.SerialNo))
            asset = App.Assets.FirstOrDefault(a =>
                string.Equals(a.SerialNo, device.SerialNo, StringComparison.OrdinalIgnoreCase));
        asset ??= App.Assets.FirstOrDefault(a =>
            string.Equals(a.Ip, device.IPv4Address, StringComparison.OrdinalIgnoreCase));
        if (asset == null || string.IsNullOrWhiteSpace(asset.PasswordEnc)) return null;

        var password = asset.Password;   // DPAPI 解密；换机器/换账号解不开时是空串
        if (string.IsNullOrEmpty(password)) return null;
        var user = !string.IsNullOrWhiteSpace(asset.AdminUserName) ? asset.AdminUserName : _userName;
        return (user, password);
    }

    private async Task ConnectAsync()
    {
        var device = _selectedDevice;
        if (device == null || string.IsNullOrWhiteSpace(device.IPv4Address)) { Toast("请先选择门禁设备", true); return; }

        // 凭据只从台账取（入库时验证过并加密保存的）——本页不再摆登录框：
        // 门禁是安防敏感设备，明文口令框反复出现既烦人又增加泄露面。
        var cred = GetStoredCredential(device);
        if (cred == null)
        {
            StatusText = $"{device.IPv4Address} 未存登录凭据：请到「设备发现」页验证并入库（或在台账手工添加时勾选验证），之后本页自动免密连接";
            Toast("该设备没有存过登录凭据，请先在「设备发现」页验证并入库", true);
            return;
        }
        var (user, password) = cred.Value;

        IsConnecting = true;
        StatusText = $"正在免密连接 {device.IPv4Address}:{device.SdkPort} …";

        var result = await Task.Run<(bool ok, string message, List<DoorItem> doors)>(() =>
        {
            lock (_gate)
            {
                LogoutSession();

                var session = App.NetSdk.Login(device.IPv4Address, device.SdkPort, user, password, device.HttpPort);
                if (!session.Ok) return (false, $"登录失败：{session.Message}", []);

                _session = session;

                var (doors, msg) = ReadDoors(session.UserId);
                return (true, msg, doors);
            }
        }).ConfigureAwait(true);

        var (ok, message, doors) = result;

        if (!ok)
        {
            _session = null;
            IsConnecting = false;
            StatusText = message;
            Log.Warn($"{device.IpMaskText} {message}", Source);
            Toast(message, true);
            return;
        }

        Doors.Clear();
        foreach (var d in doors) Doors.Add(d);

        IsConnecting = false;
        IsConnected = true;
        StatusText = $"已连接 {device.IPv4Address} · {device.ModelText} · {doors.Count} 扇门";
        Log.Info($"连接门禁设备 {device.IPv4Address} {device.ModelText}，识别到 {doors.Count} 扇门", Source);
        if (!device.IsAccessControl)
            Toast("提示：该设备型号不是 DS-K 开头，可能不是门禁设备，控制命令可能不被支持");

        // 连上就刷新一次门状态，顺手把最近记录也带出来
        RefreshAllDoorStatus();
        await QueryEventsAsync();
    }

    private void Disconnect()
    {
        var had = _session != null;

        Task.Run(() =>
        {
            lock (_gate) LogoutSession();
        });

        Doors.Clear();
        Events.Clear();
        EventSummary = "尚未查询";
        IsConnected = false;
        IsConnecting = false;
        StatusText = had ? "已断开" : "未连接 · 选择门禁设备后自动免密连接";
    }

    private void LogoutSession()
    {
        var session = _session;
        _session = null;
        if (session is { Ok: true }) App.NetSdk.Logout(session.UserId);
    }

    // ==================== 门列表 / 状态 ====================

    /// <summary>
    /// 读门列表：GET /ISAPI/AccessControl/capabilities，应答里的 DoorCap 每节一扇门。
    /// 解析不到门信息时按单门设备兜底（门 1）——人脸门禁终端基本都是单门。
    /// </summary>
    private (List<DoorItem> doors, string message) ReadDoors(int userId)
    {
        var doors = new List<DoorItem>();
        try
        {
            var (ok, xml, msg) = App.NetSdk.IsapiGet(userId, "/ISAPI/AccessControl/capabilities");
            if (ok && !string.IsNullOrWhiteSpace(xml))
            {
                var doc = XDocument.Parse(xml);
                foreach (var cap in doc.Descendants().Where(e => e.Name.LocalName == "DoorCap"))
                {
                    string noText = ChildValue(cap, "doorNo") ?? string.Empty;
                    if (!int.TryParse(noText, out int no) || no <= 0) continue;
                    doors.Add(new DoorItem { Number = no, Name = ChildValue(cap, "doorName") ?? string.Empty });
                }
            }
            else if (!ok)
            {
                Log.Warn($"读取门禁能力集失败：{msg}（按单门设备处理）", Source);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析门禁能力集异常：{ex.Message}（按单门设备处理）", Source);
        }

        if (doors.Count == 0)
            doors.Add(new DoorItem { Number = 1, Name = string.Empty });

        // 门数量做上限保护，异常应答不至于把界面撑爆
        if (doors.Count > 16) doors = doors.Take(16).ToList();
        return (doors, "连接成功");
    }

    private void RefreshAllDoorStatus()
    {
        var session = _session;
        if (session is not { Ok: true }) return;
        int userId = session.UserId;
        var doors = Doors.ToList();

        Task.Run(() =>
        {
            foreach (var door in doors)
            {
                var state = ReadDoorStatus(userId, door.Number);
                UiInvoke(() =>
                {
                    door.StatusText = state;
                    // 设备报得出状态时，高亮跟着真实门态走（覆盖上次命令的遗留）
                    door.ActiveMode = ModeFromStatus(state, fallback: door.ActiveMode);
                });
            }
        });
    }

    /// <summary>门状态文字 → 按钮高亮模式。报不出状态（未知/异常/不支持）时保持原值不动。</summary>
    private static string ModeFromStatus(string status, string fallback) => status switch
    {
        "开启" => "open",
        "已关闭" => "close",
        _ => fallback,
    };

    /// <summary>
    /// 门状态：GET /ISAPI/AccessControl/Door/{n}/status。
    /// 人脸门禁终端不支持这个查询（设备报 Invalid Operation，实测
    /// DS-K1T671BM V3.3.9），标"不支持"——不是故障，别让现场误会。
    /// </summary>
    private static string ReadDoorStatus(int userId, int doorNo)
    {
        try
        {
            var (ok, xml, msg) = App.NetSdk.IsapiGet(userId, $"/ISAPI/AccessControl/Door/{doorNo}/status");
            if (!ok)
                return msg.Contains("Invalid Operation", StringComparison.OrdinalIgnoreCase) ? "不支持" : "未知";
            if (string.IsNullOrWhiteSpace(xml)) return "未知";
            var doc = XDocument.Parse(xml);
            var raw = DescendantValue(doc, "doorState") ?? "未知";
            return raw switch
            {
                "open" => "开启",
                "closed" => "已关闭",
                "abnormal" => "异常",
                _ => raw,
            };
        }
        catch
        {
            return "未知";
        }
    }

    // ==================== 远程控制 ====================

    /// <summary>
    /// 远程控制一扇门：PUT /ISAPI/AccessControl/RemoteControl/door/{n}。
    /// cmd 取值（ISAPI 规范）：open 开门 / close 关门 / alwaysOpen 常开 /
    /// alwaysClose 常闭 / reset 恢复（取消常开常闭，回到正常模式）。
    /// </summary>
    private void ControlDoor(object? parameter, string cmd)
    {
        if (parameter is not DoorItem door) return;
        var session = _session;
        if (session is not { Ok: true }) return;
        var device = _selectedDevice;

        string actionText = cmd switch
        {
            "open" => "开门",
            "close" => "关门",
            "alwaysOpen" => "常开",
            "alwaysClose" => "常闭",
            "reset" => "恢复正常",
            _ => cmd,
        };

        int userId = session.UserId;
        int doorNo = door.Number;
        door.IsBusy = true;

        Task.Run(() =>
        {
            // 根节点必须是 RemoteControlDoor（ISAPI 规范）。历史教训：
            // 旧写法 <RemoteControl> 会被设备拒"Invalid Content"（SDK 11）。
            string body = "<RemoteControlDoor version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\">" +
                          $"<cmd>{cmd}</cmd></RemoteControlDoor>";
            var (ok, _, msg) = App.NetSdk.IsapiPut(userId, $"PUT /ISAPI/AccessControl/RemoteControl/door/{doorNo}", body);
            var state = ok ? ReadDoorStatus(userId, doorNo) : "未知";

            UiInvoke(() =>
            {
                door.IsBusy = false;
                door.StatusText = state;
                // 高亮跟实际情况走：设备报得出状态按状态；报不出（人脸终端常态）
                // 至少把常开/常闭这类持续性模式记下来，瞬时命令（开门/关门）不高亮
                door.ActiveMode = ModeFromStatus(state, fallback: cmd is "alwaysOpen" or "alwaysClose" ? cmd : "");
                if (ok)
                {
                    StatusText = $"{actionText}成功 · {device?.IPv4Address} · {door.Label}";
                    Toast($"{door.Label} {actionText}成功");
                    // 远程开门是敏感操作：操作者 / 设备 / 门 / 时间全记下来
                    Log.Info($"远程{actionText} · {device?.IPv4Address} {door.Label}（操作者 {_userName}）", Source);
                }
                else
                {
                    StatusText = $"{actionText}失败：{msg}";
                    Toast($"{door.Label} {actionText}失败：{msg}", true);
                    Log.Warn($"远程{actionText}失败 · {device?.IPv4Address} {door.Label}：{msg}", Source);
                }
            });
        });
    }

    // ==================== 通行记录 ====================

    private async Task QueryEventsAsync()
    {
        var session = _session;
        if (session is not { Ok: true }) return;
        var device = _selectedDevice;
        int userId = session.UserId;

        IsQueryingEvents = true;
        StatusText = $"正在查询 {device?.IPv4Address} 的通行记录 …";

        var result = await Task.Run<(bool ok, string message, List<AcsEventRecord> records, string summary)>(() =>
        {
            string searchId = Guid.NewGuid().ToString("N");

            // 人脸门禁终端（DS-K1T671 等，实测 V3.3.9）的事件查询只认 JSON，
            // 且 AcsEventCond 里必须带 major/minor（缺了报 MessageParametersLack: major），
            // 用 XML 请求体一律被拒 badJsonFormat。老式门禁控制器走 XML。
            // 两条路都留着：JSON 先试，被拒再退回 XML。
            string jsonBody = "{" +
                "\"AcsEventCond\":{" +
                $"\"searchID\":\"{searchId}\"," +
                "\"searchResultPosition\":0," +
                "\"maxResults\":30," +
                "\"major\":0,\"minor\":0}}";

            var (ok, xml, msg) = App.NetSdk.IsapiPut(userId, "POST /ISAPI/AccessControl/AcsEvent?format=json", jsonBody);

            List<AcsEventRecord> records;
            int total;
            if (ok)
            {
                (records, total) = ParseAcsEventsJson(xml);
            }
            else
            {
                string xmlBody =
                    "<AcsEventCond>" +
                    $"<searchID>{searchId}</searchID>" +
                    "<searchResultPosition>0</searchResultPosition>" +
                    "<maxResults>100</maxResults>" +
                    "</AcsEventCond>";

                (ok, xml, msg) = App.NetSdk.IsapiPut(userId, "POST /ISAPI/AccessControl/AcsEvent", xmlBody);
                if (!ok) return (false, $"查询失败：{msg}", [], "查询失败");
                (records, total) = ParseAcsEvents(xml);
            }

            string summary = records.Count == 0
                ? "设备没有返回通行记录（可能还没人刷过卡）"
                : $"返回最近 {records.Count} 条，设备端共 {total} 条";
            return (true, "查询成功", records, summary);
        }).ConfigureAwait(true);

        IsQueryingEvents = false;

        if (!result.ok)
        {
            StatusText = result.message;
            Toast(result.message, true);
            Log.Warn($"{device?.IpMaskText} {result.message}", Source);
            return;
        }

        Events.Clear();
        foreach (var r in result.records) Events.Add(r);
        EventSummary = result.summary;
        StatusText = $"已连接 {device?.IPv4Address} · {Doors.Count} 扇门 · 通行记录 {result.records.Count} 条";
        Log.Info($"查询通行记录 {device?.IPv4Address}：{result.summary}", Source);
    }

    /// <summary>
    /// 解析 AcsEvent 的 JSON 应答（人脸门禁终端路径）。
    /// 记录在 AcsEvent.InfoList 下；major=5 是人员通行/验证事件，
    /// 门磁心跳之类（major=3）没有人员信息，混进"最近通行记录"全是空行，过滤掉。
    /// </summary>
    private static (List<AcsEventRecord> records, int total) ParseAcsEventsJson(string json)
    {
        var records = new List<AcsEventRecord>();
        int total = 0;
        if (string.IsNullOrWhiteSpace(json)) return (records, total);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("AcsEvent", out var ev)) return (records, total);

            if (ev.TryGetProperty("totalMatches", out var tm) && tm.TryGetInt32(out var t)) total = t;
            if (!ev.TryGetProperty("InfoList", out var list) ||
                list.ValueKind != System.Text.Json.JsonValueKind.Array)
                return (records, total);

            foreach (var item in list.EnumerateArray())
            {
                string S(string name) =>
                    item.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString() ?? string.Empty
                        : string.Empty;
                int I(string name) =>
                    item.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

                bool access = I("major") == 5
                              || S("employeeNoString").Length > 0
                              || S("name").Length > 0
                              || S("cardNo").Length > 0;
                if (!access) continue;

                records.Add(new AcsEventRecord
                {
                    Time = S("time"),
                    DoorNo = item.TryGetProperty("doorNo", out var d) ? d.ToString() : string.Empty,
                    EmployeeNo = S("employeeNoString"),
                    PersonName = S("name"),
                    CardNo = S("cardNo"),
                    VerifyKind = MinorText(I("minor")),
                    SerialNo = S("serialNo"),
                });
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            LogService.Current.Warn($"通行记录应答不是有效 JSON：{ex.Message}", Source);
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"解析通行记录 JSON 异常：{ex.Message}", Source);
        }

        return (records, total);
    }

    /// <summary>常见通行事件的 minor 事件码（ACS 事件码表，认不全就按"通行"显示）。</summary>
    private static string MinorText(int minor) => minor switch
    {
        10 => "刷卡",
        15 => "密码",
        22 => "指纹",
        75 => "人脸",
        _ => "通行",
    };

    /// <summary>
    /// 解析 AcsEvent 应答。记录在 MatchList/SearchResult 下，
    /// 字段做宽松匹配（eventTime/dateTime、employeeNoString/employeeNo…），
    /// 固件差异靠「认识哪个读哪个」兜住，缺的列显示空。
    /// </summary>
    private static (List<AcsEventRecord> records, int total) ParseAcsEvents(string xml)
    {
        var records = new List<AcsEventRecord>();
        int total = 0;
        if (string.IsNullOrWhiteSpace(xml)) return (records, total);

        try
        {
            var doc = XDocument.Parse(xml);
            if (int.TryParse(DescendantValue(doc, "totalMatches"), out var t)) total = t;

            var items = doc.Descendants().Where(e => e.Name.LocalName == "SearchResult");
            foreach (var item in items)
            {
                records.Add(new AcsEventRecord
                {
                    Time = ChildValue(item, "eventTime") ?? ChildValue(item, "dateTime") ?? string.Empty,
                    DoorNo = ChildValue(item, "doorNumber") ?? string.Empty,
                    EmployeeNo = ChildValue(item, "employeeNoString") ?? ChildValue(item, "employeeNo") ?? string.Empty,
                    PersonName = ChildValue(item, "name") ?? string.Empty,
                    CardNo = ChildValue(item, "cardNo") ?? string.Empty,
                    VerifyKind = MapVerifyKind(ChildValue(item, "verifyKind")),
                    SerialNo = ChildValue(item, "serialNo") ?? string.Empty,
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"解析通行记录应答异常：{ex.Message}", Source);
        }

        return (records, total);
    }

    private static string MapVerifyKind(string? kind) => kind switch
    {
        null or "" => string.Empty,
        "card" => "刷卡",
        "face" => "人脸",
        "fingerprint" or "fingerPrint" => "指纹",
        "password" or "pw" => "密码",
        "cardOrFace" or "faceOrCard" => "卡或人脸",
        "qrCode" => "二维码",
        _ => kind,
    };

    // ==================== XML 辅助 ====================

    /// <summary>取子元素文本，忽略命名空间；找不到返回 null。</summary>
    private static string? ChildValue(XElement? parent, string name)
    {
        if (parent == null) return null;
        var node = parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        var value = node?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>全树找第一个同名元素的文本，忽略命名空间；找不到返回 null。</summary>
    private static string? DescendantValue(XDocument doc, string name)
        => doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() is { Length: > 0 } v ? v : null;

    // ==================== 线程辅助 ====================

    /// <summary>把动作投递到 UI 线程（已在 UI 线程时直接执行）。</summary>
    private static void UiInvoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    private static LogService Log => App.Log;
}

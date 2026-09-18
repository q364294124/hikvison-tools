using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Xml.Linq;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>
/// 项目验收页。
///
/// 交付验收要的不是"能连上"，而是一份"可追溯的证据"：
/// 网络通不通、账号密码对不对、型号序列号是否与合同一致、
/// 时间有没有偏差、画面有没有出图。
/// 本页把这六件事跑成流水线，每台设备留一条记录 + 抓图存证，
/// 最后生成一份带截图的单文件 HTML 报告。
///
/// 实现说明：本页复用 <see cref="DeviceTaskPageViewModel"/> 的并发执行框架，
/// 但每台设备的结论比"成功/失败"复杂得多，所以在 Execute 里把详细结论
/// 塞进字典，再在 OnDeviceProcessed 里取出来投递到 UI 线程。
/// </summary>
public sealed class AcceptanceViewModel : DeviceTaskPageViewModel
{
    private readonly ConcurrentDictionary<string, AcceptanceRecord> _detail = new(StringComparer.OrdinalIgnoreCase);

    private string _userName = "admin";
    private string _password = string.Empty;
    private bool _showPassword;

    // 验收项开关
    private bool _checkPort = true;
    private bool _checkLogin = true;
    private bool _checkDeviceInfo = true;
    private bool _checkSerialMatch = true;
    private bool _checkTime = true;
    private bool _checkSnapshot = true;

    private int _timeToleranceSeconds = 60;
    private int _captureChannels = 1;
    private string _lastReportPath = string.Empty;

    public AcceptanceViewModel()
    {
        Records = [];

        if (!string.IsNullOrWhiteSpace(App.Settings.DefaultUserName)) _userName = App.Settings.DefaultUserName;
        _captureChannels = Math.Clamp(App.Settings.CaptureMaxChannels, 1, 8);

        SelectAllChecksCommand = new RelayCommand(() =>
        {
            CheckPort = CheckLogin = CheckDeviceInfo = CheckTime = CheckSnapshot = true;
            CheckSerialMatch = true;
        });

        GenerateReportCommand = new RelayCommand(GenerateReport, () => Records.Count > 0);
        OpenReportCommand = new RelayCommand(OpenReportFolder);
        OpenSnapshotCommand = new RelayCommand(OpenSnapshotFolder);
        ExportReportCsvCommand = new RelayCommand(ExportReportCsv, () => Records.Count > 0);
        LoadFromAssetsCommand = new RelayCommand(LoadFromAssets);
    }

    public ObservableCollection<AcceptanceRecord> Records { get; }

    public RelayCommand SelectAllChecksCommand { get; }

    public RelayCommand GenerateReportCommand { get; }

    public RelayCommand OpenReportCommand { get; }

    public RelayCommand OpenSnapshotCommand { get; }

    public RelayCommand ExportReportCsvCommand { get; }

    /// <summary>从资产台账载入目标（设备不在同网段、广播搜不到时特别有用）。</summary>
    public RelayCommand LoadFromAssetsCommand { get; }

    private RelayCommand? _toggleShowPasswordCommand;
    /// <summary>切换登录口令的明文 / 密文显示。</summary>
    public RelayCommand ToggleShowPasswordCommand => _toggleShowPasswordCommand ??= new(() => ShowPassword = !ShowPassword);

    public override string Title => "项目验收";

    public override string Glyph => "IconCheck";

    public override string Description => "逐台核验网络、账号、型号序列号、时间与出图，生成带抓图的验收报告";

    public override string Usage => "建议在全部部署动作完成后执行。报告是单文件 HTML，可直接发甲方，浏览器 Ctrl+P 即出 PDF。";

    protected override string BusyTextPrefix => "设备验收";

    protected override string LogSource => "验收";

    // ==================== 参数 ====================

    public string UserName { get => _userName; set => Set(ref _userName, value); }

    public string Password { get => _password; set => Set(ref _password, value); }

    public bool ShowPassword { get => _showPassword; set => Set(ref _showPassword, value); }

    public bool CheckPort { get => _checkPort; set => Set(ref _checkPort, value); }

    public bool CheckLogin { get => _checkLogin; set => Set(ref _checkLogin, value); }

    public bool CheckDeviceInfo { get => _checkDeviceInfo; set => Set(ref _checkDeviceInfo, value); }

    public bool CheckSerialMatch { get => _checkSerialMatch; set => Set(ref _checkSerialMatch, value); }

    public bool CheckTime { get => _checkTime; set => Set(ref _checkTime, value); }

    public bool CheckSnapshot { get => _checkSnapshot; set => Set(ref _checkSnapshot, value); }

    /// <summary>设备时间允许的最大偏差（秒），超出则判不通过。</summary>
    public int TimeToleranceSeconds
    {
        get => _timeToleranceSeconds;
        set => Set(ref _timeToleranceSeconds, Math.Clamp(value, 5, 3600));
    }

    /// <summary>每台设备抓图的通道数（存证用，1 路通常够）。</summary>
    public int CaptureChannels
    {
        get => _captureChannels;
        set => Set(ref _captureChannels, Math.Clamp(value, 0, 8));
    }

    public string LastReportPath
    {
        get => _lastReportPath;
        private set
        {
            if (Set(ref _lastReportPath, value)) OnPropertyChanged(nameof(HasReport));
        }
    }

    public bool HasReport => !string.IsNullOrEmpty(LastReportPath) && File.Exists(LastReportPath);

    // ==================== 统计 ====================

    public int PassedCount => Records.Count(r => r.ResultText == "通过");

    public int FailedCount => Records.Count(r => r.ResultText == "失败");

    public int PartialCount => Records.Count(r => r.ResultText == "部分未通过");

    public string AcceptanceSummary => Records.Count == 0
        ? "尚未执行验收"
        : $"验收 {Records.Count} 台：通过 {PassedCount} · 部分未通过 {PartialCount} · 失败 {FailedCount}";

    protected override void OnTargetsChanged()
    {
        base.OnTargetsChanged();
        if (Targets.Count > 0 && string.IsNullOrWhiteSpace(_password))
            Toast("提示：请先填写设备登录口令，验收需要真实登录设备");
    }

    private void LoadFromAssets()
    {
        if (App.Assets.Count == 0) { Toast("资产台账为空", true); return; }

        Targets.Clear();
        foreach (var a in App.Assets)
        {
            if (string.IsNullOrWhiteSpace(a.Ip)) continue;
            // 台账里可能存着当前没有被广播搜到的设备（跨网段/已改 IP），
            // 这里合成为一份"可登录目标"，让验收不依赖广播
            Targets.Add(new DiscoveredDevice
            {
                UniformDevID = a.SerialNo,
                SerialNo = a.SerialNo,
                Mac = a.Mac,
                IPv4Address = a.Ip,
                IPv4SubnetMask = a.SubnetMask,
                IPv4Gateway = a.Gateway,
                SdkPort = a.Port == 0 ? (ushort)8000 : a.Port,
                HttpPort = a.HttpPort,
                DeviceDesc = a.Model,
                DeviceDescEx = a.Model,
                Manufacturer = a.Manufacturer,
                SoftwareVersion = a.FirmwareVersion,
                Activate = ActivateState.Activated,
                IsSelected = true,
            });
        }
        OnTargetsChanged();
        Toast($"已从台账载入 {Targets.Count} 台设备");
    }

    // ==================== 校验 ====================

    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Password)) return "请输入设备登录口令，验收必须真实登录才能判定";

        if (!CheckPort && !CheckLogin && !CheckDeviceInfo && !CheckTime && !CheckSnapshot)
            return "至少勾选一项检查内容";

        if (CheckSnapshot && CaptureChannels <= 0)
            return "已勾选抓图存证，但抓图通道数为 0，请调整";

        return null;
    }

    protected override void OnBeforeRun(IReadOnlyList<DiscoveredDevice> targets)
    {
        _detail.Clear();
        Records.Clear();
        NotifyAcceptanceCounters();
        LastReportPath = string.Empty;
        Log.Info($"开始验收 {targets.Count} 台设备，账号 {UserName}", LogSource);
    }

    // ==================== 单台验收 ====================

    protected override (bool ok, string message) Execute(DiscoveredDevice device)
    {
        var sw = Stopwatch.StartNew();
        var record = new AcceptanceRecord
        {
            Target = string.IsNullOrWhiteSpace(device.SerialNo) ? device.UniformDevID : device.SerialNo,
            Ip = device.IPv4Address,
            Port = device.SdkPort,
            SerialNo = device.SerialNo,
            Model = device.ModelText,
            DeviceName = device.DeviceDescEx is { Length: > 0 } ? device.DeviceDescEx : device.DeviceDesc,
        };

        int userId = -1;
        try
        {
            // ---- 1. 端口连通 ----
            if (CheckPort)
            {
                var (ok, msg, ms) = NetSdkService.CheckTcp(device.IPv4Address, device.SdkPort, Math.Max(1000, App.Settings.ConnectTimeoutMs));
                record.Online = ok;
                record.Checks.Add(new AcceptanceCheck
                {
                    Name = $"端口连通（{device.SdkPort}）",
                    Passed = ok,
                    Detail = ok ? $"{msg}，耗时 {ms}ms" : msg,
                });
            }
            else
            {
                record.Online = true;
                record.Checks.Add(new AcceptanceCheck { Name = "端口连通", Skipped = true, Detail = "已关闭该项检查" });
            }

            if (!record.Online && CheckPort && CheckLogin)
            {
                // 端口不通就没必要再登录，直接给出结论，省得现场等超时
                record.Checks.Add(new AcceptanceCheck { Name = "登录认证", Skipped = true, Detail = "端口不通，跳过登录" });
                Finish(record, sw);
                _detail[device.Key] = record;
                return (false, "端口不可达，可能 IP 被改、设备未上电或不在同一网段");
            }

            // ---- 2. 登录认证 ----
            LoginSession? session = null;
            if (CheckLogin)
            {
                session = App.NetSdk.Login(device.IPv4Address, device.SdkPort, UserName, Password, device.HttpPort);
                userId = session.Ok ? session.UserId : -1;
                record.LoggedIn = session.Ok;
                record.Checks.Add(new AcceptanceCheck
                {
                    Name = "登录认证",
                    Passed = session.Ok,
                    Detail = session.Ok ? $"登录成功，用户等级 {session.PasswordLevel}" : session.Message,
                });
                if (!session.Ok) record.Error = session.Message;
            }
            else
            {
                record.Checks.Add(new AcceptanceCheck { Name = "登录认证", Skipped = true, Detail = "已关闭该项检查" });
            }

            if (session is { Ok: true })
            {
                record.ChannelCount = session.ChannelCount > 0 ? session.ChannelCount : CountChannels(session.Info);
                record.StartChannel = session.StartChannel;
                if (string.IsNullOrWhiteSpace(record.SerialNo)) record.SerialNo = session.SerialNo;
            }

            // ---- 3. 读取设备信息（ISAPI） ----
            if (CheckDeviceInfo && userId >= 0)
            {
                var (ok, xml, msg) = App.NetSdk.IsapiGet(userId, "/ISAPI/System/deviceInfo");
                if (ok)
                {
                    record.DeviceName = XmlValue(xml, "deviceName") ?? record.DeviceName;
                    record.Model = XmlValue(xml, "model") ?? record.Model;
                    record.FirmwareVersion = XmlValue(xml, "firmwareVersion") ?? string.Empty;
                    record.FirmwareDate = XmlValue(xml, "firmwareReleasedDate") ?? string.Empty;

                    var isapiSerial = XmlValue(xml, "serialNumber");
                    bool hasFields = !string.IsNullOrWhiteSpace(record.Model) || !string.IsNullOrWhiteSpace(record.FirmwareVersion);

                    record.Checks.Add(new AcceptanceCheck
                    {
                        Name = "读取设备信息",
                        Passed = hasFields,
                        Detail = hasFields
                            ? $"型号 {record.Model}，固件 {record.FirmwareVersion}（{record.FirmwareDate}）"
                            : "设备应答成功但未解析到型号/固件字段",
                    });

                    // ---- 4. 序列号一致性（防止装错货 / 串货） ----
                    if (CheckSerialMatch)
                    {
                        if (string.IsNullOrWhiteSpace(isapiSerial) || string.IsNullOrWhiteSpace(device.SerialNo))
                        {
                            record.Checks.Add(new AcceptanceCheck
                            {
                                Name = "序列号一致性",
                                Skipped = true,
                                Detail = "SADP 或 ISAPI 未返回序列号，无法比对",
                            });
                        }
                        else
                        {
                            bool match = isapiSerial.Trim().Equals(device.SerialNo.Trim(), StringComparison.OrdinalIgnoreCase);
                            record.Checks.Add(new AcceptanceCheck
                            {
                                Name = "序列号一致性",
                                Passed = match,
                                Detail = match
                                    ? $"一致：{isapiSerial}"
                                    : $"不一致！发现列表 {device.SerialNo}，设备自报 {isapiSerial}，请核对是否装错设备",
                            });
                        }
                    }
                }
                else
                {
                    record.Checks.Add(new AcceptanceCheck { Name = "读取设备信息", Passed = false, Detail = msg });
                }
            }
            else if (CheckDeviceInfo)
            {
                record.Checks.Add(new AcceptanceCheck { Name = "读取设备信息", Skipped = true, Detail = "未登录成功，跳过" });
            }

            // ---- 5. 设备时间 ----
            if (CheckTime && userId >= 0)
            {
                var (ok, xml, msg) = App.NetSdk.IsapiGet(userId, "/ISAPI/System/time");
                if (ok)
                {
                    var text = XmlValue(xml, "localTime") ?? XmlValue(xml, "time");
                    record.DeviceTime = text ?? string.Empty;
                    if (DateTimeOffset.TryParse(text, out var deviceTime))
                    {
                        double offset = Math.Abs((DateTimeOffset.Now - deviceTime).TotalSeconds);
                        record.DeviceTimeOffsetSeconds = offset;
                        bool withinTolerance = offset <= TimeToleranceSeconds;
                        record.Checks.Add(new AcceptanceCheck
                        {
                            Name = "设备时间",
                            Passed = withinTolerance,
                            Detail = withinTolerance
                                ? $"{deviceTime:yyyy-MM-dd HH:mm:ss}，与本机偏差 {offset:F0} 秒"
                                : $"偏差 {offset:F0} 秒，超出容差 {TimeToleranceSeconds} 秒（会影响录像检索）",
                        });
                    }
                    else
                    {
                        record.Checks.Add(new AcceptanceCheck
                        {
                            Name = "设备时间",
                            Skipped = true,
                            Detail = $"设备应答的时间格式无法解析：{text}",
                        });
                    }
                }
                else
                {
                    record.Checks.Add(new AcceptanceCheck { Name = "设备时间", Passed = false, Detail = msg });
                }
            }
            else if (CheckTime)
            {
                record.Checks.Add(new AcceptanceCheck { Name = "设备时间", Skipped = true, Detail = "未登录成功，跳过" });
            }

            // ---- 6. 抓图存证 ----
            if (CheckSnapshot && userId >= 0)
            {
                if (device.IsAccessControl)
                {
                    // 门禁设备没有视频通道，抓图必失败——那是误报不是缺陷，
                    // 直接标跳过，验收结论才不被误伤。门禁的画面验证走「门禁控制」页。
                    record.Checks.Add(new AcceptanceCheck
                    {
                        Name = "抓图存证",
                        Skipped = true,
                        Detail = "门禁设备（DS-K），无视频通道，跳过；开门验证见「门禁控制」页",
                    });
                }
                else
                {
                    int channels = CaptureChannels;
                    if (record.ChannelCount > 0) channels = Math.Min(channels, record.ChannelCount);

                if (channels <= 0)
                {
                    record.Checks.Add(new AcceptanceCheck { Name = "抓图存证", Skipped = true, Detail = "抓图通道数为 0" });
                }
                else
                {
                    var dir = Path.Combine(SnapshotRoot, SafeFolder(record.SerialNo is { Length: > 0 } ? record.SerialNo : record.Ip));
                    int success = 0;
                    int start = record.StartChannel > 0 ? record.StartChannel : 1;

                    for (int i = 0; i < channels; i++)
                    {
                        int channel = start + i;
                        var file = Path.Combine(dir, $"ch{channel}_{DateTime.Now:HHmmss}.jpg");
                        var (ok, msg, bytes) = App.NetSdk.CaptureJpeg(userId, channel, file);
                        if (ok)
                        {
                            success++;
                            record.Snapshots.Add(file);
                        }
                        else
                        {
                            Log.Warn($"{record.Ip} 通道 {channel} 抓图失败：{msg}", LogSource);
                        }
                    }

                    record.Checks.Add(new AcceptanceCheck
                    {
                        Name = "抓图存证",
                        Passed = success > 0,
                        Detail = success > 0
                            ? $"成功 {success} / {channels} 路，已保存到 output/snapshots"
                            : $"全部 {channels} 路抓图失败，请检查通道号或码流状态",
                    });
                }
                }
            }
            else if (CheckSnapshot)
            {
                record.Checks.Add(new AcceptanceCheck { Name = "抓图存证", Skipped = true, Detail = "未登录成功，跳过" });
            }
        }
        catch (Exception ex)
        {
            record.Error = ex.Message;
            record.Checks.Add(new AcceptanceCheck { Name = "执行异常", Passed = false, Detail = ex.Message });
            Log.Error($"验收 {device.IpMaskText} 时异常", ex, LogSource);
        }
        finally
        {
            if (userId >= 0) App.NetSdk.Logout(userId);
        }

        Finish(record, sw);
        _detail[device.Key] = record;

        int pass = record.Checks.Count(c => c.Passed);
        int fail = record.Checks.Count(c => !c.Passed && !c.Skipped);
        string message = !string.IsNullOrEmpty(record.Error)
            ? record.Error!
            : fail == 0 ? $"全部 {pass} 项通过" : $"{fail} 项未通过";

        return (record.ResultText == "通过", message);
    }

    private void Finish(AcceptanceRecord record, Stopwatch sw)
    {
        sw.Stop();
        record.ElapsedMs = sw.ElapsedMilliseconds;
    }

    protected override void OnDeviceProcessed(DiscoveredDevice device, OperationResult result)
    {
        if (!_detail.TryGetValue(device.Key, out var record)) return;
        UiInvoke(() =>
        {
            Records.Add(record);
            NotifyAcceptanceCounters();
            GenerateReportCommand.RaiseCanExecuteChanged();
            ExportReportCsvCommand.RaiseCanExecuteChanged();
        });
    }

    protected override void AfterRun()
    {
        UiInvoke(NotifyAcceptanceCounters);
        if (Records.Count > 0)
        {
            // 报告是验收的最终交付物，跑完直接生成，省得用户再点一次
            UiInvoke(GenerateReport);
        }
    }

    private void NotifyAcceptanceCounters()
    {
        OnPropertyChanged(nameof(PassedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(PartialCount));
        OnPropertyChanged(nameof(AcceptanceSummary));
    }

    // ==================== 报告 ====================

    private static string OutputRoot => string.IsNullOrWhiteSpace(App.Settings.OutputDir) ? AppPaths.OutputDir : App.Settings.OutputDir;

    private static string SnapshotRoot => Path.Combine(OutputRoot, "snapshots");

    private static string ReportRoot => Path.Combine(OutputRoot, "reports");

    private void GenerateReport()
    {
        if (Records.Count == 0) { Toast("还没有验收记录，无法生成报告", true); return; }
        try
        {
            var sdkNote = $"SADP {App.Sadp.VersionText} / NetSDK 已初始化 {(App.NetSdk.IsInitialized ? "是" : "否")}";
            var path = Exporter.ExportAcceptanceReport(
                Records, ReportRoot, App.Settings.ProjectName, App.Settings.Operator, sdkNote);
            LastReportPath = path;
            Toast($"验收报告已生成：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error("生成验收报告失败", ex, LogSource);
            Toast($"生成报告失败：{ex.Message}", true);
        }
    }

    private void ExportReportCsv()
    {
        if (Records.Count == 0) { Toast("还没有验收记录", true); return; }
        try
        {
            Directory.CreateDirectory(ReportRoot);
            var file = Path.Combine(ReportRoot, $"验收明细_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            CsvUtil.Write(file,
                ["目标", "IP", "序列号", "型号", "固件版本", "固件日期", "通道数", "设备时间", "时间偏差(秒)", "检查项", "结论", "错误", "耗时(ms)"],
                Records.Select(r => (IReadOnlyList<string>)new[]
                {
                    r.Target, r.Ip, r.SerialNo, r.Model, r.FirmwareVersion, r.FirmwareDate,
                    r.ChannelCount.ToString(), r.DeviceTime, r.DeviceTimeOffsetSeconds.ToString("F0"),
                    r.CheckSummary, r.ResultText, r.Error ?? string.Empty, r.ElapsedMs.ToString(),
                }).ToList());
            Toast($"验收明细已导出：{Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Toast($"导出失败：{ex.Message}", true);
        }
    }

    private void OpenReportFolder()
    {
        try
        {
            Directory.CreateDirectory(ReportRoot);
            EnvironmentCheck.OpenFolder(ReportRoot);
        }
        catch (Exception ex)
        {
            Toast($"打开目录失败：{ex.Message}", true);
        }

        if (HasReport)
            try { Process.Start(new ProcessStartInfo { FileName = LastReportPath, UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn($"打开报告失败：{ex.Message}", LogSource); }
    }

    private void OpenSnapshotFolder()
    {
        try
        {
            Directory.CreateDirectory(SnapshotRoot);
            EnvironmentCheck.OpenFolder(SnapshotRoot);
        }
        catch (Exception ex)
        {
            Toast($"打开目录失败：{ex.Message}", true);
        }
    }

    private RelayCommand? _openSingleReportCommand;
    public RelayCommand OpenSingleReportCommand => _openSingleReportCommand ??= new(() =>
    {
        if (!HasReport) { Toast("还没有生成报告", true); return; }
        try { Process.Start(new ProcessStartInfo { FileName = LastReportPath, UseShellExecute = true }); }
        catch (Exception ex) { Toast($"打开报告失败：{ex.Message}", true); }
    });

    // ==================== 辅助 ====================

    /// <summary>从 ISAPI 应答里取指定标签的文本，忽略命名空间。</summary>
    private static string? XmlValue(string xml, string tag)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            var node = doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, tag, StringComparison.OrdinalIgnoreCase));
            var value = node?.Value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>通道总数 = 模拟通道 + 数字（IP）通道，兼容新旧两种通道编码方式。</summary>
    private static int CountChannels(Native.NET_DVR_DEVICEINFO_V30 info)
    {
        int ipChannels = info.byIPChanNum | (info.byHighDChanNum << 8);
        return info.byChanNum + ipChannels;
    }

    private static string SafeFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}

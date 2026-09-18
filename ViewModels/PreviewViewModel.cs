using System.Collections.ObjectModel;
using System.Windows.Threading;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>预览通道下拉/树节点里的一项。</summary>
public sealed class PreviewChannelOption
{
    public int Number { get; init; }
    public string Label { get; init; } = string.Empty;
    public override string ToString() => Label;
}

/// <summary>
/// 预览设备树的一个节点。
///
/// 树是两层：设备（根）→ 通道（叶）。设备节点在登录成功后填充通道，
/// 未登录的设备节点没有子节点，界面上就是一枝光杆。
/// </summary>
public sealed class PreviewTreeNode : ObservableObject
{
    private bool _isExpanded = true;
    private string _stateText = "未登录";
    private bool _isLoggedIn;

    /// <summary>设备节点才有：指向设备本身；通道节点沿用同一设备引用（便于取 IP/会话）。</summary>
    public DiscoveredDevice? Device { get; init; }

    /// <summary>通道节点才有：通道号；设备节点为 0。</summary>
    public int ChannelNumber { get; init; }

    /// <summary>显示名。设备节点 = IP + 型号；通道节点 = 通道名。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>副标题（设备节点显示台账编号/凭据状态；通道节点显示设备 IP）。</summary>
    public string SubLabel { get; init; } = string.Empty;

    public bool IsDevice => ChannelNumber == 0;

    /// <summary>登录状态文字：未登录 / 登录中… / 已登录 · N 路通道 / 登录失败。</summary>
    public string StateText { get => _stateText; set => Set(ref _stateText, value); }

    public bool IsLoggedIn { get => _isLoggedIn; set => Set(ref _isLoggedIn, value); }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>通道子节点集合（只有设备节点用得上）。</summary>
    public ObservableCollection<PreviewTreeNode> Children { get; } = [];

    public override string ToString() => Label;
}

/// <summary>一个已开窗的预览格（宫格里的一个画面）。</summary>
public sealed class PreviewTile : ObservableObject
{
    private bool _isSelected;
    private string _statusText = "取流中…";
    private bool _isLive;
    private IntPtr _windowHandle;
    private bool _isEnlarged;
    private bool _isHidden;

    /// <summary>第几格（0 基）。</summary>
    public int Index { get; set; }

    public string DeviceIp { get; init; } = string.Empty;

    public int ChannelNumber { get; init; }

    /// <summary>显示名（OSD 通道名或重排后的「通道 N」），来自树节点；空则回退真实通道号。</summary>
    public string? ChannelName { get; init; }

    /// <summary>画面标题条上的文字，如 "192.168.1.64 · 北门球机" 或 "… · 通道 1"。</summary>
    public string Title => string.IsNullOrWhiteSpace(ChannelName)
        ? $"{DeviceIp} · 通道 {ChannelNumber}"
        : $"{DeviceIp} · {ChannelName}";

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>该格是否已在出流（决定状态灯颜色）。</summary>
    public bool IsLive { get => _isLive; set => Set(ref _isLive, value); }

    /// <summary>是否当前选中格（抓图针对它操作）。</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>
    /// 双击放大标记：放大模式下仅这一格为 true。放大不是把宿主搬走
    /// （WinForms Panel 的 HWND 只能有一个父容器），而是宫格切成 1×1、
    /// 其余格隐藏（UniformGrid 会跳过 Collapsed 的子项）。
    /// </summary>
    public bool IsEnlarged { get => _isEnlarged; internal set => Set(ref _isEnlarged, value); }

    /// <summary>
    /// 本格要不要藏起来（视图按它 Collapse 容器，UniformGrid 跳过 Collapsed 子项）：
    /// 放大态下除放大格以外都藏；分屏缩小时超出容量的格也藏（流不停，切回来立刻有画面）。
    /// </summary>
    public bool IsHidden { get => _isHidden; internal set => Set(ref _isHidden, value); }

    /// <summary>该格对应的 WinForms Panel 句柄，由视图注入。</summary>
    internal IntPtr WindowHandle
    {
        get => _windowHandle;
        set => _windowHandle = value;
    }
}

/// <summary>
/// 实时预览页（多画面版）。
///
/// 现场场景：一个 NVR 下面挂十几路，甲方要"全都看一眼"。
/// 旧的单画面只能一路一路切，太慢；现在改成「左边验证过的设备树 +
/// 右边多画面宫格」，双击树上的通道就开一格。
///
/// 实现要点：
///   1. 每台设备一个 LoginSession（`_sessions`），每格一个 RealPlay 句柄
///      （`_handles` 记账）。同设备的多个通道共用一次登录，不重复登录
///      既省时间，也避开设备"登录连接数已满"这个最常见的坑；
///   2. 视频仍是 NET_DVR_RealPlay_V40 的 hPlayWnd 模式：每格一个
///      WinForms Panel，SDK 自己解码渲染。格子数变化时视图重建 Panel，
///      句柄通过 StartTileAsync 回灌；
///   3. 空域问题不变：WindowsFormsHost 永远盖在 WPF 内容之上，
///      所以每格的标题条走"视频区外"的独立行，不叠在画面上；
///   4. 离开页面必须全停：换页会销毁所有 HWND，继续推流等于往
///      已销毁的窗口上画。Unloaded → StopAll。
/// </summary>
public sealed class PreviewViewModel : PageViewModel
{
    private const string Source = "预览";

    /// <summary>串行化登录/取流/停流的互斥锁：开窗、关窗、切流、离开页面都可能并发到达。</summary>
    private readonly object _gate = new();

    /// <summary>设备 Key → 该设备的登录会话。同一设备的多路通道共用一次登录。</summary>
    private readonly Dictionary<string, LoginSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>RealPlay 句柄 → 该路预览格（停流时按格找回句柄）。</summary>
    private readonly Dictionary<int, PreviewTile> _handles = new();

    private readonly DispatcherTimer _elapsedTimer;

    private string _userName = "admin";
    private bool _useSubStream = true;
    private string _statusText = "未连接 · 在左侧登录设备后双击通道开窗";
    private string _elapsedText = "00:00";
    private DateTime _startedAt;
    private bool _isBusy;
    private int _gridSize = 4;
    private int _snapshotCount;
    private bool _ptzVisible;
    private PreviewTile? _enlargedTile;

    /// <summary>云台能力缓存（IP:通道 → 是否支持），一次实测后面不再重复问设备。</summary>
    private readonly Dictionary<string, bool> _ptzCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>通道实况扫描缓存（设备 Key → 哪些通道真有画面），登录时实测一次。</summary>
    private readonly Dictionary<string, ChannelScanResult> _channelScans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>通道 OSD 名称缓存（设备 Key → 真实通道号 → 名称），登录时自动同步一次。</summary>
    private readonly Dictionary<string, Dictionary<int, string>> _channelNames = new(StringComparer.OrdinalIgnoreCase);

    private bool _filterEmptyChannels = true;

    public PreviewViewModel()
    {
        Tree = [];
        Tiles = [];

        if (!string.IsNullOrWhiteSpace(App.Settings.DefaultUserName)) _userName = App.Settings.DefaultUserName;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
            ElapsedText = Tiles.Any(t => t.IsLive) ? (DateTime.Now - _startedAt).ToString(@"mm\:ss") : "00:00";

        RefreshCommand = new RelayCommand(RefreshTree);
        Grid1Command = new RelayCommand(() => GridSize = 1);
        Grid4Command = new RelayCommand(() => GridSize = 4);
        Grid9Command = new RelayCommand(() => GridSize = 9);
        CloseTileCommand = new RelayCommand(p => CloseTile(p as PreviewTile));
        CloseAllCommand = new RelayCommand(CloseAllTiles, () => Tiles.Count > 0);
        SnapshotCommand = new RelayCommand(CaptureSnapshot, () => SelectedTile != null);
        OpenSnapshotsCommand = new RelayCommand(OpenSnapshotFolder);
        LogoutAllCommand = new RelayCommand(LogoutAll, () => _sessions.Count > 0);
        OpenChannelManagerCommand = new RelayCommand(OpenChannelManager, () => SelectedDeviceNode != null);
        ToggleExpandedCommand = new RelayCommand(p =>
        {
            if (p is PreviewTreeNode node && node.IsDevice) node.IsExpanded = !node.IsExpanded;
        });
    }

    public override string Title => "实时预览";

    public override string Glyph => "IconEye";

    public override string Description => "多画面实时预览：左侧设备树双击通道开窗，支持 1/4/9 分屏";

    public override string Usage => "入库设备进本页自动免密登录，双击通道开一格；未入库的设备先到「设备发现」页「验证并入库」。双击画面放大/还原；支持云台的设备（球机等）选中后下方出现虚拟云台，按住方向键转动。通道显示编号从 1 重排、名称自动同步设备 OSD；切换码流立即对已开画面生效；空通道用左下「通道管理」手动勾选显隐（缩略图确认）。";

    // ==================== 码流 ====================

    /// <summary>true=子码流（多画面首选，省带宽），false=主码流（单画面看清晰度）。
    /// 切换立即生效：对已经在播的画面自动停流重开，不用手动关了再开。</summary>
    public bool UseSubStream
    {
        get => _useSubStream;
        set
        {
            if (!Set(ref _useSubStream, value)) return;
            OnPropertyChanged(nameof(StreamText));
            if (!_switchingStreams) _ = SwitchStreamsAsync();
        }
    }

    private bool _switchingStreams;

    public string StreamText => _useSubStream ? "子码流" : "主码流";

    /// <summary>
    /// 码流实时切换：把所有在播画面停流重开（保持窗口句柄不变）。
    /// 以前只在"下一路开窗"时生效，现场切完盯着旧画面以为没切换成功。
    /// </summary>
    private async Task SwitchStreamsAsync()
    {
        _switchingStreams = true;
        try
        {
            var targets = Tiles.Where(t => t.IsLive && t.WindowHandle != IntPtr.Zero).ToList();
            if (targets.Count == 0)
            {
                StatusText = $"码流已切换为{StreamText}（对下一路开窗生效）";
                return;
            }

            IsBusy = true;
            foreach (var tile in targets)
            {
                await Task.Run(() =>
                {
                    lock (_gate)
                    {
                        foreach (var kv in _handles.Where(k => ReferenceEquals(k.Value, tile)).ToList())
                        {
                            App.NetSdk.StopRealPlay(kv.Key);
                            _handles.Remove(kv.Key);
                        }
                    }
                }).ConfigureAwait(true);
                tile.IsLive = false;
                await StartTileAsync(tile, tile.WindowHandle).ConfigureAwait(true);
            }
            IsBusy = false;
            StatusText = $"码流已实时切换为{StreamText} · {targets.Count} 路画面已重开";
            Log.Info($"码流实时切换 → {StreamText}，重开 {targets.Count} 路", Source);
        }
        finally
        {
            _switchingStreams = false;
        }
    }

    /// <summary>
    /// 只列出真有画面的通道（默认开）。
    ///
    /// 录像机按机型报通道数：8 路机器只接 3 个摄像头也报 8 路，
    /// 不过滤的话预览页会挂一串点开全黑的通道，现场很难看。
    /// 判定靠登录时的一次 ISAPI 实测（见 NetSdkService.DetectVideoChannels）；
    /// 判定不了（老固件）就原样全列，不会自作聪明藏掉真画面。
    /// </summary>
    public bool FilterEmptyChannels
    {
        get => _filterEmptyChannels;
        set
        {
            if (!Set(ref _filterEmptyChannels, value)) return;
            RebuildChannelLists();
            StatusText = value
                ? "已过滤无画面的通道（已登录设备重新列出通道）"
                : "已显示全部通道（含未接摄像机的空通道）";
        }
    }

    private bool _audioEnabled;

    /// <summary>
    /// 打开/关闭预览音频（勾选框）。
    ///
    /// hPlayWnd 解码模式下声音是全局开关（NET_DVR_ClientAudioStart），
    /// 不是"哪格选中就出哪格的声音"——所有在播画面共用一路混音。
    /// 没有画面在播时 SDK 打不开音频，直接提示并回弹勾选。
    /// 关最后一格画面 / 离开页面时会自动关掉音频并回弹勾选。
    /// </summary>
    public bool AudioEnabled
    {
        get => _audioEnabled;
        set
        {
            if (!Set(ref _audioEnabled, value)) return;

            if (value)
            {
                var (ok, msg) = App.NetSdk.StartAudio();
                if (ok)
                {
                    StatusText = SelectedTile != null
                        ? $"{SelectedTile.Title} 音频已打开（多画面时只混播一路声音）"
                        : "音频已打开";
                }
                else
                {
                    // 回弹勾选：Set 已经把字段写成 true，这里手动翻回去
                    _audioEnabled = false;
                    OnPropertyChanged(nameof(AudioEnabled));
                    Toast($"音频打开失败：{msg}", true);
                    StatusText = $"音频打开失败：{msg}";
                }
            }
            else
            {
                App.NetSdk.StopAudio();
                StatusText = "音频已关闭";
            }
        }
    }

    // ==================== 设备树 ====================

    /// <summary>左侧设备树根节点。台账设备 + 当场发现且已激活的设备。</summary>
    public ObservableCollection<PreviewTreeNode> Tree { get; }

    private PreviewTreeNode? _selectedNode;

    public PreviewTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (Set(ref _selectedNode, value))
            {
                OnPropertyChanged(nameof(NodeSummary));
                OnPropertyChanged(nameof(SelectedDeviceLoggedIn));
            }
        }
    }

    public string NodeSummary => _selectedNode == null
        ? "未选择设备"
        : _selectedNode.IsDevice
            ? $"{_selectedNode.Label}（{_selectedNode.StateText}）"
            : $"{_selectedNode.SubLabel} · 通道 {_selectedNode.ChannelNumber}";

    /// <summary>选中的是已登录的设备节点（决定「断开」这类按钮是否可用）。</summary>
    public bool SelectedDeviceLoggedIn => _selectedNode is { IsDevice: true, IsLoggedIn: true };

    /// <summary>树为空时显示的空态提示。</summary>
    public bool TreeEmpty => Tree.Count == 0;

    // ==================== 宫格 ====================

    /// <summary>已开窗的画面。顺序即宫格顺序。</summary>
    public ObservableCollection<PreviewTile> Tiles { get; }

    /// <summary>分屏数：1 / 4 / 9。切分屏会自动退出放大态。</summary>
    public int GridSize
    {
        get => _gridSize;
        private set
        {
            if (Set(ref _gridSize, value))
            {
                ExitEnlarge();
                OnPropertyChanged(nameof(GridColumns));
                OnPropertyChanged(nameof(GridRows));
                OnPropertyChanged(nameof(GridSummary));
            }
        }
    }

    /// <summary>放大态下宫格强制 1×1（其余格隐藏），UniformGrid 跳过 Collapsed 子项后单格铺满。</summary>
    public int GridColumns => _enlargedTile != null ? 1 : _gridSize == 1 ? 1 : _gridSize == 4 ? 2 : 3;

    public int GridRows => GridColumns;

    public string GridSummary => _enlargedTile != null
        ? $"放大画面 · 已开 {Tiles.Count} 路"
        : $"{GridSize} 分屏 · 已开 {Tiles.Count} 路";

    /// <summary>选中格的设备支持云台时，右栏显示虚拟云台面板（能力由 ISAPI 实测，不猜型号）。</summary>
    public bool PtzVisible { get => _ptzVisible; private set => Set(ref _ptzVisible, value); }

    /// <summary>当前选中的格（抓图针对它）。</summary>
    public PreviewTile? SelectedTile => Tiles.FirstOrDefault(t => t.IsSelected);

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    public int SnapshotCount { get => _snapshotCount; private set => Set(ref _snapshotCount, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand Grid1Command { get; }
    public RelayCommand Grid4Command { get; }
    public RelayCommand Grid9Command { get; }
    public RelayCommand CloseTileCommand { get; }
    public RelayCommand CloseAllCommand { get; }
    public RelayCommand SnapshotCommand { get; }
    public RelayCommand OpenSnapshotsCommand { get; }
    public RelayCommand LogoutAllCommand { get; }
    public RelayCommand OpenChannelManagerCommand { get; }
    public RelayCommand ToggleExpandedCommand { get; }

    // ==================== 生命周期 ====================

    public override void OnEnter()
    {
        RefreshTree();
        _ = AutoLoginStoredAsync();
    }

    public override void OnDevicesInjected(IReadOnlyList<DiscoveredDevice> devices)
    {
        RefreshTree();
        var match = devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.IPv4Address));
        if (match == null) return;
        var node = Tree.FirstOrDefault(n => n.IsDevice &&
            string.Equals(n.Device?.Key, match.Key, StringComparison.OrdinalIgnoreCase));
        if (node != null) SelectedNode = node;
    }

    /// <summary>
    /// 重建设备树。只放"可信"的设备：
    ///   1. 台账里的设备（发现页「验证并入库」过的，副标题带验证标记）；
    ///   2. 当场搜到且已激活、但还没入库的（用户可能想先看画面）。
    /// 未激活设备不进树 —— 登录必然失败，列出来只是噪音。
    ///
    /// 关键：**重建时要保留已有登录会话**。否则用户点一次「刷新」，
    /// 树被清空重造，已登录的设备全回落成"未登录"、通道列表消失，
    /// 而 _sessions 里的会话还挂着 —— 界面上看着没登录，实际占着
    /// 设备连接数，再点登录必然报"连接数已满"。
    /// 所以这里对 `_sessions` 里存在的设备，直接把通道列表和状态补回去。
    /// </summary>
    private void RefreshTree()
    {
        var keepKey = _selectedNode?.Device?.Key;
        int keepChannel = _selectedNode?.IsDevice == false ? _selectedNode.ChannelNumber : 0;

        // 先把"哪些设备当前有活会话"记下来（Tree 清空后旧节点就找不回了）
        var liveKeys = new HashSet<string>(_sessions.Keys, StringComparer.OrdinalIgnoreCase);

        Tree.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先放台账设备（跨网段 / 改过 IP 的设备只能靠台账找回来）
        foreach (var asset in App.Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Ip)) continue;
            var device = DeviceFromAsset(asset);
            if (!seen.Add(device.Key)) continue;
            Tree.Add(new PreviewTreeNode
            {
                Device = device,
                Label = $"{device.IPv4Address} · {Truncate(device.ModelText, 16)}",
                SubLabel = string.IsNullOrWhiteSpace(asset.Verified)
                    ? $"台账 {asset.Id} · 未验证凭据"
                    : $"台账 {asset.Id} · {asset.Verified}",
            });
        }

        // 再补当场发现、已激活、尚未入库的设备
        foreach (var d in App.Devices)
        {
            if (string.IsNullOrWhiteSpace(d.IPv4Address)) continue;
            if (d.Activate != ActivateState.Activated) continue;
            if (!seen.Add(d.Key)) continue;
            Tree.Add(new PreviewTreeNode
            {
                Device = d,
                Label = $"{d.IPv4Address} · {Truncate(d.ModelText, 16)}",
                SubLabel = "当场发现 · 未入库",
            });
        }

        // 把活会话的状态回填到新节点上（通道列表重建，状态文字还原）
        if (liveKeys.Count > 0)
        {
            foreach (var node in Tree.Where(n => n.IsDevice))
            {
                var key = node.Device?.Key;
                if (key == null || !liveKeys.Contains(key)) continue;
                if (!_sessions.TryGetValue(key, out var session) || session is not { Ok: true }) continue;

                foreach (var c in BuildChannels(session, key))
                    node.Children.Add(new PreviewTreeNode
                    {
                        Device = node.Device,
                        ChannelNumber = c.Number,
                        Label = c.Label,
                        SubLabel = node.Device?.IPv4Address ?? string.Empty,
                        StateText = ChannelIdleText,
                    });

                node.IsLoggedIn = true;
                node.IsExpanded = true;
                node.StateText = ChannelStateText(session, key, node.Children.Count);
            }
        }

        // 恢复选中（同设备同通道）
        PreviewTreeNode? restored = null;
        if (keepKey != null)
        {
            var again = Tree.FirstOrDefault(n => n.IsDevice &&
                string.Equals(n.Device?.Key, keepKey, StringComparison.OrdinalIgnoreCase));
            if (again != null)
            {
                restored = again;
                if (keepChannel > 0)
                {
                    var ch = again.Children.FirstOrDefault(c => c.ChannelNumber == keepChannel);
                    if (ch != null) restored = ch;
                }
            }
        }

        // 原选中设备已被移出树（比如台账被清空）时，优先落到第一个已登录设备
        restored ??= Tree.FirstOrDefault(n => n.IsDevice && n.IsLoggedIn) ?? Tree.FirstOrDefault();

        _selectedNode = restored;
        OnPropertyChanged(nameof(SelectedNode));
        OnPropertyChanged(nameof(NodeSummary));
        OnPropertyChanged(nameof(SelectedDeviceLoggedIn));
        OnPropertyChanged(nameof(TreeEmpty));
        RaiseStates();

        if (Tree.Count == 0)
            StatusText = "暂无可用设备：先到「设备发现」页搜索并「验证并入库」，或确认资产台账已导入";
        else if (liveKeys.Count > 0)
            StatusText = $"设备列表已刷新 · 保留 {liveKeys.Count} 台已登录设备 · {GridSummary}";
    }

    private static DiscoveredDevice DeviceFromAsset(AssetRecord a) => new()
    {
        UniformDevID = a.SerialNo,
        SerialNo = a.SerialNo,
        Mac = a.Mac,
        IPv4Address = a.Ip,
        SdkPort = a.Port == 0 ? (ushort)8000 : a.Port,
        HttpPort = a.HttpPort,
        DeviceDesc = a.Model,
        DeviceDescEx = a.Model,
        Manufacturer = a.Manufacturer,
        SoftwareVersion = a.FirmwareVersion,
        AdminUserName = a.AdminUserName,
        Activate = ActivateState.Activated,
    };

    private static string Truncate(string text, int max)
        => string.IsNullOrWhiteSpace(text) ? "未知型号" : text.Length <= max ? text : text[..max] + "…";

    private void RaiseStates()
    {
        CloseAllCommand.RaiseCanExecuteChanged();
        SnapshotCommand.RaiseCanExecuteChanged();
        LogoutAllCommand.RaiseCanExecuteChanged();
    }

    // ==================== 登录设备（填充通道） ====================

    /// <summary>通道节点在"没开窗"时的状态字。曾经沿用默认的"未登录"，
    /// 设备明明登录了、通道下面却挂着"未登录"，现场反复被误解成登录失败。</summary>
    internal const string ChannelIdleText = "未开窗 · 双击";

    /// <summary>点设备节点：未登录则登录并展开通道，已登录则折叠/展开。
    /// 台账里存过口令的设备（入库时验证过的）直接免密登录，不要求再填口令。</summary>
    public async Task ToggleDeviceAsync(PreviewTreeNode? node, bool toggleIfLoggedIn = true)
    {
        if (node is not { IsDevice: true }) return;

        if (node.IsLoggedIn)
        {
            if (toggleIfLoggedIn) node.IsExpanded = !node.IsExpanded;
            return;
        }

        var device = node.Device;
        if (device == null) return;

        // 凭据只从台账取（入库时验证过的免密直连）——本页不再摆登录框：
        // 没入库的设备给出去处提示，而不是让现场对着一个空口令框发呆。
        var stored = GetStoredCredential(device);
        if (stored == null)
        {
            Toast("该设备没有存过登录凭据：请到「设备发现」页「验证并入库」（或在台账手工添加时勾选验证），之后本页自动免密登录", true);
            StatusText = $"{device.IPv4Address} 未存登录凭据，请先在「设备发现」页验证并入库";
            return;
        }
        var (user, password) = stored.Value;

        IsBusy = true;
        node.StateText = "登录中…";
        StatusText = $"正在登录 {device.IPv4Address}:{device.SdkPort} …";

        var result = await Task.Run<(bool ok, string message, List<PreviewTreeNode> channels)>(() =>
        {
            LoginSession session;
            lock (_gate)
            {
                DropSession(device.Key);

                session = App.NetSdk.Login(device.IPv4Address, device.SdkPort, user, password, device.HttpPort);
                if (!session.Ok) return (false, session.Message, []);

                _sessions[device.Key] = session;
            }

            // OSD 名称同步 + 通道实况扫描放在锁外：ISAPI 往返可能要几秒，
            // 别把停流/开窗一起卡住
            SyncChannels(session, device.Key);

            var channels = new List<PreviewTreeNode>();
            foreach (var c in BuildChannels(session, device.Key))
                channels.Add(new PreviewTreeNode
                {
                    Device = device,
                    ChannelNumber = c.Number,
                    Label = c.Label,
                    SubLabel = device.IPv4Address,
                    StateText = ChannelIdleText,
                });

            return (true, ChannelStateText(session, device.Key, channels.Count), channels);
        }).ConfigureAwait(true);

        var (ok, message, channels) = result;
        IsBusy = false;

        if (!ok)
        {
            node.IsLoggedIn = false;
            node.StateText = "登录失败";
            StatusText = $"{device.IPv4Address} 登录失败：{message}";
            Log.Warn($"预览登录失败 {device.IpMaskText}：{message}", Source);
            Toast($"{device.IPv4Address} 登录失败：{message}", true);
            RaiseStates();
            return;
        }

        node.Children.Clear();
        foreach (var c in channels) node.Children.Add(c);
        node.IsLoggedIn = true;
        node.IsExpanded = true;
        node.StateText = message;
        OnPropertyChanged(nameof(SelectedDeviceLoggedIn));

        StatusText = $"{device.IPv4Address} 已登录 · 双击通道开窗";
        Log.Info($"预览登录 {device.IPv4Address} {device.ModelText}，{channels.Count} 路通道", Source);
        RaiseStates();
    }

    // ==================== 开窗 / 关窗 ====================

    /// <summary>双击通道节点：开一格画面。</summary>
    public async Task OpenChannelAsync(PreviewTreeNode? node)
    {
        if (node == null || node.IsDevice) return;
        var device = node.Device;
        if (device == null) return;

        if (!_sessions.TryGetValue(device.Key, out var session) || session is not { Ok: true })
        {
            Toast("该设备尚未登录，请先点设备名登录", true);
            return;
        }

        // 同一路已经开着：直接选中它，不重复开
        var exist = Tiles.FirstOrDefault(t =>
            string.Equals(t.DeviceIp, device.IPv4Address, StringComparison.OrdinalIgnoreCase) &&
            t.ChannelNumber == node.ChannelNumber);
        if (exist != null)
        {
            SelectTile(exist);
            StatusText = $"{exist.Title} 已在画面中";
            return;
        }

        // 格满了：自动升一档分屏（1 → 4 → 9）；清单里已经超编就一步到位升满
        if (Tiles.Count >= GridSize)
        {
            int next = GridSize == 1 ? 4 : 9;
            if (Tiles.Count >= next) next = 9;
            if (GridSize >= 9)
            {
                Toast("9 分屏已满，请先关掉一格再开", true);
                return;
            }
            GridSize = next;
        }

        var tile = new PreviewTile
        {
            Index = Tiles.Count,
            DeviceIp = device.IPv4Address,
            ChannelNumber = node.ChannelNumber,
            ChannelName = node.Label,
            StatusText = "等待窗口…",
        };
        Tiles.Add(tile);
        SelectTile(tile);
        node.StateText = "预览中";
        OnPropertyChanged(nameof(GridSummary));
        RaiseStates();
        Log.Info($"请求开窗 {tile.Title}，等待视频宿主就绪…", Source);

        // 视图会在容器生成后回调 StartTileAsync；这里不直接取流。
    }

    /// <summary>
    /// 视图注入句柄后调用：真正取流。单路失败只影响这一格。
    /// </summary>
    internal async Task StartTileAsync(PreviewTile tile, IntPtr windowHandle)
    {
        AttachProbeCount++;   // 自检探针：只要宿主把句柄回灌过来就会 +1

        // 同一格同一句柄重复回灌（布局变化会重复触发 Loaded）：已经在出图就别再开一路，
        // 否则会多出一个 RealPlay 句柄没人回收
        if (tile.IsLive && tile.WindowHandle == windowHandle) return;

        tile.WindowHandle = windowHandle;
        if (windowHandle == IntPtr.Zero)
        {
            tile.StatusText = "窗口无效";
            Log.Warn($"{tile.Title} 取流中止：视频宿主未拿到有效窗口句柄", Source);
            return;
        }

        var device = Tree.FirstOrDefault(n => n.IsDevice &&
            string.Equals(n.Device?.IPv4Address, tile.DeviceIp, StringComparison.OrdinalIgnoreCase))?.Device;
        if (device == null)
        {
            tile.StatusText = "设备已移除";
            Log.Warn($"{tile.Title} 取流中止：设备已不在设备树中", Source);
            return;
        }

        if (!_sessions.TryGetValue(device.Key, out var session) || session is not { Ok: true })
        {
            tile.StatusText = "设备未登录";
            Log.Warn($"{tile.Title} 取流中止：设备会话不存在（请先双击设备名登录）", Source);
            return;
        }

        uint streamType = _useSubStream ? 1u : 0u;
        IsBusy = true;
        var (handle, msg) = await Task.Run(() =>
        {
            lock (_gate)
            {
                if (!_sessions.ContainsKey(device.Key)) return (-1, "设备会话已断开");
                return App.NetSdk.RealPlay(session.UserId, tile.ChannelNumber, windowHandle, streamType);
            }
        }).ConfigureAwait(true);
        IsBusy = false;

        // 格子可能在等待期间被关掉了：句柄要立刻收回去
        if (!Tiles.Contains(tile))
        {
            // 有意不 await：停流可能阻塞几百毫秒，这里没必要挡住调用方。
            // 用 _ = 显式丢弃，避免编译器 CS4014。
            if (handle >= 0) _ = Task.Run(() => App.NetSdk.StopRealPlay(handle));
            return;
        }

        if (handle < 0)
        {
            tile.IsLive = false;
            tile.StatusText = $"取流失败：{msg}";
            Log.Warn($"{tile.Title} 取流失败：{msg}", Source);
            Toast($"{tile.Title} 取流失败：{msg}", true);
            return;
        }

        lock (_gate) _handles[handle] = tile;
        tile.IsLive = true;
        tile.StatusText = StreamText;

        if (!_elapsedTimer.IsEnabled) { _startedAt = DateTime.Now; _elapsedTimer.Start(); }

        StatusText = $"{tile.Title} 已出图（{StreamText}）· {GridSummary}";
        Log.Info($"开窗 {tile.Title}（{StreamText}）", Source);
        CheckPtzForTile(tile);
        RaiseStates();
    }

    /// <summary>关掉一格：停流 + 从宫格移除。</summary>
    public void CloseTile(PreviewTile? tile)
    {
        if (tile == null) return;

        Task.Run(() =>
        {
            lock (_gate)
            {
                foreach (var kv in _handles.Where(k => ReferenceEquals(k.Value, tile)).ToList())
                {
                    App.NetSdk.StopRealPlay(kv.Key);
                    _handles.Remove(kv.Key);
                }
            }
        });

        Tiles.Remove(tile);
        for (int i = 0; i < Tiles.Count; i++) Tiles[i].Index = i;

        SetChannelState(tile.DeviceIp, tile.ChannelNumber, ChannelIdleText);
        if (_enlargedTile == tile) ExitEnlarge();
        // 关格后索引整体前移，超容量/放大的隐藏集合都可能变化：统一重算
        ApplyEnlargeState();

        if (Tiles.Count > 0 && !Tiles.Any(t => t.IsSelected)) SelectTile(Tiles[0]);
        if (Tiles.Count == 0)
        {
            OnPropertyChanged(nameof(SelectedTile));
            // 画面全关了音频也就没了声源，自动回弹勾选
            if (_audioEnabled)
            {
                _audioEnabled = false;
                OnPropertyChanged(nameof(AudioEnabled));
                App.NetSdk.StopAudio();
            }
        }

        OnPropertyChanged(nameof(GridSummary));
        StatusText = Tiles.Count > 0 ? $"已关闭一路 · {GridSummary}" : "全部画面已关闭";
        RaiseStates();
    }

    private void CloseAllTiles()
    {
        foreach (var tile in Tiles.ToList()) CloseTile(tile);
        StatusText = "全部画面已关闭";
        RaiseStates();
    }

    private void SelectTile(PreviewTile tile)
    {
        foreach (var t in Tiles) t.IsSelected = ReferenceEquals(t, tile);
        OnPropertyChanged(nameof(SelectedTile));
        SnapshotCommand.RaiseCanExecuteChanged();
        CheckPtzForTile(tile);
    }

    /// <summary>
    /// 自检探针：StartTileAsync 被调用过几次。
    ///
    /// 存在的理由：曾经"格子建出来了、但句柄从没回灌"这个 bug 完全静默——
    /// TileHost 在 DataTemplate 里用 DataContext 找 VM 永远找不到，
    /// 表现是登录成功、界面无报错、就是全黑，日志里连一条取流记录都没有。
    /// 现在 --livecheck 会塞一格假画面实测这条链，黑屏不再靠肉眼发现。
    /// </summary>
    internal int AttachProbeCount { get; private set; }

    /// <summary>自检专用：塞若干格假画面，触发完整的宿主建立 + 句柄回灌（不取流）。</summary>
    internal void AddProbeTiles(int count = 4)
    {
        for (int i = 0; i < count; i++)
            Tiles.Add(new PreviewTile
            {
                Index = Tiles.Count,
                DeviceIp = ProbeTileIp,
                ChannelNumber = i + 1,
                StatusText = "自检探针",
            });
        OnPropertyChanged(nameof(GridSummary));
    }

    /// <summary>
    /// 自检专用：放大指定序号的探针格。
    ///
    /// 为什么要专门测它：双击放大曾经"只有第一格正常，别的格一放大就错位/消失"。
    /// 原因是隐藏做在了模板内部的元素上，容器照样占位，而 UniformGrid 会按行
    /// 继续排下去（第 2、3、4 个被排到可视区下方）。只看第一格永远看不出问题，
    /// 所以自检放大的必须是**非第一格**。
    /// </summary>
    internal void EnlargeProbeTile(int index)
    {
        if (index < 0 || index >= Tiles.Count) return;
        _enlargedTile = Tiles[index];
        ApplyEnlargeState();
        StatusText = $"{Tiles[index].Title} 已放大 · 自检布局";
    }

    /// <summary>自检专用：模拟"从多切到少"分屏（如 4 格在 1 分屏下），复用放大布局断言。</summary>
    internal void SetProbeGridSize(int size) => GridSize = size;

    /// <summary>自检专用：撤掉探针格并复位放大态（否则分屏按钮会一直停在"放大"）。</summary>
    internal void RemoveProbeTile()
    {
        foreach (var t in Tiles.Where(t => t.DeviceIp == ProbeTileIp).ToList()) Tiles.Remove(t);
        ExitEnlarge();
        OnPropertyChanged(nameof(GridSummary));
    }

    private const string ProbeTileIp = "127.0.0.1";

    /// <summary>视图层在格子被点击时调用。</summary>
    internal void SelectTileFromView(PreviewTile tile) => SelectTile(tile);

    /// <summary>视图层双击格子：放大/还原。</summary>
    internal void ToggleEnlargeFromView(PreviewTile tile)
    {
        if (tile == null) return;
        _enlargedTile = ReferenceEquals(_enlargedTile, tile) ? null : tile;
        ApplyEnlargeState();
        StatusText = _enlargedTile == null
            ? "已还原多画面"
            : $"{tile.Title} 已放大 · 再双击画面还原";
        Log.Info(_enlargedTile == null ? "画面还原多画面" : $"{tile.Title} 双击放大", Source);
    }

    private void ExitEnlarge()
    {
        _enlargedTile = null;
        ApplyEnlargeState();
    }

    private void ApplyEnlargeState()
    {
        // IsHidden 的完整语义（视图按它 Collapse 容器，UniformGrid 跳过 Collapsed 子项）：
        //   ① 放大态：除放大格以外的全部藏掉；
        //   ② 分屏缩小（9→1 / 4→1 / 9→4）：超出容量的格藏掉。
        //      ②曾缺失 —— 切分屏只改 UniformGrid 行列数、Tiles 集合一个不删，
        //      多出来的格被排到可视视频区"下一行"，WPF 边框 + WinForms 视频 HWND
        //      （永远盖在 WPF 之上）压到下方的操作条/云台上，表现就是"从多切到少错位"。
        //      隐藏只是看不见，流不停，切回大分屏立刻有画面。
        for (int i = 0; i < Tiles.Count; i++)
        {
            var t = Tiles[i];
            t.IsEnlarged = ReferenceEquals(t, _enlargedTile);
            t.IsHidden = _enlargedTile != null
                ? !ReferenceEquals(t, _enlargedTile)
                : i >= _gridSize;
        }
        OnPropertyChanged(nameof(GridColumns));
        OnPropertyChanged(nameof(GridRows));
        OnPropertyChanged(nameof(GridSummary));
    }

    /// <summary>按 IP 找设备树里的设备节点（树里存的 DiscoveredDevice 带端口/HTTP 端口等登录信息）。</summary>
    private DiscoveredDevice? FindDeviceByIp(string ip)
        => Tree.FirstOrDefault(n => n.IsDevice &&
            string.Equals(n.Device?.IPv4Address, ip, StringComparison.OrdinalIgnoreCase))?.Device;

    /// <summary>
    /// 找台账里存过的登录凭据：序列号优先、IP 兜底（与入库匹配口径一致）。
    /// 返回 null = 台账没这台或没存过口令，调用方走"要求手输口令"的老路。
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

    /// <summary>
    /// 免密直连：入库时验证过的口令随台账保存（DPAPI 加密），进页面后对
    /// 这些设备后台自动登录，双击通道即可看画面，不再反复输密码。
    /// 未入库 / 没存过口令的设备不受影响，仍走右上角口令框。
    /// 自检（--livecheck）模式跳过：自检不允许产生任何真实网络副作用。
    /// </summary>
    private async Task AutoLoginStoredAsync()
    {
        if (HeadlessMode) return;

        foreach (var node in Tree.Where(n => n.IsDevice && !n.IsLoggedIn).ToList())
        {
            var device = node.Device;
            if (device == null || GetStoredCredential(device) == null) continue;
            try { await ToggleDeviceAsync(node, toggleIfLoggedIn: false).ConfigureAwait(true); }
            catch (Exception ex) { Log.Warn($"自动登录 {device.IPv4Address} 异常：{ex.Message}", Source); }
        }
    }

    /// <summary>自检置位：禁止一切真实网络副作用（自动登录、云台探测都看它）。</summary>
    internal static bool HeadlessMode { get; set; }

    /// <summary>更新树里某个通道节点的状态字（设备 IP + 通道号定位）。</summary>
    private void SetChannelState(string deviceIp, int channel, string text)
    {
        var node = Tree.FirstOrDefault(n => !n.IsDevice &&
            string.Equals(n.SubLabel, deviceIp, StringComparison.OrdinalIgnoreCase) &&
            n.ChannelNumber == channel);
        if (node != null) node.StateText = text;
    }

    // ==================== 云台 ====================

    /// <summary>选中格变化/开窗成功后实测一次云台能力（有缓存，不反复问设备）。</summary>
    internal void CheckPtzForTile(PreviewTile? tile)
    {
        PtzVisible = false;
        if (tile == null) return;
        if (HeadlessMode) return;

        var device = FindDeviceByIp(tile.DeviceIp);
        if (device == null || !_sessions.TryGetValue(device.Key, out var session) || session is not { Ok: true })
            return;

        string key = $"{tile.DeviceIp}:{tile.ChannelNumber}";
        int userId = session.UserId;
        int channel = tile.ChannelNumber;

        Task.Run(() =>
        {
            bool supported;
            lock (_ptzCache)
            {
                if (_ptzCache.TryGetValue(key, out var cached)) supported = cached;
                else
                {
                    var (sup, _) = App.NetSdk.CheckPtzSupport(userId, channel);
                    _ptzCache[key] = sup;
                    supported = sup;
                    Log.Info($"云台能力探测 {tile.Title}：{(supported ? "支持" : "不支持")}", Source);
                }
            }

            UiInvoke(() =>
            {
                // 探测期间用户可能已经切到别的格，只有还是它选中才刷新面板
                var current = SelectedTile;
                if (current != null &&
                    string.Equals(current.DeviceIp, tile.DeviceIp, StringComparison.OrdinalIgnoreCase) &&
                    current.ChannelNumber == tile.ChannelNumber)
                    PtzVisible = supported;
            });
        });
    }

    /// <summary>云台开始转动（视图"按住"时调用）。命令值见 NetSdkService.PtzCommand。</summary>
    internal void PtzStart(uint command) => PtzExecute(command, stop: false);

    /// <summary>云台停止（视图"松开/移出"时调用）。</summary>
    internal void PtzStop(uint command) => PtzExecute(command, stop: true);

    private void PtzExecute(uint command, bool stop)
    {
        var tile = SelectedTile;
        if (tile == null) return;
        var device = FindDeviceByIp(tile.DeviceIp);
        if (device == null || !_sessions.TryGetValue(device.Key, out var session) || session is not { Ok: true })
        {
            if (!stop) Toast("云台所在设备已断开，请重新开窗", true);
            return;
        }

        int userId = session.UserId;
        int channel = tile.ChannelNumber;
        string title = tile.Title;
        Task.Run(() =>
        {
            if (!App.NetSdk.PtzControl(userId, channel, command, stop))
            {
                uint err = App.NetSdk.LastError;
                // 停止命令失败不弹打扰，只有开始失败才提示（画面上方向不动本身就是反馈）
                if (!stop)
                {
                    UiInvoke(() => Toast($"云台控制失败（{title}，SDK {err}）", true));
                    Log.Warn($"云台控制失败（{title}，命令 {command}，SDK {err}）", Source);
                }
            }
        });
    }

    /// <summary>
    /// 视图层在格子被卸载时调用（分屏数变化重建容器、离开页面）。
    /// 只停这一路的流、不把格子从集合里删掉 —— 重建后会同格重新取流。
    /// </summary>
    internal void StopTile(PreviewTile tile)
    {
        if (tile == null) return;
        tile.WindowHandle = IntPtr.Zero;
        tile.IsLive = false;

        Task.Run(() =>
        {
            lock (_gate)
            {
                foreach (var kv in _handles.Where(k => ReferenceEquals(k.Value, tile)).ToList())
                {
                    App.NetSdk.StopRealPlay(kv.Key);
                    _handles.Remove(kv.Key);
                }
            }
        });
    }

    // ==================== 停流 / 登出 ====================

    /// <summary>停掉全部画面与登录会话。离开页面、手动断开都走这里。</summary>
    internal void StopAll()
    {
        int tiles = Tiles.Count;
        int sessions = _sessions.Count;

        Task.Run(() =>
        {
            lock (_gate)
            {
                foreach (var kv in _handles.ToList())
                {
                    App.NetSdk.StopRealPlay(kv.Key);
                    _handles.Remove(kv.Key);
                }
                foreach (var kv in _sessions.ToList())
                {
                    if (kv.Value is { Ok: true }) App.NetSdk.Logout(kv.Value.UserId);
                    _sessions.Remove(kv.Key);
                }
            }
        });

        Tiles.Clear();
        _channelScans.Clear();
        foreach (var node in Tree.Where(n => n.IsDevice))
        {
            node.IsLoggedIn = false;
            node.StateText = "未登录";
            node.Children.Clear();
        }

        ExitEnlarge();
        PtzVisible = false;
        // 音频是全局开关，离开页面/断开全部时必须一并关掉，否则离开预览页后可能还有声
        if (_audioEnabled)
        {
            _audioEnabled = false;
            OnPropertyChanged(nameof(AudioEnabled));
            App.NetSdk.StopAudio();
        }
        _elapsedTimer.Stop();
        ElapsedText = "00:00";
        OnPropertyChanged(nameof(SelectedTile));
        OnPropertyChanged(nameof(GridSummary));
        StatusText = tiles > 0 || sessions > 0 ? "已断开全部连接" : "未连接 · 在左侧登录设备后双击通道开窗";
        RaiseStates();
    }

    private void LogoutAll() => StopAll();

    /// <summary>换设备登录前，先把这台设备上一次的会话收掉。</summary>
    private void DropSession(string deviceKey)
    {
        _channelScans.Remove(deviceKey);   // 会话没了，通道实况缓存也一并作废
        if (!_sessions.TryGetValue(deviceKey, out var old)) return;
        _sessions.Remove(deviceKey);
        if (old is { Ok: true }) App.NetSdk.Logout(old.UserId);
    }

    /// <summary>
    /// 登录后同步两样东西（都只读、失败都不影响登录）：
    ///   ① OSD 名称：设备的通道名自动拉下来当显示名，现场一眼认出哪路是哪个摄像头；
    ///   ② 通道实况：哪些通道真有画面（录像机按机型报通道数，点开全黑的那种要标记出来）。
    /// 扫描结果常驻缓存，是否用于过滤由 FilterEmptyChannels 决定；通道管理弹窗也用它标状态。
    /// </summary>
    private void SyncChannels(LoginSession session, string deviceKey)
    {
        if (HeadlessMode) return;

        // ① OSD 名称同步
        try
        {
            var names = App.NetSdk.FetchChannelNames(session.UserId, session);
            if (names.Count > 0)
            {
                _channelNames[deviceKey] = names;
                ChannelPrefs.SetNames(deviceKey, names);
                Log.Info($"OSD 名称同步 {deviceKey}：拿到 {names.Count} 路通道名", Source);
            }
            else
            {
                Log.Info($"OSD 名称同步 {deviceKey}：设备未返回通道名，显示名沿用编号", Source);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"OSD 名称同步 {deviceKey} 异常：{ex.Message}", Source);
        }

        // ② 通道实况扫描
        try
        {
            var scan = App.NetSdk.DetectVideoChannels(session.UserId, session);
            _channelScans[deviceKey] = scan;
            if (scan.Ok)
            {
                Log.Info($"通道实况扫描 {deviceKey}：有画面 {scan.Channels.Count} 路"
                    + (scan.AnalogKnown ? string.Empty : "（本地通道无法判定，按全部列出）")
                    + (scan.Message.Length > 0 ? $" · {scan.Message}" : string.Empty), Source);
            }
            else
            {
                Log.Info($"通道实况扫描 {deviceKey} 未判定，列出全部通道 · {scan.Message}", Source);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"通道实况扫描异常 {deviceKey}：{ex.Message}", Source);
        }
    }

    /// <summary>通道名查询：先看登录时同步的缓存，没有就回退到偏好文件里持久化的那份。</summary>
    private Dictionary<int, string> GetChannelNames(string deviceKey)
    {
        if (_channelNames.TryGetValue(deviceKey, out var names)) return names;

        names = [];
        foreach (var (k, v) in ChannelPrefs.NamesFor(deviceKey))
            if (int.TryParse(k, out var n)) names[n] = v;
        _channelNames[deviceKey] = names;
        return names;
    }

    /// <summary>设备节点状态字：隐藏了多少路要说清楚，否则会被当成"通道数不对"。</summary>
    private string ChannelStateText(LoginSession session, string deviceKey, int visible)
    {
        int total = TotalChannelCount(session);
        if (visible >= total) return $"已登录 · {visible} 路通道";
        return $"已登录 · {visible} 路通道（已隐藏 {total - visible} 路：无画面或通道管理里手动关闭）";
    }

    /// <summary>设备报的总通道数（不过滤时的数量），用来算过滤掉几路。</summary>
    private static int TotalChannelCount(LoginSession session)
    {
        int analog = Math.Min(session.ChannelCount, 64);
        int ip = session.IpChannelCount > 0 && session.StartDigitalChannel > 0
            ? Math.Min(session.IpChannelCount, 128 - analog)
            : 0;
        return analog + ip;
    }

    /// <summary>
    /// 已登录设备的通道列表重建（切换「只列出有画面的通道」时用，不需要重新登录）。
    /// </summary>
    private void RebuildChannelLists()
    {
        foreach (var node in Tree.Where(n => n.IsDevice && n.IsLoggedIn))
        {
            var key = node.Device?.Key;
            if (key == null || !_sessions.TryGetValue(key, out var session) || session is not { Ok: true }) continue;

            // 过滤开关可能刚关掉：此时缓存的扫描结果不该再参与
            if (!FilterEmptyChannels) _channelScans.Remove(key);

            node.Children.Clear();
            foreach (var c in BuildChannels(session, key))
                node.Children.Add(new PreviewTreeNode
                {
                    Device = node.Device,
                    ChannelNumber = c.Number,
                    Label = c.Label,
                    SubLabel = node.Device?.IPv4Address ?? string.Empty,
                    StateText = ChannelIdleText,
                });

            node.StateText = ChannelStateText(session, key, node.Children.Count);
        }
    }

    /// <summary>
    /// 登录后构建通道列表。
    ///
    /// 显示编号从 1 重排：海康 SDK 的固定约定是模拟通道 1~32、数字（IP）通道 33 起，
    /// 纯 IP 通道的设备（如解码器/只有网络摄像机的 NVR）从 33 开始列，现场看着别扭。
    /// 显示层统一重排成 1..N；真实通道号只在内部取流用（通道管理弹窗里可见）。
    /// 显示名优先用 OSD 同步下来的通道名，没有才用编号。
    ///
    /// 过滤两层：
    ///   1. 通道管理手动勾掉的（ChannelPrefs）—— 用户意志，最高优先级，任何开关下都生效；
    ///   2. 自动实况判定（ChannelScanResult）—— 仅在「只列出有画面的通道」勾选时生效，
    ///      且本地（模拟）通道只有在判定过（AnalogKnown）时才过滤，宁可多列不误藏。
    /// 自动过滤后一路不剩 → 判定不可靠，回退成全列；手动全隐藏是用户自己的选择，照办。
    /// </summary>
    private List<PreviewChannelOption> BuildChannels(LoginSession session, string deviceKey)
        => BuildChannels(session, deviceKey, applyFilter: true);

    private List<PreviewChannelOption> BuildChannels(LoginSession session, string deviceKey, bool applyFilter)
    {
        ChannelScanResult? scan = null;
        if (applyFilter && FilterEmptyChannels) _channelScans.TryGetValue(deviceKey, out scan);
        var live = scan is { Ok: true } ? scan.Channels : null;
        bool analogKnown = scan is { Ok: true, AnalogKnown: true };

        var disabled = ChannelPrefs.DisabledChannels(deviceKey);
        var names = GetChannelNames(deviceKey);

        var list = new List<PreviewChannelOption>();

        void Add(int realChannel)
        {
            if (disabled.Contains(realChannel)) return;
            var label = names.TryGetValue(realChannel, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : $"通道 {list.Count + 1}";
            list.Add(new PreviewChannelOption { Number = realChannel, Label = label });
        }

        int analog = session.ChannelCount;
        int start = session.StartChannel > 0 ? session.StartChannel : 1;
        for (int i = 0; i < analog && i < 64; i++)
        {
            int ch = start + i;
            if (live != null && analogKnown && !live.Contains(ch)) continue;
            Add(ch);
        }

        int ipCount = session.IpChannelCount;
        int ipStart = session.StartDigitalChannel;
        if (ipCount > 0 && ipStart > 0)
            for (int i = 0; i < ipCount && list.Count < 128; i++)
            {
                int ch = ipStart + i;
                if (live != null && !live.Contains(ch)) continue;
                Add(ch);
            }

        // 一路不剩：手动全隐藏照办（返回空，树上显示 0 路通道）；
        // 没手动隐藏却是空的 → 自动判定不可靠，回退成不过滤重排
        if (list.Count == 0 && disabled.Count == 0)
        {
            if (applyFilter) return BuildChannels(session, deviceKey, applyFilter: false);

            // 设备连通道数都报 0 的兜底（老异常固件），给一个通道 1 保底能开窗
            list.Add(new PreviewChannelOption { Number = 1, Label = "通道 1" });
        }

        return list;
    }

    // ==================== 通道管理 ====================

    /// <summary>当前选中的设备节点（选中通道节点时自动上溯到它的设备）。</summary>
    private PreviewTreeNode? SelectedDeviceNode => SelectedNode?.IsDevice == true
        ? SelectedNode
        : SelectedNode?.Device?.Key is string key
            ? Tree.FirstOrDefault(n => n.IsDevice &&
                string.Equals(n.Device?.Key, key, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// 打开通道管理弹窗（当前选中设备）：勾选显隐、看缩略图、同步 OSD 名称。
    /// 关闭后重建该设备（以及全部已登录设备）的通道列表。
    /// </summary>
    private void OpenChannelManager()
    {
        var node = SelectedDeviceNode;
        if (node?.Device == null) { Toast("请先在左侧选择一台设备", true); return; }

        if (!_sessions.TryGetValue(node.Device.Key, out var session) || session is not { Ok: true })
        {
            Toast("通道管理需要先登录设备：请先点设备名登录", true);
            return;
        }

        var window = new Views.ChannelManagerWindow(this, node.Device, session);
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner != null && owner != window) window.Owner = owner;
        window.ShowDialog();

        // 勾选/名称可能变了：名称缓存由弹窗清掉，重建所有已登录设备的通道列表（不重新登录）
        RebuildChannelLists();
        StatusText = $"通道管理已保存 · {GridSummary}";
    }

    /// <summary>通道管理弹窗保存后调用：名称缓存作废，下次构建通道时重读偏好文件。</summary>
    internal void ReloadChannelNames() => _channelNames.Clear();

    /// <summary>给通道管理弹窗：设备的全部通道（不过滤，含自动判定为无画面的），附 OSD 名称。</summary>
    internal List<(int number, string name)> AllChannelsForManager(LoginSession session, string deviceKey)
    {
        var result = new List<(int, string)>();
        var names = GetChannelNames(deviceKey);

        void Add(int real)
        {
            var label = names.TryGetValue(real, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : $"通道 {real}";
            result.Add((real, label));
        }

        int analog = session.ChannelCount;
        int start = session.StartChannel > 0 ? session.StartChannel : 1;
        for (int i = 0; i < analog && i < 64; i++) Add(start + i);

        int ipCount = session.IpChannelCount;
        int ipStart = session.StartDigitalChannel;
        if (ipCount > 0 && ipStart > 0)
            for (int i = 0; i < ipCount && result.Count < 128; i++) Add(ipStart + i);

        if (result.Count == 0) Add(1);
        return result;
    }

    /// <summary>给通道管理弹窗：设备实况扫描结果（没有/未判定返回 null）。</summary>
    internal ChannelScanResult? ScanResultFor(string deviceKey)
        => _channelScans.TryGetValue(deviceKey, out var scan) ? scan : null;

    // ==================== 抓图 ====================

    private void CaptureSnapshot()
    {
        var tile = SelectedTile;
        if (tile == null) { Toast("请先点选一路画面", true); return; }

        var device = Tree.FirstOrDefault(n => n.IsDevice &&
            string.Equals(n.Device?.IPv4Address, tile.DeviceIp, StringComparison.OrdinalIgnoreCase))?.Device;
        if (device == null || !_sessions.TryGetValue(device.Key, out var session) || session is not { Ok: true })
        {
            Toast("该画面所在设备已断开，无法抓图", true);
            return;
        }

        int userId = session.UserId;
        int channel = tile.ChannelNumber;
        string serial = device.SerialNo;
        string ip = tile.DeviceIp;
        string title = tile.Title;

        Task.Run(() =>
        {
            var dir = Path.Combine(SnapshotRoot, "preview");
            var stem = SafeFile(string.IsNullOrWhiteSpace(serial) ? ip : serial);
            var name = $"{stem}_ch{channel}_{DateTime.Now:HHmmss}.jpg";
            var file = Path.Combine(dir, name);
            var (ok, msg, bytes) = App.NetSdk.CaptureJpeg(userId, channel, file);
            UiInvoke(() =>
            {
                if (ok)
                {
                    SnapshotCount++;
                    Toast($"已抓图存证：{name}（{bytes / 1024} KB）");
                    Log.Info($"预览抓图 {title} → {file}", Source);
                }
                else
                {
                    Toast($"抓图失败：{msg}", true);
                    Log.Warn($"预览抓图失败（{title}）：{msg}", Source);
                }
            });
        });
    }

    private void OpenSnapshotFolder()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(SnapshotRoot, "preview"));
            EnvironmentCheck.OpenFolder(Path.Combine(SnapshotRoot, "preview"));
        }
        catch (Exception ex)
        {
            Toast($"打开目录失败：{ex.Message}", true);
        }
    }

    private static string SnapshotRoot
        => Path.Combine(string.IsNullOrWhiteSpace(App.Settings.OutputDir) ? AppPaths.OutputDir : App.Settings.OutputDir, "snapshots");

    private static string SafeFile(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    /// <summary>把动作投递到 UI 线程（已在 UI 线程时直接执行）。</summary>
    private static void UiInvoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    private static LogService Log => App.Log;
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HikDeployTool.Models;
using HikDeployTool.Native;
using HikDeployTool.Services;
using HikDeployTool.ViewModels;

namespace HikDeployTool.Views;

/// <summary>
/// 通道管理弹窗：逐通道缩略图 + 显示开关 + OSD 名称。
///
/// 线程模型：抓图循环在后台线程逐路执行（设备端一秒左右一路，几十路要一小会儿，
/// 弹窗必须保持可交互——用户可能等不及就先勾选保存了）；所有 UI 更新回 UI 线程。
/// 关窗即取消循环（CTS），避免后台还在往已关掉的窗口投递。
/// </summary>
public partial class ChannelManagerWindow : Window
{
    /// <summary>一张通道卡。</summary>
    public sealed class ChannelItem : ObservableObject
    {
        public int Number { get; init; }

        /// <summary>SDK 真实通道号（取流内部用；显示编号已重排，见 PreviewViewModel.BuildChannels）。</summary>
        public string ChannelText => $"真实通道 {Number}";

        private string _name = string.Empty;
        public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(HoverText)); } }

        private bool _enabled = true;
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        private string _statusText = "排队抓图…";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        private BitmapSource? _thumb;
        public BitmapSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }

        /// <summary>悬停放大预览的说明行。</summary>
        public string HoverText => $"{Name} · 真实通道 {Number}";
    }

    public ObservableCollection<ChannelItem> Items { get; } = [];

    private readonly PreviewViewModel _vm;
    private readonly DiscoveredDevice _device;
    private readonly LoginSession _session;
    private readonly string _deviceKey;
    private CancellationTokenSource? _captureCts;
    private int _lastEnabledCount;

    public ChannelManagerWindow(PreviewViewModel vm, DiscoveredDevice device, LoginSession session)
    {
        InitializeComponent();
        DataContext = this;

        _vm = vm;
        _device = device;
        _session = session;
        _deviceKey = device.Key;

        Title = $"通道管理 · {device.IPv4Address}{(string.IsNullOrWhiteSpace(device.ModelText) ? string.Empty : $" · {device.ModelText}")}";
        TxtTitle.Text = Title;

        BuildItems();
        StartOsdSync();
        StartThumbLoop(force: false);
    }

    /// <summary>
    /// 打开时自动补拉一次 OSD 名称：登录那一刻设备可能没响应（或登录早于同步逻辑），
    /// 弹窗里有活动会话，这里再给一次机会。拿到的名字直接改卡片并落盘，
    /// 用户不点保存也不丢。
    /// </summary>
    private async void StartOsdSync()
    {
        SetProgress("正在同步 OSD 名称…");
        var names = await Task.Run(
            () => AppState.Current.NetSdk.FetchChannelNames(_session.UserId, _session)).ConfigureAwait(true);

        if (names.Count == 0)
        {
            SetProgress("OSD 名称同步失败：设备未返回通道名（可稍后点「同步OSD名称」重试，或手动填写）");
            return;
        }

        foreach (var item in Items)
            if (names.TryGetValue(item.Number, out var name) && !string.IsNullOrWhiteSpace(name))
                item.Name = name;

        ChannelPrefs.SetNames(_deviceKey, names);
        _vm.ReloadChannelNames();
        SetProgress($"OSD 名称已同步：拿到 {names.Count} 路（若仍显示数字，说明设备端通道名本身没配，可在 NVR 上配置后重试）");
    }

    private void OnSyncOsd(object sender, RoutedEventArgs e) => StartOsdSync();

    private void BuildItems()
    {
        var disabled = ChannelPrefs.DisabledChannels(_deviceKey);
        var scan = _vm.ScanResultFor(_deviceKey);
        var knownOnline = scan is { Ok: true } ? scan.Channels : null;

        foreach (var (number, name) in _vm.AllChannelsForManager(_session, _deviceKey))
        {
            var item = new ChannelItem { Number = number, Name = name, Enabled = !disabled.Contains(number) };

            // 自动判定的状态先标上（抓图成功后会被缩略图覆盖成更确凿的结论）
            if (knownOnline != null)
                item.StatusText = knownOnline.Contains(number) ? "自动判定：有画面" : "自动判定：无画面";

            Items.Add(item);
        }

        _lastEnabledCount = Items.Count(i => i.Enabled);
        UpdateSummary();
    }

    // ==================== 缩略图 ====================

    private string ThumbPath(int channel)
        => Path.Combine(AppPaths.ThumbDir, $"{SafeFile(_deviceKey)}_ch{channel}.jpg");

    private async void StartThumbLoop(bool force)
    {
        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;

        int done = 0;
        int total = Items.Count;
        SetProgress($"缩略图 0/{total}");

        foreach (var item in Items)
        {
            if (token.IsCancellationRequested) return;

            var path = ThumbPath(item.Number);
            bool hasCache = File.Exists(path);

            if (force || !hasCache)
            {
                SetItemStatus(item, "抓图中…");
                var (ok, msg, _) = await Task.Run(
                    () => AppState.Current.NetSdk.CaptureJpeg(_session.UserId, item.Number, path,
                        picQuality: NetSdkConst.PIC_QUALITY_LOW),
                    token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                if (!ok)
                {
                    SetItemStatus(item, File.Exists(path) ? string.Empty : "无画面 / 抓图失败");
                    LogService.Current.Info($"通道缩略图 ch{item.Number}：{msg}", "通道管理");
                }
            }

            if (token.IsCancellationRequested) return;

            if (File.Exists(path))
            {
                SetItemStatus(item, string.Empty);
                LoadThumb(item, path);
            }

            done++;
            SetProgress($"缩略图 {done}/{total}");
        }
    }

    private void LoadThumb(ChannelItem item, string path)
    {
        try
        {
            // OnLoad + 新实例：文件可能被"刷新缩略图"重写过，必须绕开 URI 级缓存
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            SetThumb(item, bmp);
        }
        catch
        {
            // 缩略图坏了不算事：留状态字提示即可
        }
    }

    private void SetThumb(ChannelItem item, BitmapSource? thumb)
        => Dispatcher.Invoke(() => item.Thumb = thumb);

    private void SetItemStatus(ChannelItem item, string text)
        => Dispatcher.Invoke(() => item.StatusText = text);

    private void SetProgress(string text)
        => Dispatcher.Invoke(() => TxtProgress.Text = text);

    private static string SafeFile(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    // ==================== 按钮 ====================

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);
    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool enabled)
    {
        foreach (var item in Items) item.Enabled = enabled;
        UpdateSummary();
    }

    private void OnRefreshThumbs(object sender, RoutedEventArgs e) => StartThumbLoop(force: true);

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void OnSaveAndClose(object sender, RoutedEventArgs e)
    {
        Save();
        DialogResult = true;
        Close();
    }

    private void Save()
    {
        ChannelPrefs.SetDisabled(_deviceKey, Items.Where(i => !i.Enabled).Select(i => i.Number));
        ChannelPrefs.SetNames(_deviceKey, Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .ToDictionary(i => i.Number, i => i.Name));

        // 名称写回了偏好文件：让预览页的内存缓存作废，重建树时重新读
        _vm.ReloadChannelNames();

        _lastEnabledCount = Items.Count(i => i.Enabled);
        UpdateSummary();
        LogService.Current.Info(
            $"通道管理保存 {_device.IPv4Address}：启用 {_lastEnabledCount}/{Items.Count} 路", "通道管理");
    }

    private void UpdateSummary()
        => TxtSummary.Text = $"已启用 {Items.Count(i => i.Enabled)} / 共 {Items.Count} 路（取消勾选的通道不在预览树显示）";

    protected override void OnClosed(EventArgs e)
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;
        base.OnClosed(e);
    }
}

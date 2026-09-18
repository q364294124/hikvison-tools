using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using HikDeployTool.Models;
using HikDeployTool.ViewModels;

namespace HikDeployTool.Services;

/// <summary>
/// 应用级共享状态。
///
/// 之所以做成单例而不是层层传参：设备列表、设置、原生服务这三样东西
/// 是全部页面共享的，且原生回调来自 SDK 自己的线程，需要一个统一的地方
/// 做"跨线程 → UI 线程"的转换和去重合并。
/// </summary>
public sealed class AppState
{
    private readonly Dictionary<string, DiscoveredDevice> _index = new(StringComparer.OrdinalIgnoreCase);
    private int _assetSeq;

    public static AppState Current { get; } = new();

    private AppState()
    {
        Log = LogService.Current;
        Sadp = new SadpService();
        NetSdk = new NetSdkService();

        Sadp.DeviceFound += OnDeviceFound;
        Sadp.Trace += msg => Log.Warn(msg, "SADP");
    }

    public LogService Log { get; }

    public SadpService Sadp { get; }

    public NetSdkService NetSdk { get; }

    public AppSettings Settings { get; private set; } = new();

    /// <summary>发现到的设备（界面直接绑定）。</summary>
    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];

    /// <summary>资产台账。</summary>
    public ObservableCollection<AssetRecord> Assets { get; } = [];

    /// <summary>设备列表发生结构性变化（新增/清空）时触发。</summary>
    public event Action? DevicesChanged;

    /// <summary>设置了变化（用于设置页回填）。</summary>
    public event Action? SettingsChanged;

    private static Dispatcher UiDispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    // ==================== 生命周期 ====================

    /// <summary>加载配置与台账，并确保输出目录存在。</summary>
    public void Load()
    {
        AppPaths.EnsureCreated();

        var settings = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        if (settings != null) Settings = settings;
        if (string.IsNullOrWhiteSpace(Settings.SdkLogDir)) Settings.SdkLogDir = AppPaths.SdkLogDirDefault;
        if (string.IsNullOrWhiteSpace(Settings.OutputDir)) Settings.OutputDir = AppPaths.OutputDir;

        var assets = JsonStore.Load<List<AssetRecord>>(AppPaths.AssetsFile);
        if (assets != null)
        {
            Assets.ReplaceAll(assets);
            _assetSeq = assets
                .Select(a => int.TryParse(a.Id.Replace("HJ-", ""), out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();
        }

        Log.Info($"已加载配置，历史台账 {Assets.Count} 条", "启动");
    }

    public void SaveSettings()
    {
        JsonStore.Save(AppPaths.SettingsFile, Settings);
        SettingsChanged?.Invoke();
    }

    public void SaveAssets()
    {
        JsonStore.Save(AppPaths.AssetsFile, Assets.ToList());
    }

    public void ApplySdkLogSettings()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Settings.SdkLogDir))
            {
                Directory.CreateDirectory(Settings.SdkLogDir);
                Sadp.SetSdkLogFile(Settings.SdkLogLevel, Settings.SdkLogDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"应用 SDK 日志设置失败：{ex.Message}", "设置");
        }
    }

    // ==================== 设备列表 ====================

    /// <summary>原生回调入口：合并设备信息并投递到 UI 线程。</summary>
    private void OnDeviceFound(DiscoveredDevice device)
    {
        var dispatcher = UiDispatcher;

        if (dispatcher.CheckAccess())
        {
            MergeDevice(device);
            return;
        }

        // 用 BeginInvoke 而不是 Invoke：SADP 回调很密集，同步等待 UI 线程会拖慢搜索
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => MergeDevice(device)));
        }
        catch (Exception ex)
        {
            Log.Warn($"投递设备信息到界面失败：{ex.Message}", "SADP");
        }
    }

    private void MergeDevice(DiscoveredDevice device)
    {
        string key = device.Key;
        if (string.IsNullOrWhiteSpace(key)) return;

        if (_index.TryGetValue(key, out var existing))
        {
            existing.CopyFrom(device);
            return;
        }

        _index[key] = device;
        Devices.Add(device);
        DevicesChanged?.Invoke();
    }

    /// <summary>清空发现列表（同时清掉 SDK 内部缓存，避免旧设备被再次回放）。</summary>
    public void ClearDevices(bool clearSdkCache = false)
    {
        Devices.Clear();
        _index.Clear();
        if (clearSdkCache && Sadp.IsRunning)
        {
            try
            {
                Sadp.Clearup();
                Sadp.SendInquiry();
            }
            catch (Exception ex)
            {
                Log.Warn($"刷新 SDK 缓存失败：{ex.Message}", "SADP");
            }
        }
        DevicesChanged?.Invoke();
    }

    public IReadOnlyList<DiscoveredDevice> SelectedDevices()
        => Devices.Where(d => d.IsSelected).ToList();

    public DiscoveredDevice? FindByKey(string key)
        => _index.TryGetValue(key, out var d) ? d : null;

    /// <summary>
    /// 把设备合并进台账（按序列号去重）。返回 (新增数, 更新数)。
    /// verified=true 表示这批设备已逐台登录验证过凭据，台账里记上标记。
    /// </summary>
    public (int added, int updated) MergeIntoAssets(IEnumerable<DiscoveredDevice> devices, string? location = null,
        bool verified = false, string? userName = null)
    {
        int added = 0, updated = 0;
        foreach (var d in devices)
        {
            var key = d.Key;
            if (string.IsNullOrWhiteSpace(key)) continue;

            var exist = Assets.FirstOrDefault(a =>
                (!string.IsNullOrWhiteSpace(a.SerialNo) && a.SerialNo.Equals(d.SerialNo, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(a.Mac) && a.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase)));

            if (exist != null)
            {
                exist.UpdateFrom(d);
                if (!string.IsNullOrWhiteSpace(location)) exist.Location = location;
                if (verified) exist.Verified = $"已验证 {DateTime.Now:MM-dd HH:mm}";
                if (!string.IsNullOrWhiteSpace(userName)) exist.AdminUserName = userName;
                updated++;
            }
            else
            {
                var record = AssetRecord.FromDevice(d, NextAssetId());
                if (!string.IsNullOrWhiteSpace(location)) record.Location = location;
                if (verified) record.Verified = $"已验证 {DateTime.Now:MM-dd HH:mm}";
                if (!string.IsNullOrWhiteSpace(userName)) record.AdminUserName = userName;
                // 名称默认用型号；已激活设备补上 IP 便于识别
                Assets.Add(record);
                added++;
            }
        }
        if (added > 0 || updated > 0) SaveAssets();
        return (added, updated);
    }

    public string NextAssetId()
    {
        _assetSeq++;
        return $"HJ-{_assetSeq:D4}";
    }

    public void ReindexAssetSequence()
    {
        _assetSeq = Assets
            .Select(a => int.TryParse(a.Id.Replace("HJ-", ""), out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
    }

    /// <summary>整个应用退出时的清理。</summary>
    public void Shutdown()
    {
        try { SaveSettings(); } catch { /* 忽略 */ }
        try { SaveAssets(); } catch { /* 忽略 */ }
        try { Sadp.Dispose(); } catch { /* 忽略 */ }
        try { NetSdk.Dispose(); } catch { /* 忽略 */ }
    }
}

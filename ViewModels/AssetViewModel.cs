using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>
/// 资产台账页。
///
/// 台账是交付里最容易被忽略、又最容易被追责的东西：
/// 半年后甲方问"3 号楼 2 层东侧那台是哪台、IP 多少、什么版本"，
/// 现场翻不出记录就只能重新上梯子爬一遍。
/// 所以这里把"发现即入库"做成一步，并允许填写位置/备注，最后导出 CSV 交甲方。
/// </summary>
public sealed class AssetViewModel : PageViewModel
{
    private string _keyword = string.Empty;
    private AssetRecord? _selected;

    public AssetViewModel()
    {
        View = CollectionViewSource.GetDefaultView(App.Assets);
        View.Filter = FilterAsset;

        ImportFromDevicesCommand = new RelayCommand(ImportFromDevices);
        AddManualCommand = new RelayCommand(AddManual);
        EditSelectedCommand = new RelayCommand(EditSelected, () => Selected != null);
        ProbeSelectedCommand = new AsyncRelayCommand(ProbeSelectedAsync, () => Selected != null);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => Selected != null);
        DeleteAllCommand = new RelayCommand(DeleteAll);
        ExportCommand = new RelayCommand(Export);
        ImportCommand = new RelayCommand(Import);
        SyncCommand = new RelayCommand(SyncFromDevices);
        OpenOutputCommand = new RelayCommand(() => EnvironmentCheck.OpenFolder(OutputDir));

        App.Assets.CollectionChanged += (_, _) => NotifyStats();
        NotifyStats();
    }

    public ObservableCollection<AssetRecord> Assets => App.Assets;

    public ICollectionView View { get; }

    public override string Title => "资产台账";

    public override string Glyph => "IconList";

    public override string Description => "维护设备清单，支持编辑名称与安装位置、导出 CSV 交给甲方";

    public override string Usage => "把搜索到的设备一键入库，再补上「安装位置」和自定义名称，导出即为交付清单。「检测」可刷新选中设备的在线/激活状态与固件信息。";

    public RelayCommand ImportFromDevicesCommand { get; }

    public RelayCommand AddManualCommand { get; }

    public RelayCommand EditSelectedCommand { get; }

    public AsyncRelayCommand ProbeSelectedCommand { get; }

    public RelayCommand DeleteSelectedCommand { get; }

    public RelayCommand DeleteAllCommand { get; }

    public RelayCommand ExportCommand { get; }

    public RelayCommand ImportCommand { get; }

    public RelayCommand SyncCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    // ==================== 过滤与统计 ====================

    public string Keyword
    {
        get => _keyword;
        set
        {
            if (!Set(ref _keyword, value)) return;
            View.Refresh();
        }
    }

    public AssetRecord? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            DeleteSelectedCommand.RaiseCanExecuteChanged();
            EditSelectedCommand.RaiseCanExecuteChanged();
            ProbeSelectedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected != null;

    private bool FilterAsset(object obj)
    {
        if (obj is not AssetRecord a) return false;
        if (string.IsNullOrWhiteSpace(Keyword)) return true;

        var k = Keyword.Trim();
        return Contains(a.Id) || Contains(a.Name) || Contains(a.Ip) || Contains(a.SerialNo)
            || Contains(a.Mac) || Contains(a.Location) || Contains(a.Model) || Contains(a.Note);

        bool Contains(string? src) => !string.IsNullOrEmpty(src) && src.Contains(k, StringComparison.OrdinalIgnoreCase);
    }

    public int TotalCount => Assets.Count;

    public int FilteredCount => View.Cast<object>().Count();

    public int ActivatedCount => Assets.Count(a => a.ActivateState == "已激活");

    public int OnlineCount => Assets.Count(a => a.Online == "在线");

    public int LocatedCount => Assets.Count(a => !string.IsNullOrWhiteSpace(a.Location));

    public string StatsText => Assets.Count == 0
        ? "台账为空"
        : $"共 {TotalCount} 条 · 已激活 {ActivatedCount} · 最近在线 {OnlineCount} · 已填位置 {LocatedCount}"
          + (string.IsNullOrWhiteSpace(Keyword) ? string.Empty : $" · 当前筛选 {FilteredCount} 条");

    private void NotifyStats()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActivatedCount));
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(LocatedCount));
        OnPropertyChanged(nameof(StatsText));
        View.Refresh();
    }

    // ==================== 维护 ====================

    private void ImportFromDevices()
    {
        var picked = App.SelectedDevices();
        var source = picked.Count > 0 ? (IReadOnlyList<DiscoveredDevice>)picked : App.Devices.ToList();
        if (source.Count == 0)
        {
            Toast("设备列表为空，请先在「设备发现」页搜索设备", true);
            return;
        }

        var (added, updated) = App.MergeIntoAssets(source);
        NotifyStats();
        Log.Success($"台账入库：新增 {added} 条，更新 {updated} 条", "台账");
        Toast($"已入库：新增 {added} 条，更新 {updated} 条");
    }

    /// <summary>
    /// 手工新增：弹独立窗口（对标海康平台端的「添加设备」），支持
    /// IP/域名单台、IP 段、批量文本三种模式，可选逐台验证凭据后入库。
    /// 关窗后统一刷新统计并选中新加的记录。
    /// </summary>
    private void AddManual()
    {
        var dlg = new Views.AssetAddWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;

        NotifyStats();
        if (dlg.LastAdded != null) Selected = dlg.LastAdded;
        Toast(dlg.ResultMessage);
    }

    /// <summary>
    /// 编辑选中的台账记录：弹窗改名称/位置/备注，也允许修正 IP/端口/凭据/
    /// 序列号。保存后统一落盘并刷新统计。
    /// </summary>
    private void EditSelected()
    {
        if (Selected == null) return;
        var dlg = new Views.AssetEditWindow(Selected)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (dlg.ShowDialog() != true) return;

        App.SaveAssets();
        NotifyStats();
        Toast("已保存修改");
    }

    /// <summary>
    /// 检测选中的设备（刷新动态信息）：
    /// ① TCP 探测 SDK 端口 → 在线/离线 + 最近发现；
    /// ② 在线且台账存有口令 → 真登录一次：成功则激活状态实锤「已激活」、
    ///    刷新「已验证」标记，并顺手从 deviceInfo 回填序列号/MAC/型号/固件；
    ///    失败说明口令已经变了，明确告诉用户（不猜状态）；
    /// ③ 在线但没存口令 → 只能测到"端口通"，激活状态需要先验证登录。
    /// </summary>
    private async System.Threading.Tasks.Task ProbeSelectedAsync()
    {
        var rec = Selected;
        if (rec == null) return;

        string ip = rec.Ip;
        ushort port = rec.Port;
        string user = rec.AdminUserName;
        string password = rec.Password;   // 解不开返回空串，平滑降级为"无凭据"
        bool hasCred = password.Length > 0 && !string.IsNullOrWhiteSpace(user);

        bool tcpOk = false, loginOk = false;
        string loginMsg = string.Empty;
        string serial = string.Empty, mac = string.Empty, model = string.Empty, fw = string.Empty;

        using (Busy($"正在检测 {ip}:{port} …"))
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                var state = Services.AppState.Current;
                tcpOk = NetSdkService.CheckTcp(ip, port, 2000).ok;
                if (!tcpOk || !hasCred) return;

                var session = state.NetSdk.Login(ip, port, user, password);
                if (!session.Ok)
                {
                    loginMsg = session.Message;
                    return;
                }

                loginOk = true;
                (serial, mac, model, fw) = Views.AssetAddWindow.FetchDeviceInfo(state, session.UserId);
                state.NetSdk.Logout(session.UserId);   // 只验明正身，会话不保留
            });
        }

        if (tcpOk)
        {
            rec.Online = "在线";
            rec.LastSeen = DateTime.Now;
            if (loginOk)
            {
                rec.ActivateState = "已激活";
                rec.Verified = $"已验证 {DateTime.Now:MM-dd HH:mm}";
                // deviceInfo 抓到的实况回填（空值不覆盖已有信息，与手工添加同一口径）
                if (serial.Length > 0) rec.SerialNo = serial;
                if (mac.Length > 0) rec.Mac = mac;
                if (model.Length > 0) rec.Model = model;
                if (fw.Length > 0) rec.FirmwareVersion = fw;
            }
        }
        else
        {
            rec.Online = "离线";
        }

        App.SaveAssets();
        NotifyStats();

        if (!tcpOk)
        {
            Log.Warn($"台账检测：{ip}:{port} 离线（SDK 端口不通）", "台账");
            Toast($"检测完成：{ip} 离线（端口 {port} 不通）", true);
        }
        else if (loginOk)
        {
            Log.Info($"台账检测：{ip} 在线，凭据有效，激活状态已确认（已激活）", "台账");
            Toast($"检测完成：在线 · 凭据有效 · 已激活（实况信息已刷新）");
        }
        else if (hasCred)
        {
            Log.Warn($"台账检测：{ip} 在线，但口令登录失败：{loginMsg}", "台账");
            Toast($"检测完成：在线，但口令登录失败：{loginMsg}", true);
        }
        else
        {
            Log.Info($"台账检测：{ip} 端口通；未存登录口令，激活状态需先验证登录", "台账");
            Toast("检测完成：端口通（未存口令，无法确认激活状态；点「编辑」补入口令后再检测）");
        }
    }

    private void DeleteSelected()
    {
        if (Selected == null) return;
        var target = Selected;
        var ok = MessageBox.Show(
            $"确定从台账中删除这条记录吗？\n\n{target.Id}  {target.Name}\n{target.Ip}\n\n该操作不可撤销。",
            "删除确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!ok) return;

        Assets.Remove(target);
        App.SaveAssets();
        NotifyStats();
        Selected = null;
        Toast("已删除 1 条记录");
    }

    private void DeleteAll()
    {
        if (Assets.Count == 0) { Toast("台账已经是空的", true); return; }

        var ok = MessageBox.Show(
            $"确定清空全部 {Assets.Count} 条台账记录吗？\n\n建议先「导出 CSV」备份，该操作不可撤销。",
            "清空台账", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!ok) return;

        Assets.Clear();
        App.ReindexAssetSequence();
        App.SaveAssets();
        NotifyStats();
        Toast("台账已清空");
    }

    private static string OutputDir => string.IsNullOrWhiteSpace(App.Settings.OutputDir) ? AppPaths.OutputDir : App.Settings.OutputDir;

    private void Export()
    {
        if (Assets.Count == 0) { Toast("台账为空，没有可导出的内容", true); return; }
        try
        {
            Directory.CreateDirectory(OutputDir);
            var name = string.IsNullOrWhiteSpace(App.Settings.ProjectName) ? "资产台账" : $"{App.Settings.ProjectName}_资产台账";
            var file = Path.Combine(OutputDir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            Exporter.ExportAssets(Assets, file);
            Toast($"台账已导出：{Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Log.Error("导出台账失败", ex, "台账");
            Toast($"导出失败：{ex.Message}", true);
        }
    }

    private void Import()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入的台账 CSV",
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            var (count, message) = Exporter.ImportAssets(dlg.FileName);
            NotifyStats();
            Toast(count > 0 ? $"导入完成：{message}" : $"导入失败：{message}", count == 0);
        }
        catch (Exception ex)
        {
            Log.Error("导入台账失败", ex, "台账");
            Toast($"导入失败：{ex.Message}", true);
        }
    }

    /// <summary>用当前搜索结果刷新台账的动态字段（IP 变了、版本升级了都能同步）。</summary>
    private void SyncFromDevices()
    {
        if (Assets.Count == 0) { Toast("台账为空，无需同步", true); return; }
        if (App.Devices.Count == 0) { Toast("设备列表为空，请先搜索设备", true); return; }

        int updated = 0;
        foreach (var a in Assets)
        {
            var d = App.Devices.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(a.SerialNo) && a.SerialNo.Equals(x.SerialNo, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(a.Mac) && a.Mac.Equals(x.Mac, StringComparison.OrdinalIgnoreCase)));
            if (d == null) continue;
            a.UpdateFrom(d);
            updated++;
        }

        int offline = Assets.Count - updated;
        App.SaveAssets();
        NotifyStats();
        Log.Info($"台账同步完成：更新 {updated} 条，{offline} 条未在本次搜索中出现", "台账");
        Toast($"已同步 {updated} 条记录（{offline} 条本次未搜索到）");
    }

    public override void OnEnter() => NotifyStats();

    private static LogService Log => App.Log;
}

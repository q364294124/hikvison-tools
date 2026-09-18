using System.Net;
using System.Windows;
using System.Windows.Controls;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.Views;

/// <summary>
/// 台账「手工新增」弹窗（对标海康平台端的「添加设备」窗口）。
///
/// 三种模式：
///   · IP / 域名 —— 单台补录，地址可以是 IP 也可以是域名；
///   · IP 段     —— 同一 C 段批量补录（起始 ~ 结束，最多 254 台）；
///   · 批量导入  —— 文本粘贴，一行一台：地址[,端口[,用户名[,密码[,名称]]]]。
///
/// 勾选「验证登录凭据」时逐台试登录（与设备发现页"验证并入库"同一条链路）：
/// 通过的记「已验证」、口令 DPAPI 加密落台账，预览页就能免密直连。
/// 批量场景验证失败的跳过不入库；单台验证失败给一次"仍要添加"的机会。
///
/// 为什么不再走老的"新增一条空白记录"：空行记录没人补得全，半年后台账里
/// 全是「手工录入设备」，起不到交付清单的作用。
/// </summary>
public partial class AssetAddWindow : Window
{
    /// <summary>本次弹窗最后新增的一条记录（供调用方选中高亮）。</summary>
    public AssetRecord? LastAdded { get; private set; }

    /// <summary>关闭前写入的结果摘要（新增/更新/跳过数量）。</summary>
    public string ResultMessage { get; private set; } = string.Empty;

    private bool _busy;

    public AssetAddWindow()
    {
        InitializeComponent();
    }

    private enum Mode { Single, Range, Batch }

    private Mode CurrentMode => ModeSingle.IsChecked == true ? Mode.Single
        : ModeRange.IsChecked == true ? Mode.Range : Mode.Batch;

    /// <summary>一条待入库的目标设备。Serial/Mac/Model/Firmware 由验证登录时从设备实况抓取。</summary>
    private sealed record AddTarget(
        string Ip, ushort Port, string User, string Password, string Name,
        string Serial = "", string Mac = "", string Model = "", string Firmware = "");

    // ==================== 模式切换 ====================

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 中途 ModeSingle 的 IsChecked="True" 就会触发一次，
        // 此时后面的面板还没建出来，直接跳过
        if (PanelSingle is null || PanelRange is null || PanelBatch is null) return;

        PanelSingle.Visibility = CurrentMode == Mode.Single ? Visibility.Visible : Visibility.Collapsed;
        PanelRange.Visibility = CurrentMode == Mode.Range ? Visibility.Visible : Visibility.Collapsed;
        PanelBatch.Visibility = CurrentMode == Mode.Batch ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 按钮入口 ====================

    private void OnAddClick(object sender, RoutedEventArgs e) => _ = AddCoreAsync(keepOpen: false);

    private void OnAddAndContinueClick(object sender, RoutedEventArgs e) => _ = AddCoreAsync(keepOpen: true);

    private async System.Threading.Tasks.Task AddCoreAsync(bool keepOpen)
    {
        if (_busy) return;
        var state = AppState.Current;

        List<AddTarget> targets;
        try
        {
            targets = CollectTargets();
        }
        catch (InvalidOperationException ex)
        {
            Warn(ex.Message);
            return;
        }

        if (targets.Count == 0)
        {
            Warn("没有可添加的设备，请检查输入");
            return;
        }

        bool verify = ChkVerify.IsChecked == true;
        var passed = new List<AddTarget>();
        var failures = new List<string>();
        var okIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 验证通过的 IP（决定是否记「已验证」）

        SetBusy(true);

        if (verify)
        {
            int i = 0;
            foreach (var t in targets)
            {
                i++;
                ShowStatus($"正在验证 {i}/{targets.Count} · {t.Ip}:{t.Port} …");
                var target = t;
                var (ok, msg, serial, mac, model, fw) = await System.Threading.Tasks.Task.Run(() =>
                {
                    var session = state.NetSdk.Login(target.Ip, target.Port, target.User, target.Password);
                    if (!session.Ok) return (false, session.Message, string.Empty, string.Empty, string.Empty, string.Empty);

                    // 顺手把序列号/MAC/型号/固件从设备抓回来 —— 手工补录的设备
                    // 没有发现广播可听，这些参数只有登录后问设备本身才拿得到
                    var info = FetchDeviceInfo(state, session.UserId);

                    // 验证完就登出：预览页用时会自己登录，这里只验明凭据
                    state.NetSdk.Logout(session.UserId);
                    return (true, string.Empty, info.serial, info.mac, info.model, info.firmware);
                }).ConfigureAwait(true);

                if (ok)
                {
                    passed.Add(target with { Serial = serial, Mac = mac, Model = model, Firmware = fw });
                    okIps.Add(t.Ip);
                }
                else
                {
                    failures.Add($"{t.Ip}:{t.Port}（{msg}）");
                }
            }
        }
        else
        {
            passed = targets;
        }

        // 单台验证失败：给一次"仍要添加"的机会（不记「已验证」，口令照存）
        if (verify && passed.Count == 0 && targets.Count == 1)
        {
            SetBusy(false);
            var again = MessageBox.Show(this,
                $"凭据验证失败：{failures[0]}\n\n仍要把这台设备加入台账吗？\n（不记「已验证」，之后可在设备发现页重新验证）",
                "验证失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (again != MessageBoxResult.Yes) return;
            passed = targets;
        }

        if (passed.Count == 0)
        {
            SetBusy(false);
            Warn($"全部验证失败，未入库：\n{string.Join("\n", failures)}");
            state.Log.Warn($"台账手工添加：{targets.Count} 台全部验证失败", "台账");
            return;
        }

        // ---- 入库（同 IP 已存在的做更新，不重复造行） ----
        int added = 0, updated = 0;
        foreach (var t in passed)
        {
            var exist = state.Assets.FirstOrDefault(a =>
                string.Equals(a.Ip, t.Ip, StringComparison.OrdinalIgnoreCase));

            if (exist != null)
            {
                exist.Port = t.Port;
                if (!string.IsNullOrWhiteSpace(t.User)) exist.AdminUserName = t.User;
                if (t.Password.Length > 0) exist.Password = t.Password;   // DPAPI 加密
                if (okIps.Contains(t.Ip)) exist.Verified = $"已验证 {DateTime.Now:MM-dd HH:mm}";
                // 设备实况抓到的参数补进台账（空则不动，别抹掉已有信息）
                if (t.Serial.Length > 0) exist.SerialNo = t.Serial;
                if (t.Mac.Length > 0) exist.Mac = t.Mac;
                if (t.Model.Length > 0) exist.Model = t.Model;
                if (t.Firmware.Length > 0) exist.FirmwareVersion = t.Firmware;
                updated++;
                continue;
            }

            var rec = new AssetRecord
            {
                Id = state.NextAssetId(),
                Name = t.Name.Length > 0 ? t.Name : (t.Model.Length > 0 ? t.Model : "手工录入设备"),
                Ip = t.Ip,
                Port = t.Port,
                AdminUserName = t.User,
                SerialNo = t.Serial,
                Mac = t.Mac,
                Model = t.Model,
                FirmwareVersion = t.Firmware,
                Online = "未知",
                ActivateState = "未知",
                Acceptance = "未验收",
            };
            if (t.Password.Length > 0) rec.Password = t.Password;
            if (okIps.Contains(t.Ip)) rec.Verified = $"已验证 {DateTime.Now:MM-dd HH:mm}";
            state.Assets.Add(rec);
            LastAdded ??= rec;
            added++;
        }

        // ---- 在线状态实测 ----
        // 手工补录的设备多半是刚上架 / 刚接线的，台账里挂一排"未知"看不出哪台还没通。
        // 验证过凭据的直接算在线（登录都通了）；其余按 SDK 端口做一次 TCP 探测。
        int online = 0;
        foreach (var t in passed)
        {
            var rec = state.Assets.FirstOrDefault(a =>
                string.Equals(a.Ip, t.Ip, StringComparison.OrdinalIgnoreCase));
            if (rec == null) continue;

            bool isOnline = okIps.Contains(t.Ip);
            if (!isOnline)
            {
                ShowStatus($"正在检测在线状态 · {t.Ip}:{t.Port} …");
                var target = t;
                isOnline = await System.Threading.Tasks.Task.Run(
                    () => NetSdkService.CheckTcp(target.Ip, target.Port, 1500).ok);
            }

            rec.Online = isOnline ? "在线" : "离线";

            // 能用口令登录成功 = 设备必然已激活（未激活设备拒绝登录）。
            // 手工补录的设备没有 SADP 广播可听，激活状态只能靠这一步实锤，
            // 之前写死"未知"就是"激活状态获取不到"的根源。
            if (okIps.Contains(t.Ip)) rec.ActivateState = "已激活";

            if (isOnline)
            {
                rec.LastSeen = DateTime.Now;   // 只有真连上了才更新「最近发现」
                online++;
            }
        }

        state.SaveAssets();
        state.Log.Info($"台账手工添加：新增 {added} 条，更新 {updated} 条"
            + $"，在线 {online}/{passed.Count}"
            + (failures.Count > 0 ? $"，验证失败跳过 {failures.Count} 台" : string.Empty), "台账");

        ResultMessage = $"新增 {added} 条，更新 {updated} 条 · 在线 {online}／{passed.Count}"
            + (failures.Count > 0 ? $" · {failures.Count} 台验证失败未入库" : string.Empty);

        if (keepOpen)
        {
            // 清空输入，继续录下一台
            TxtName.Text = string.Empty;
            TxtAddress.Text = string.Empty;
            PwdSingle.Clear();
            TxtStartIp.Text = string.Empty;
            TxtEndIp.Text = string.Empty;
            PwdRange.Clear();
            TxtBatch.Text = string.Empty;

            SetBusy(false);
            ShowStatus(ResultMessage, okColor: true);
            TxtAddress.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }

    // ==================== 输入收集与校验 ====================

    private List<AddTarget> CollectTargets()
    {
        return CurrentMode switch
        {
            Mode.Single => [CollectSingle()],
            Mode.Range => CollectRange(),
            _ => CollectBatch(),
        };
    }

    private AddTarget CollectSingle()
    {
        string address = TxtAddress.Text.Trim();
        if (address.Length == 0 || address.Contains(' '))
            throw new InvalidOperationException("请填写设备地址（IP 或域名）");

        ushort port = ParsePort(TxtPort.Text, "端口");
        string user = TxtUser.Text.Trim();
        if (user.Length == 0) throw new InvalidOperationException("请填写用户名");

        string pwd = PwdSingle.Password;
        if (ChkVerify.IsChecked == true && pwd.Length == 0)
            throw new InvalidOperationException("勾选了「验证登录凭据」，请填写设备登录密码");

        return new AddTarget(address, port, user, pwd, TxtName.Text.Trim());
    }

    private List<AddTarget> CollectRange()
    {
        if (!TryParseIp(TxtStartIp.Text.Trim(), out var start))
            throw new InvalidOperationException("起始 IP 地址无效");
        if (!TryParseIp(TxtEndIp.Text.Trim(), out var end))
            throw new InvalidOperationException("结束 IP 地址无效");

        var sb = start.GetAddressBytes();
        var eb = end.GetAddressBytes();
        if (sb[0] != eb[0] || sb[1] != eb[1] || sb[2] != eb[2])
            throw new InvalidOperationException("仅支持同一 C 段（IP 前三个数字必须相同）");

        int lo = Math.Min(sb[3], eb[3]);
        int hi = Math.Max(sb[3], eb[3]);
        if (hi - lo + 1 > 254)
            throw new InvalidOperationException("一次最多添加 254 台，范围太大了");

        ushort port = ParsePort(TxtRangePort.Text, "端口");
        string user = TxtRangeUser.Text.Trim();
        if (user.Length == 0) throw new InvalidOperationException("请填写用户名");
        string pwd = PwdRange.Password;
        if (ChkVerify.IsChecked == true && pwd.Length == 0)
            throw new InvalidOperationException("勾选了「验证登录凭据」，请填写设备登录密码");

        var prefix = $"{sb[0]}.{sb[1]}.{sb[2]}";
        var list = new List<AddTarget>(hi - lo + 1);
        for (int o = lo; o <= hi; o++)
            list.Add(new AddTarget($"{prefix}.{o}", port, user, pwd, string.Empty));
        return list;
    }

    private List<AddTarget> CollectBatch()
    {
        var list = new List<AddTarget>();
        int lineNo = 0;
        foreach (var raw in TxtBatch.Text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#")) continue;

            var parts = line.Split(',');
            string address = parts[0].Trim();
            if (address.Length == 0 || address.Contains(' '))
                throw new InvalidOperationException($"第 {lineNo} 行地址无效：{line}");

            ushort port = 8000;
            if (parts.Length > 1 && parts[1].Trim().Length > 0)
                port = ParsePort(parts[1].Trim(), $"第 {lineNo} 行端口");

            string user = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : "admin";
            string pwd = parts.Length > 3 ? parts[3].Trim() : string.Empty;
            string name = parts.Length > 4 ? parts[4].Trim() : string.Empty;

            list.Add(new AddTarget(address, port, user, pwd, name));
            if (list.Count > 200)
                throw new InvalidOperationException("批量导入一次最多 200 台，请分批添加");
        }
        return list;
    }

    private static ushort ParsePort(string text, string label)
    {
        if (!ushort.TryParse(text.Trim(), out var port) || port == 0 || port > 65535)
            throw new InvalidOperationException($"{label}无效（1-65535）");
        return port;
    }

    /// <summary>
    /// 登录后向设备要基础信息（ISAPI /ISAPI/System/deviceInfo）：
    /// 序列号、MAC、型号、固件版本。取不到就返回空串 —— 台账里
    /// 这些列保持原样，绝不拿空值覆盖已有数据。
    /// internal：台账页「检测」按钮（AssetViewModel）走同一条链路复用。
    /// </summary>
    internal static (string serial, string mac, string model, string firmware) FetchDeviceInfo(AppState state, int userId)
    {
        try
        {
            var (ok, xml, _) = state.NetSdk.IsapiGet(userId, "/ISAPI/System/deviceInfo");
            if (!ok || string.IsNullOrWhiteSpace(xml)) return ("", "", "", "");

            var doc = System.Xml.Linq.XDocument.Parse(xml);
            string V(string name) => doc.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim() ?? string.Empty;

            return (V("serialNumber"), V("macAddress"), V("model"), V("firmwareVersion"));
        }
        catch
        {
            return ("", "", "", "");
        }
    }

    private static bool TryParseIp(string text, out IPAddress address)
    {
        if (IPAddress.TryParse(text, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address = ip;
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    // ==================== UI 状态 ====================

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnAdd.IsEnabled = !busy;
        BtnAddAndContinue.IsEnabled = !busy;
        ChkVerify.IsEnabled = !busy;
        if (!busy && StatusText.Text.StartsWith("正在验证"))
            StatusText.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string text, bool okColor = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            okColor ? "BrushSuccess" : "BrushTextSecondary");
        StatusText.Visibility = Visibility.Visible;
    }

    private void Warn(string text)
    {
        ShowStatus(text);
        System.Windows.MessageBox.Show(this, text, "添加设备", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

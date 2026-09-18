using System.Windows;
using HikDeployTool.Models;

namespace HikDeployTool.Views;

/// <summary>
/// 台账「编辑」弹窗。
///
/// 表格内双击只能改名称/位置/备注这几列，手工补录时填错的 IP/端口/
/// 用户名、后来补知道的序列号，都得有个正经修改入口。
/// 口令框留空 = 不动已保存口令（它平时是密文，摆明文也摆不出来）。
/// </summary>
public partial class AssetEditWindow : Window
{
    private readonly AssetRecord _record;

    public AssetEditWindow(AssetRecord record)
    {
        InitializeComponent();
        _record = record;

        TxtSubTitle.Text = $"{record.Ip} · {record.ActivateState} · {record.Online}"
            + (string.IsNullOrWhiteSpace(record.Model) ? string.Empty : $" · {record.Model}");
        TxtId.Text = record.Id;
        TxtName.Text = record.Name;
        TxtLocation.Text = record.Location;
        TxtIp.Text = record.Ip;
        TxtPort.Text = record.Port.ToString();
        TxtUser.Text = record.AdminUserName;
        TxtSerial.Text = record.SerialNo;
        TxtMac.Text = record.Mac;
        TxtModel.Text = record.Model;
        TxtFirmware.Text = record.FirmwareVersion;
        TxtNote.Text = record.Note;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string ip = TxtIp.Text.Trim();
        if (ip.Length == 0 || ip.Contains(' '))
        {
            MessageBox.Show(this, "请填写设备 IP 地址", "编辑台账",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!ushort.TryParse(TxtPort.Text.Trim(), out ushort port) || port == 0 || port > 65535)
        {
            MessageBox.Show(this, "SDK 端口无效（1-65535）", "编辑台账",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _record.Name = TxtName.Text.Trim();
        _record.Location = TxtLocation.Text.Trim();
        _record.Ip = ip;
        _record.Port = port;
        _record.AdminUserName = TxtUser.Text.Trim();
        _record.SerialNo = TxtSerial.Text.Trim();
        _record.Mac = TxtMac.Text.Trim();
        _record.Model = TxtModel.Text.Trim();
        _record.FirmwareVersion = TxtFirmware.Text.Trim();
        _record.Note = TxtNote.Text.Trim();

        // 口令留空 = 不修改；填了才覆盖（内部走 DPAPI 加密）
        string pwd = PwdPassword.Password;
        if (pwd.Length > 0) _record.Password = pwd;

        DialogResult = true;
        Close();
    }
}

using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>
/// 批量激活页。
///
/// 现场痛点：新拆封的 IPC 都是"未激活"状态，一台台用浏览器点激活极慢，
/// 而且设备默认 IP 都在 192.168.1.64 撞车，必须先激活再改 IP。
/// 本页把"统一初始口令 + 并发激活"做成一次点击。
/// </summary>
public sealed class ActivationViewModel : DeviceTaskPageViewModel
{
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private bool _allowWeakPassword;
    private bool _showPassword;

    public ActivationViewModel()
    {
        ConfirmPasswordCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrEmpty(_confirmPassword))
            {
                Toast("请再次输入以便核对", true);
                return;
            }
            Toast(_password == _confirmPassword ? "两次输入一致" : "两次输入不一致", _password != _confirmPassword);
        });

        GeneratePasswordCommand = new RelayCommand(() =>
        {
            var pwd = PasswordPolicy.Generate(12);
            Password = pwd;
            ConfirmPassword = pwd;
            ShowPassword = true;
            try { System.Windows.Clipboard.SetText(pwd); Toast("已生成强口令并复制到剪贴板，请妥善保存"); }
            catch { Toast("已生成强口令，请妥善保存"); }
        });
    }

    public override string Title => "批量激活";

    public override string Glyph => "IconBolt";

    public override string Description => "给未激活设备批量下发初始口令，一次完成开箱上线";

    public override string Usage => "推荐流程：发现页点「只勾选未激活」→ 投递到本页 → 生成统一初始口令 → 执行。设备重启约需 30 秒。";

    protected override string BusyTextPrefix => "批量激活";

    protected override string LogSource => "激活";

    // ==================== 口令 ====================

    public string Password
    {
        get => _password;
        set
        {
            if (!Set(ref _password, value)) return;
            Strength = PasswordPolicy.Evaluate(value);
            OnPropertyChanged(nameof(PasswordMatchText));
        }
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set
        {
            if (Set(ref _confirmPassword, value)) OnPropertyChanged(nameof(PasswordMatchText));
        }
    }

    private PasswordStrength _strength = PasswordPolicy.Evaluate(string.Empty);

    public PasswordStrength Strength
    {
        get => _strength;
        private set
        {
            if (!Set(ref _strength, value)) return;
            OnPropertyChanged(nameof(StrengthText));
            OnPropertyChanged(nameof(StrengthPercent));
            OnPropertyChanged(nameof(StrengthBrushKey));
        }
    }

    public double StrengthPercent => Strength.Percent;

    public string StrengthText => string.IsNullOrEmpty(Password)
        ? "尚未设置口令"
        : $"强度：{Strength.Label} · {Strength.Hint}";

    /// <summary>供 XAML 通过 DataTrigger 切换进度条颜色。</summary>
    public string StrengthBrushKey => Strength.Score switch
    {
        <= 1 => "Weak",
        2 => "Medium",
        3 => "Strong",
        _ => "VeryStrong",
    };

    public string PasswordMatchText => string.IsNullOrEmpty(_confirmPassword)
        ? string.Empty
        : _password == _confirmPassword ? "两次输入一致" : "两次输入不一致";

    /// <summary>允许弱口令（部分老固件不接受特殊字符，或项目指定了简单口令）。</summary>
    public bool AllowWeakPassword
    {
        get => _allowWeakPassword;
        set => Set(ref _allowWeakPassword, value);
    }

    public bool ShowPassword
    {
        get => _showPassword;
        set => Set(ref _showPassword, value);
    }

    public RelayCommand GeneratePasswordCommand { get; }

    public RelayCommand ConfirmPasswordCommand { get; }

    private RelayCommand? _toggleShowPasswordCommand;
    public RelayCommand ToggleShowPasswordCommand => _toggleShowPasswordCommand ??= new(() => ShowPassword = !ShowPassword);

    // ==================== 校验与执行 ====================

    protected override string? Validate()
    {
        if (string.IsNullOrEmpty(Password)) return "请先设置激活口令";

        if (!string.IsNullOrEmpty(ConfirmPassword) && Password != ConfirmPassword)
            return "两次输入的口令不一致，请检查";

        if (!AllowWeakPassword && !Strength.Acceptable)
            return $"口令强度不足（{Strength.Hint}）。若确需使用弱口令，请勾选「允许弱口令」。";

        int already = Targets.Count(d => d.Activate == ActivateState.Activated);
        if (already == Targets.Count && already > 0)
            return $"清单里 {already} 台设备都已是激活状态，无需再次激活。";

        return null;
    }

    protected override (bool ok, string message) Execute(DiscoveredDevice device)
    {
        if (device.Activate == ActivateState.Activated)
            return (true, "已是激活状态，跳过");

        if (string.IsNullOrWhiteSpace(device.UniformDevID))
            return (false, "设备唯一标识（UniformDevID）为空，无法激活");

        var (ok, msg) = App.Sadp.Activate(device.UniformDevID, Password);
        return (ok, msg);
    }

    protected override void OnDeviceProcessed(DiscoveredDevice device, OperationResult result)
    {
        if (!result.Success) return;
        UiInvoke(() =>
        {
            // 激活成功后本地状态先改掉，界面上设备行会立刻变成"已激活"；
            // 真正的确认由 SADP 后续上报的 byActivated 覆盖。
            device.Activate = ActivateState.Activated;
            if (string.IsNullOrWhiteSpace(device.AdminUserName)) device.AdminUserName = App.Settings.DefaultUserName;
        });
    }

    protected override void AfterRun()
    {
        // 激活后设备会重启并重新上广播，稍等再刷一次列表更接近真实状态
        if (SuccessCount == 0) return;
        Toast($"激活完成 {SuccessCount} 台。设备重启约 30 秒后回到「设备发现」页确认状态。");
    }
}

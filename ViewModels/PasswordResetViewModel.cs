using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using HikDeployTool.Models;
using HikDeployTool.Native;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>密码重置方式。</summary>
public sealed class ResetTypeOption
{
    public byte Value { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;
}

/// <summary>二维码来源选项。</summary>
public sealed class QrSourceOption
{
    public QrKind Value { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;
}

/// <summary>安全问题的一条答案。</summary>
public sealed class SecurityAnswerItem : ObservableObject
{
    private string _answer = string.Empty;

    public int Index { get; init; }
    public string Label => $"安全问题 {Index + 1}";
    public string Answer { get => _answer; set => Set(ref _answer, value); }
}

/// <summary>一次"读取设备码 / GUID / 找回能力"的结果。</summary>
public sealed class RecoveryItem
{
    public string Target { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;

    /// <summary>设备码原文（同时就是"二维码重置"的二维码内容）。</summary>
    public string DeviceCode { get; init; } = string.Empty;

    public string Guid { get; init; } = string.Empty;

    /// <summary>设备已配置的找回方式（来自 SADP_GET_PASSWORD_RESET_TYPE）。</summary>
    public string Ability { get; init; } = string.Empty;

    /// <summary>设备码读取失败时的具体原因（含 SADP 错误码）。</summary>
    public string DeviceCodeError { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool HasDeviceCode => !string.IsNullOrWhiteSpace(DeviceCode);
}

/// <summary>
/// 密码重置页（密码救援）。
///
/// 现场场景：甲方忘了密码 / 前任施工队没交密码 / 设备被离职员工改过密码。
/// 海康的官方救援路径是 SADP 的"忘记密码"流程，按设备固件能力不同分成几种：
///   ③ 设备码 / 二维码：设备返回一串 Base64 密文（设备码），
///      把它做成二维码给手机扫，或交给海康官方工具，换回"重置口令"再下发；
///   ⑦ 手机扫码：需要设备上预留过手机号，二维码内容由设备组装
///      （"域名?code=D:型号**密文"）；
///   ⑥ 预留邮箱：二维码内容由设备直接给出，扫码后安全码发到预留邮箱；
///   ② 导入授权文件：设备码 → 官方工具 → 授权文件 → 在这里导入；
///   ④ GUID 字符串；⑤ 安全问题。
///
/// 本页把这些方式一次性列全，并把"设备码读取"做成批量。
/// 重置接口优先用 V50（密码字段 128 字节，支持现代复杂口令），失败才回退 V40。
///
/// ---- 关于"二维码显示不出来" ----
/// SADP SDK **只返回二维码内容字符串，从不生成图片**（见 Sadp.h 的
/// SADP_PHONE_QR_CODES / SADP_QR_CODES 注释，以及 demo 里上层自己调 CQrcode 的做法）。
/// 所以二维码能不能显示，取决于上层有没有一个编码器。本页用的是
/// <see cref="QrEncoder"/>（QrCodes.cpp 的移植，已与 python-qrcode 逐字节比对过）。
/// </summary>
public sealed class PasswordResetViewModel : DeviceTaskPageViewModel
{
    private byte _resetType = SadpConst.RESET_BY_QRCODE;
    private string _newPassword = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _code = string.Empty;
    private string _authFile = string.Empty;
    private string _guid = string.Empty;
    private string _mailBox = string.Empty;
    private string _phoneNo = string.Empty;
    private bool _syncIpcPassword = true;
    private bool _showPassword;
    private string _probeSummary = "尚未读取设备信息";
    private string _probeDetail = string.Empty;

    // ---- 二维码区状态 ----
    private QrKind _selectedQrSource = QrKind.DeviceCode;
    private BitmapSource? _qrImage;
    private string _qrContent = string.Empty;
    private string _qrMeta = string.Empty;
    private string _qrNotice = "选择来源后点「生成二维码」。";
    private string _qrVersionText = string.Empty;
    private bool _hasQr;
    private RecoveryItem? _selectedRecovery;
    private DiscoveredDevice? _selectedTarget;

    public PasswordResetViewModel()
    {
        ResetTypes =
        [
            new ResetTypeOption
            {
                Value = SadpConst.RESET_BY_QRCODE,
                Label = "设备码 / 二维码",
                Hint = "最常用：读取设备码 → 手机扫码或海康官方工具换回「重置口令」→ 填回下方下发",
            },
            new ResetTypeOption
            {
                Value = SadpConst.RESET_BY_PHONE,
                Label = "手机扫码（需预留手机号）",
                Hint = "二维码由设备按手机号生成，手机扫一下即完成验证，最后把手机上的口令填回来",
            },
            new ResetTypeOption
            {
                Value = SadpConst.RESET_BY_MAILBOX,
                Label = "预留邮箱",
                Hint = "需要设备出厂时预留过安全邮箱，扫码后安全码发到该邮箱",
            },
            new ResetTypeOption
            {
                Value = SadpConst.RESET_BY_FILE,
                Label = "导入授权文件",
                Hint = "设备码交给海康官方密码重置工具，换回授权文件后在这里导入下发",
            },
            new ResetTypeOption
            {
                Value = SadpConst.RESET_BY_GUID,
                Label = "GUID 字符串",
                Hint = "直接拿到设备的 GUID 明文，适合已导出过 GUID 的设备",
            },
            new ResetTypeOption
            {
                Value = SadpConst.RESET_BY_QUESTION,
                Label = "安全问题",
                Hint = "出厂时预设的提问找回，需要把全部答案都填上",
            },
        ];

        QrSources =
        [
            new QrSourceOption
            {
                Value = QrKind.DeviceCode,
                Label = "设备码二维码",
                Hint = "二维码内容就是设备码本身。手机扫码后回到设备网页完成验证，拿到重置口令。",
            },
            new QrSourceOption
            {
                Value = QrKind.PhoneScan,
                Label = "手机扫码重置",
                Hint = "需要先填手机号：设备按该手机号查出一段密文，二维码内容为「域名?code=D:型号**密文」。",
            },
            new QrSourceOption
            {
                Value = QrKind.Mailbox,
                Label = "预留邮箱重置",
                Hint = "需要设备已预留安全邮箱：二维码内容由设备直接给出，扫码后安全码发到该邮箱。",
            },
        ];

        Answers = [];
        for (int i = 0; i < 3; i++) Answers.Add(new SecurityAnswerItem { Index = i });

        Recovery = [];

        GeneratePasswordCommand = new RelayCommand(() =>
        {
            var pwd = PasswordPolicy.Generate(12);
            NewPassword = pwd;
            ConfirmPassword = pwd;
            ShowPassword = true;
            Toast("已生成新口令，请复制保存后再执行重置");
        });

        PickAuthFileCommand = new RelayCommand(PickAuthFile);

        ReadRecoveryInfoCommand = new AsyncRelayCommand(ReadRecoveryInfoAsync);

        // 点清单里的单选框 → 把那台设备设为二维码 / 读邮箱的目标
        SelectTargetCommand = new RelayCommand(p => SelectedTarget = p as DiscoveredDevice);

        LoadQrCommand = new AsyncRelayCommand(LoadQrAsync, () => Targets.Count > 0);

        ReadMailboxCommand = new AsyncRelayCommand(ReadMailboxAsync, () => Targets.Count > 0);

        UseSelectedRecoveryCommand = new RelayCommand(_ => UseSelectedRecovery(), _ => SelectedRecovery is { HasDeviceCode: true });

        SaveQrCommand = new RelayCommand(SaveQr, () => HasQr);
        CopyQrContentCommand = new RelayCommand(CopyQrContent, () => HasQr);
        CopyQrImageCommand = new RelayCommand(CopyQrImage, () => HasQr);
        ClearQrCommand = new RelayCommand(ClearQr, () => HasQr);

        AddAnswerCommand = new RelayCommand(() =>
        {
            if (Answers.Count >= SadpConst.MAX_QUESTION_LIST_LEN)
            {
                Toast($"最多支持 {SadpConst.MAX_QUESTION_LIST_LEN} 个安全问题", true);
                return;
            }
            Answers.Add(new SecurityAnswerItem { Index = Answers.Count });
        });

        RemoveAnswerCommand = new RelayCommand(() =>
        {
            if (Answers.Count <= 1) { Toast("至少保留一个安全问题输入框", true); return; }
            Answers.RemoveAt(Answers.Count - 1);
        });
    }

    public ObservableCollection<ResetTypeOption> ResetTypes { get; }

    public ObservableCollection<QrSourceOption> QrSources { get; }

    public ObservableCollection<SecurityAnswerItem> Answers { get; }

    public ObservableCollection<RecoveryItem> Recovery { get; }

    public RelayCommand GeneratePasswordCommand { get; }

    public RelayCommand PickAuthFileCommand { get; }

    public AsyncRelayCommand ReadRecoveryInfoCommand { get; }

    public AsyncRelayCommand LoadQrCommand { get; }

    public AsyncRelayCommand ReadMailboxCommand { get; }

    public RelayCommand UseSelectedRecoveryCommand { get; }

    public RelayCommand SaveQrCommand { get; }

    public RelayCommand CopyQrContentCommand { get; }

    public RelayCommand CopyQrImageCommand { get; }

    public RelayCommand ClearQrCommand { get; }

    public RelayCommand AddAnswerCommand { get; }

    public RelayCommand RemoveAnswerCommand { get; }

    /// <summary>点清单单选框时把该设备设为二维码 / 读邮箱目标。</summary>
    public RelayCommand SelectTargetCommand { get; }

    /// <summary>
    /// 清单里当前被单选框选中的设备 —— 二维码生成、读预留邮箱都作用在它身上。
    /// 清单变化时由 OnTargetsChanged 自动维持有效（没了就回退到第一台）。
    /// </summary>
    public DiscoveredDevice? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value)) return;
            OnPropertyChanged(nameof(QrTargetHint));
        }
    }

    /// <summary>二维码 / 读邮箱真正作用的目标：优先手动选中的那台，兜底清单第一台。</summary>
    private DiscoveredDevice? QrTarget => SelectedTarget ?? Targets.FirstOrDefault();

    private RelayCommand? _toggleShowPasswordCommand;
    /// <summary>切换新口令的明文 / 密文显示。</summary>
    public RelayCommand ToggleShowPasswordCommand => _toggleShowPasswordCommand ??= new(() => ShowPassword = !ShowPassword);

    public override string Title => "密码重置";

    public override string Glyph => "IconKey";

    public override string Description => "设备密码遗忘时，用设备码 / 二维码 / GUID / 安全问题重置为新口令";

    public override string Usage => "先点「读取设备码」批量拿凭据，再用「生成二维码」把设备码做成可扫的码；手机扫完把口令填回下方即可下发。失败会透出设备剩余尝试次数，切忌反复试错。";

    protected override string BusyTextPrefix => "批量密码重置";

    protected override string LogSource => "密码重置";

    // ==================== 重置方式 ====================

    public byte ResetType
    {
        get => _resetType;
        set
        {
            if (!Set(ref _resetType, value)) return;
            OnPropertyChanged(nameof(CurrentTypeHint));
            OnPropertyChanged(nameof(NeedCode));
            OnPropertyChanged(nameof(NeedAuthFile));
            OnPropertyChanged(nameof(NeedGuid));
            OnPropertyChanged(nameof(NeedAnswers));
            OnPropertyChanged(nameof(NeedPhone));
            OnPropertyChanged(nameof(NeedMailBox));
            OnPropertyChanged(nameof(NeedResetToken));
            OnPropertyChanged(nameof(ShowQrPanel));
            OnPropertyChanged(nameof(CurrentTypeLabel));
            OnPropertyChanged(nameof(CodeLabel));
            OnPropertyChanged(nameof(CodeHint));
            OnPropertyChanged(nameof(QrSuggestion));

            // 重置方式与二维码来源天然一一对应，切方式时顺手把二维码源也切过去，
            // 省掉一次"为什么生成的码不对"的困惑
            var mapping = ResetType switch
            {
                SadpConst.RESET_BY_PHONE => QrKind.PhoneScan,
                SadpConst.RESET_BY_MAILBOX => QrKind.Mailbox,
                _ => QrKind.DeviceCode,
            };
            if (SelectedQrSource != mapping) SelectedQrSource = mapping;
        }
    }

    public ResetTypeOption? CurrentType => ResetTypes.FirstOrDefault(t => t.Value == ResetType);

    public string CurrentTypeLabel => CurrentType?.Label ?? "未知方式";

    public string CurrentTypeHint => CurrentType?.Hint ?? string.Empty;

    /// <summary>方式 3：设备码本身也要填进「重置口令」栏（部分固件直接认设备码）。</summary>
    public bool NeedCode => ResetType == SadpConst.RESET_BY_QRCODE;

    public bool NeedAuthFile => ResetType == SadpConst.RESET_BY_FILE;

    public bool NeedGuid => ResetType == SadpConst.RESET_BY_GUID;

    public bool NeedAnswers => ResetType == SadpConst.RESET_BY_QUESTION;

    /// <summary>方式 7 需要手机号（设备要用它查二维码）。</summary>
    public bool NeedPhone => ResetType == SadpConst.RESET_BY_PHONE;

    /// <summary>方式 6 需要预留邮箱地址。</summary>
    public bool NeedMailBox => ResetType == SadpConst.RESET_BY_MAILBOX;

    /// <summary>方式 3 / 6 / 7 都要把扫码换回来的口令填进 szCode。</summary>
    public bool NeedResetToken => ResetType is SadpConst.RESET_BY_QRCODE or SadpConst.RESET_BY_MAILBOX or SadpConst.RESET_BY_PHONE;

    /// <summary>这几种方式存在"扫描二维码"的环节，把二维码区显示出来。</summary>
    public bool ShowQrPanel => NeedResetToken;

    /// <summary>szCode 字段在界面上到底叫什么，随方式变。</summary>
    public string CodeLabel => ResetType switch
    {
        SadpConst.RESET_BY_QRCODE => "重置口令 / 设备码",
        SadpConst.RESET_BY_MAILBOX => "邮箱收到的安全码",
        SadpConst.RESET_BY_PHONE => "手机上拿到的重置口令",
        _ => "重置口令",
    };

    public string CodeHint => ResetType switch
    {
        SadpConst.RESET_BY_QRCODE =>
            "两条路都行：① 右侧生成二维码，手机扫码验证后把口令贴回来；② 直接把设备码原文填这里（部分固件直接认）。",
        SadpConst.RESET_BY_MAILBOX =>
            "先点右侧「生成二维码」并扫码触发流程，设备会把安全码发到预留邮箱，收到后填这里。",
        SadpConst.RESET_BY_PHONE =>
            "先用预留手机号生成二维码、手机扫码完成验证，把手机上给出的口令填这里。",
        _ => string.Empty,
    };

    /// <summary>给用户的"下一步做什么"提示。</summary>
    public string QrSuggestion => SelectedQrSource switch
    {
        QrKind.DeviceCode => "生成后让手机扫这张码，按提示完成验证即可拿到重置口令。",
        QrKind.PhoneScan => "生成后让手机扫这张码，验证完成手机会给出重置口令。",
        QrKind.Mailbox => "生成后让手机扫这张码，安全码会发到设备预留的邮箱。",
        _ => string.Empty,
    };

    public string NewPassword
    {
        get => _newPassword;
        set
        {
            if (!Set(ref _newPassword, value)) return;
            Strength = PasswordPolicy.Evaluate(value);
            OnPropertyChanged(nameof(StrengthText));
            OnPropertyChanged(nameof(StrengthPercent));
        }
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => Set(ref _confirmPassword, value);
    }

    private PasswordStrength _strength = PasswordPolicy.Evaluate(string.Empty);

    public PasswordStrength Strength { get => _strength; private set => Set(ref _strength, value); }

    public double StrengthPercent => Strength.Percent;

    public string StrengthText => string.IsNullOrEmpty(NewPassword)
        ? "尚未设置新口令"
        : $"强度：{Strength.Label} · {Strength.Hint}";

    public bool ShowPassword { get => _showPassword; set => Set(ref _showPassword, value); }

    public string Code { get => _code; set => Set(ref _code, value); }

    public string AuthFile { get => _authFile; set => Set(ref _authFile, value); }

    public string Guid { get => _guid; set => Set(ref _guid, value); }

    /// <summary>预留邮箱地址（方式 6 用）。</summary>
    public string MailBox { get => _mailBox; set => Set(ref _mailBox, value); }

    /// <summary>预留手机号（方式 7 用）。</summary>
    public string PhoneNo { get => _phoneNo; set => Set(ref _phoneNo, value); }

    public bool SyncIpcPassword { get => _syncIpcPassword; set => Set(ref _syncIpcPassword, value); }

    public string ProbeSummary { get => _probeSummary; private set => Set(ref _probeSummary, value); }

    public string ProbeDetail { get => _probeDetail; private set => Set(ref _probeDetail, value); }

    private void PickAuthFile()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择设备导出的授权文件 / GUID 加密文件",
                Filter = "授权文件 (*.xml;*.txt;*.dat;*.bin)|*.xml;*.txt;*.dat;*.bin|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() == true)
            {
                AuthFile = dlg.FileName;
                Toast("已选择授权文件");
            }
        }
        catch (Exception ex)
        {
            Toast($"选择文件失败：{ex.Message}", true);
        }
    }

    // ==================== 读取设备码 / GUID ====================

    private async Task ReadRecoveryInfoAsync()
    {
        var list = Targets.ToList();
        if (list.Count == 0)
        {
            Toast("请先载入待处理设备", true);
            return;
        }

        Recovery.Clear();
        SelectedRecovery = null;
        ProbeSummary = "正在读取…";
        ProbeDetail = string.Empty;

        using (Busy($"正在读取 {list.Count} 台设备的设备码…"))
        {
            await Task.Run(() =>
            {
                foreach (var d in list)
                {
                    var codeResult = App.Sadp.GetDeviceCode(d.UniformDevID, d.Mac);
                    var guidResult = App.Sadp.GetGuid(d.UniformDevID);
                    var abilityResult = App.Sadp.GetPasswordResetAbility(d.UniformDevID);
                    var mailboxResult = App.Sadp.GetUserMailbox(d.UniformDevID);

                    // 读取失败的原因分三层，缺哪层都会让用户以为"程序坏了"：
                    // 设备码本身（最要紧）、GUID、以及"这台设备到底配过哪些找回方式"
                    string error = codeResult.ok ? string.Empty : codeResult.message;
                    if (!codeResult.ok && !guidResult.ok) error += "；GUID：" + guidResult.message;

                    var item = new RecoveryItem
                    {
                        Target = string.IsNullOrWhiteSpace(d.SerialNo) ? d.UniformDevID : d.SerialNo,
                        Address = d.IpMaskText,
                        DeviceCode = codeResult.code,
                        Guid = guidResult.guid,
                        Ability = abilityResult.ok ? abilityResult.ability.Describe() : $"读取失败：{abilityResult.message}",
                        DeviceCodeError = error,
                        Message = codeResult.ok
                            ? "读取成功"
                            : "未读到设备码 —— 见「失败原因」列",
                    };

                    if (mailboxResult.ok && string.IsNullOrWhiteSpace(MailBox) && list.Count == 1)
                        UiInvoke(() => MailBox = mailboxResult.mailbox);

                    UiInvoke(() => Recovery.Add(item));
                }
            });
        }

        int ok = Recovery.Count(r => r.HasDeviceCode);
        ProbeSummary = $"读取完成：{ok} / {Recovery.Count} 台拿到设备码";
        ProbeDetail = BuildProbeDetail();

        // 只有一台设备时直接把结果填进输入框，减少手工复制
        if (Recovery.Count == 1)
        {
            var only = Recovery[0];
            if (only.HasDeviceCode) Code = only.DeviceCode;
            if (!string.IsNullOrWhiteSpace(only.Guid)) Guid = only.Guid;
            SelectedRecovery = only;
        }

        Log.Info($"{ProbeSummary}。{ProbeDetail}", LogSource);
        Toast(ProbeSummary, ok == 0);
    }

    /// <summary>把失败原因按错误码归类，一眼能看出"是设备的问题还是环境的问题"。</summary>
    private string BuildProbeDetail()
    {
        var failed = Recovery.Where(r => !r.HasDeviceCode && !string.IsNullOrWhiteSpace(r.DeviceCodeError)).ToList();
        if (failed.Count == 0) return string.Empty;

        var groups = failed
            .GroupBy(r => r.DeviceCodeError)
            .Select(g => $"{g.Count()} 台：{g.Key}")
            .ToList();

        string advice = failed[0].DeviceCodeError.Contains("2019") ? "（设备未激活，先激活再读）"
            : failed[0].DeviceCodeError.Contains("2011") ? "（响应超时，检查设备与本机是否同网段、是否被防火墙拦住）"
            : failed[0].DeviceCodeError.Contains("2009") ? "（设备拒绝：多半绑定了萤石云/Hik-Connect，需先在平台侧处理）"
            : failed[0].DeviceCodeError.Contains("2051") || failed[0].DeviceCodeError.Contains("2054")
                ? "（设备不支持该命令，可能是仅发现设备或链路协议不匹配）"
                : string.Empty;

        return $"失败 {failed.Count} 台 —— {string.Join("；", groups)}{advice}";
    }

    private async Task ReadMailboxAsync()
    {
        var dev = QrTarget;
        if (dev == null) { Toast("请先载入设备", true); return; }

        string box = string.Empty;
        string message = string.Empty;
        using (Busy("正在读取设备预留邮箱…"))
        {
            await Task.Run(() =>
            {
                var r = App.Sadp.GetUserMailbox(dev.UniformDevID);
                box = r.mailbox;
                message = r.message;
            });
        }

        if (string.IsNullOrWhiteSpace(box))
        {
            Toast($"读取预留邮箱失败：{message}", true);
            Log.Warn($"读取 {dev.IpMaskText} 预留邮箱失败：{message}", LogSource);
            return;
        }

        MailBox = box;
        Toast($"设备预留邮箱：{box}");
    }

    /// <summary>用清单里的第一台设备读取二维码原料。多台时请先在清单里只保留目标设备。</summary>
    private async Task LoadQrAsync()
    {
        var dev = Targets.FirstOrDefault();
        if (dev == null) { Toast("请先载入设备", true); return; }

        string phone = PhoneNo?.Trim() ?? string.Empty;
        string mailbox = MailBox?.Trim() ?? string.Empty;
        var kind = SelectedQrSource;

        if (kind == QrKind.DeviceCode && string.IsNullOrWhiteSpace(Code))
        {
            // 设备码还没读就先把它读出来，省得用户来回点
            string fetched = string.Empty;
            string err = string.Empty;
            using (Busy("正在读取设备码…"))
            {
                await Task.Run(() =>
                {
                    var r = App.Sadp.GetDeviceCode(dev.UniformDevID, dev.Mac);
                    fetched = r.code;
                    err = r.message;
                });
            }
            if (string.IsNullOrWhiteSpace(fetched))
            {
                SetQrNotice($"读不到设备码，无法生成二维码。原因：{err}", true);
                Toast($"读取设备码失败：{err}", true);
                Log.Warn($"读取 {dev.IpMaskText} 设备码失败：{err}", LogSource);
                return;
            }
            Code = fetched;
        }

        if (kind == QrKind.PhoneScan && string.IsNullOrWhiteSpace(phone))
        {
            Toast("手机扫码方式需要先填写设备上预留的手机号", true);
            return;
        }

        QrPayload? payload = null;
        string failMessage = string.Empty;

        using (Busy("正在向设备索取二维码数据…"))
        {
            await Task.Run(() =>
            {
                switch (kind)
                {
                    case QrKind.DeviceCode:
                        var code = Code?.Trim() ?? string.Empty;
                        payload = new QrPayload
                        {
                            Kind = QrKind.DeviceCode,
                            RawQr = code,
                            Content = code,
                        };
                        break;

                    case QrKind.PhoneScan:
                        var rp = App.Sadp.GetPhoneQrCodes(dev.UniformDevID, phone);
                        payload = rp.payload;
                        failMessage = rp.message;
                        break;

                    case QrKind.Mailbox:
                        var rm = App.Sadp.GetMailQrCodes(dev.UniformDevID, mailbox);
                        payload = rm.payload;
                        failMessage = rm.message;
                        if (payload != null && string.IsNullOrWhiteSpace(MailBox) && !string.IsNullOrWhiteSpace(payload.MailBox))
                            UiInvoke(() => MailBox = payload.MailBox);
                        break;
                }
            });
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Content))
        {
            SetQrNotice($"设备未返回二维码数据：{failMessage}", true);
            Toast($"生成二维码失败：{failMessage}", true);
            Log.Warn($"读取 {dev.IpMaskText} 二维码数据失败：{failMessage}", LogSource);
            return;
        }

        RenderQr(dev, payload);
    }

    /// <summary>把二维码内容真正编码成图案。</summary>
    private void RenderQr(DiscoveredDevice dev, QrPayload payload)
    {
        try
        {
            var (qr, eccNote) = EncodeQr(payload.Content);

            // 目标像素对齐界面里那个展示框的内沿（260 - 2×边框 - 2×4 边距 = 250）。
            //
            // 250 不是随手定的：设备码最长 512 字节（SADP_SAFE_CODE_V31），
            // 纠错 Q 下大约 105~125 个模块，屏幕上必须保证**每个模块 ≥2 个物理像素**，
            // 否则手机会扫不出来 —— 而"二维码画出来了但扫不动"是最糟的失败形态，
            // 用户以为程序好了，现场却反复重试。250px 对 105 模块给出 scale=3（2.4px/模块），
            // 对 125 模块给出 scale=2 且正好 1:1 显示（2.0px/模块）。
            //
            // 严格 1:1 或 2:1 的整数缩放配合 NearestNeighbor，
            // 模块边界不会被插值糊掉。保存到文件时另按 scale=8 重新编码（见 SaveQr）。
            int quiet = 4;
            int scale = Math.Max(2, (int)Math.Ceiling(250.0 / (qr.ModuleCount + quiet * 2)));
            var bmp = QrEncoder.ToBitmap(qr, scale, quiet);

            QrImage = bmp;
            QrContent = payload.Content;
            _lastPayload = payload;
            HasQr = true;

            QrVersionText = $"版本 {qr.Version}（{qr.ModuleCount}×{qr.ModuleCount} 格） · {eccNote} · 放大 {scale}×";
            QrMeta = BuildQrMeta(dev, payload);
            QrNotice = $"{payload.KindLabel} 已生成。{QrSuggestion}";

            Log.Success($"已为 {dev.IpMaskText} 生成{payload.KindLabel}（{payload.Content.Length} 字符）", LogSource);
        }
        catch (Exception ex)
        {
            HasQr = false;
            QrImage = null;
            QrContent = string.Empty;
            QrVersionText = string.Empty;
            QrMeta = string.Empty;
            SetQrNotice($"二维码编码失败：{ex.Message}", true);
            Log.Error("二维码编码失败", ex, LogSource);
        }
    }

    /// <summary>
    /// 选纠错等级：优先 Q（更耐划痕/屏幕反光），装不下再退 M、L。
    /// 屏幕扫码一般不需要这么高的等级，但二维码是"扫不出来就得重来一遍现场操作"的东西，
    /// 编码冗余换来的成功率非常划算。
    /// </summary>
    private static (QrCode qr, string note) EncodeQr(string content)
    {
        var attempts = new (QrEcc ecc, string name)[] { (QrEcc.Q, "Q"), (QrEcc.M, "M"), (QrEcc.L, "L") };
        Exception? last = null;

        foreach (var (ecc, name) in attempts)
        {
            try
            {
                var qr = QrEncoder.Encode(content, ecc);
                string note = name == "Q" ? "纠错 Q" : $"纠错 {name}（内容较长自动降级）";
                return (qr, note);
            }
            catch (ArgumentException ex)
            {
                last = ex;
            }
        }

        throw new ArgumentException(
            $"二维码内容过长，无法编码（{content.Length} 字符，最高容量的 L 级也装不下）。" +
            $"设备码异常长时请改用「导入授权文件」方式。{last?.Message}", last);
    }

    private string BuildQrMeta(DiscoveredDevice dev, QrPayload payload)
    {
        var parts = new List<string>
        {
            $"目标：{dev.IpMaskText}",
            $"方式：{payload.KindLabel}",
            $"内容长度：{payload.Content.Length} 字符",
        };
        if (!string.IsNullOrWhiteSpace(payload.DeviceModel)) parts.Add($"设备型号：{payload.DeviceModel}");
        if (!string.IsNullOrWhiteSpace(payload.DomainName)) parts.Add($"域名：{payload.DomainName}");
        if (!string.IsNullOrWhiteSpace(payload.PhoneNo)) parts.Add($"手机号：{payload.PhoneNo}");
        if (!string.IsNullOrWhiteSpace(payload.MailBox)) parts.Add($"预留邮箱：{payload.MailBox}");
        if (!string.IsNullOrWhiteSpace(payload.ServiceMailBox)) parts.Add($"服务邮箱：{payload.ServiceMailBox}");
        if (payload.ValidSeconds > 0) parts.Add($"有效期：{payload.ValidSeconds / 60} 分钟");
        return string.Join("　·　", parts);
    }

    private void UseSelectedRecovery()
    {
        var item = SelectedRecovery;
        if (item == null || !item.HasDeviceCode) { Toast("请先在下方表格里选中一台已读到设备码的设备", true); return; }

        Code = item.DeviceCode;
        SelectedQrSource = QrKind.DeviceCode;

        var dev = Targets.FirstOrDefault(d => d.IpMaskText == item.Address || d.SerialNo == item.Target)
                  ?? Targets.FirstOrDefault();

        // 表格里选中了谁，清单上的单选框就跟着指到谁，两处"选中"保持一致
        if (dev != null) SelectedTarget = dev;

        var payload = new QrPayload { Kind = QrKind.DeviceCode, RawQr = item.DeviceCode, Content = item.DeviceCode };

        if (dev != null) RenderQr(dev, payload);
        else
        {
            // 清单里已经没有这台设备了（用户清过清单），也让他看到码。
            // 注意 IpMaskText 是只读的计算属性（Ip/掩码拼出来的），只能用原始字段构造。
            string ip = item.Address;
            int slash = ip.IndexOf('/');
            if (slash >= 0) ip = ip[..slash];
            var stub = new DiscoveredDevice { SerialNo = item.Target, IPv4Address = ip };
            RenderQr(stub, payload);
        }
    }

    public RecoveryItem? SelectedRecovery
    {
        get => _selectedRecovery;
        set
        {
            if (!Set(ref _selectedRecovery, value)) return;
            UseSelectedRecoveryCommand.RaiseCanExecuteChanged();
        }
    }

    // ==================== 二维码区状态 ====================

    public QrKind SelectedQrSource
    {
        get => _selectedQrSource;
        set
        {
            if (!Set(ref _selectedQrSource, value)) return;
            OnPropertyChanged(nameof(QrSuggestion));
            OnPropertyChanged(nameof(QrSourceHint));
            OnPropertyChanged(nameof(QrInputHint));
            OnPropertyChanged(nameof(NeedQrPhone));
            OnPropertyChanged(nameof(NeedQrMailBox));
            OnPropertyChanged(nameof(QrSourceLabel));
        }
    }

    public string QrSourceLabel => QrSources.FirstOrDefault(s => s.Value == SelectedQrSource)?.Label ?? "二维码";

    public string QrSourceHint => QrSources.FirstOrDefault(s => s.Value == SelectedQrSource)?.Hint ?? string.Empty;

    /// <summary>手机扫码来源需要手机号输入框。</summary>
    public bool NeedQrPhone => SelectedQrSource == QrKind.PhoneScan;

    /// <summary>邮箱来源需要邮箱输入框。</summary>
    public bool NeedQrMailBox => SelectedQrSource == QrKind.Mailbox;

    /// <summary>二维码区里那行随来源变化的输入提示。</summary>
    public string QrInputHint => SelectedQrSource switch
    {
        QrKind.DeviceCode => "二维码内容直接取自设备码，无需额外信息。",
        QrKind.PhoneScan => "二维码由设备按下面这个手机号生成，必须与设备上预留的号码一致。",
        QrKind.Mailbox => "二维码由设备按下面这个邮箱生成；留空则用设备上已预留的邮箱。",
        _ => string.Empty,
    };

    /// <summary>二维码区顶部那行"当前会作用在哪台设备"的提示。
    /// 生成二维码只取清单第一台，不写清楚现场很容易给错设备做码。</summary>
    public string QrTargetHint
    {
        get
        {
            var dev = Targets.FirstOrDefault();
            if (dev == null) return "清单里还没有设备：先到「设备发现」页勾选并投递过来。";

            string who = string.IsNullOrWhiteSpace(dev.SerialNo) ? dev.UniformDevID : dev.SerialNo;
            return Targets.Count == 1
                ? $"目标：{who}（{dev.IpMaskText}）"
                : $"目标：清单第 1 台 {who}（{dev.IpMaskText}）—— 清单里共 {Targets.Count} 台，二维码只认第一台。";
        }
    }

    public BitmapSource? QrImage { get => _qrImage; private set => Set(ref _qrImage, value); }

    public string QrContent { get => _qrContent; private set => Set(ref _qrContent, value); }

    public string QrMeta { get => _qrMeta; private set => Set(ref _qrMeta, value); }

    public string QrNotice { get => _qrNotice; private set => Set(ref _qrNotice, value); }

    public string QrVersionText { get => _qrVersionText; private set => Set(ref _qrVersionText, value); }

    public bool HasQr
    {
        get => _hasQr;
        private set
        {
            if (!Set(ref _hasQr, value)) return;
            SaveQrCommand.RaiseCanExecuteChanged();
            CopyQrContentCommand.RaiseCanExecuteChanged();
            CopyQrImageCommand.RaiseCanExecuteChanged();
            ClearQrCommand.RaiseCanExecuteChanged();
        }
    }

    private QrPayload? _lastPayload;

    private void SetQrNotice(string text, bool isError)
    {
        QrNotice = text;
        if (isError)
        {
            HasQr = false;
            QrImage = null;
            QrContent = string.Empty;
            QrVersionText = string.Empty;
            QrMeta = string.Empty;
        }
    }

    private void ClearQr()
    {
        HasQr = false;
        QrImage = null;
        QrContent = string.Empty;
        QrMeta = string.Empty;
        QrVersionText = string.Empty;
        _lastPayload = null;
        QrNotice = "已清除。选择来源后点「生成二维码」。";
    }

    private string DefaultQrFileName(string ext)
    {
        var dev = QrTarget;
        string who = dev == null ? "device" : SafeName(dev.SerialNo.Length > 0 ? dev.SerialNo : dev.IpMaskText);
        string kind = _lastPayload?.Kind switch
        {
            QrKind.PhoneScan => "PhoneQr",
            QrKind.Mailbox => "MailQr",
            _ => "DeviceCodeQr",
        };
        return $"{kind}_{who}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
    }

    private static string SafeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "device";
        var chars = raw.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private void SaveQr()
    {
        if (QrImage == null) { Toast("还没有可保存的二维码", true); return; }
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存二维码图片",
                Filter = "PNG 图片 (*.png)|*.png|BMP 图片 (*.bmp)|*.bmp",
                FileName = DefaultQrFileName(".png"),
                DefaultExt = ".png",
            };
            string dir = string.IsNullOrWhiteSpace(App.Settings.OutputDir) ? AppPaths.OutputDir : App.Settings.OutputDir;
            try { if (Directory.Exists(dir)) dlg.InitialDirectory = dir; } catch { /* 忽略 */ }

            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;

            // 一律按 scale=8 重新编码，而不是直接存屏幕上那张低倍图：
            // 界面为了塞进卡片把码压到 ~190px，拿去打印或发给甲方会糊，
            // 而二维码是"扫不出来就得重来一遍现场操作"的东西，宁可存大一点。
            var qr = EncodeQr(QrContent).qr;

            if (path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                // BMP 走自写编码器（不依赖 WPF 图像管线，自检环境也能落盘）
                QrEncoder.WriteBmp(qr, path, 8, 4);
            }
            else
            {
                var hiRes = QrEncoder.ToBitmap(qr, 8, 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(hiRes));
                using var fs = File.Create(path);
                encoder.Save(fs);
            }

            Log.Success($"二维码已保存：{path}", LogSource);
            Toast($"二维码已保存：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error("保存二维码失败", ex, LogSource);
            Toast($"保存失败：{ex.Message}", true);
        }
    }

    private void CopyQrContent()
    {
        if (!HasQr) { Toast("还没有可复制的内容", true); return; }
        try
        {
            System.Windows.Clipboard.SetText(QrContent);
            Toast($"已复制二维码内容（{QrContent.Length} 字符）");
        }
        catch (Exception ex)
        {
            Toast($"复制失败：{ex.Message}", true);
        }
    }

    private void CopyQrImage()
    {
        if (QrImage == null) { Toast("还没有可复制的二维码", true); return; }
        try
        {
            System.Windows.Clipboard.SetImage(QrImage);
            Toast("二维码图片已复制到剪贴板，可直接粘贴进文档");
        }
        catch (Exception ex)
        {
            Toast($"复制失败：{ex.Message}", true);
        }
    }

    private RelayCommand? _copyRecoveryCommand;
    public RelayCommand CopyRecoveryCommand => _copyRecoveryCommand ??= new(() =>
    {
        if (Recovery.Count == 0) { Toast("还没有可复制的内容", true); return; }
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in Recovery)
            {
                sb.AppendLine($"目标：{r.Target}  ({r.Address})");
                if (!string.IsNullOrEmpty(r.DeviceCode)) sb.AppendLine($"  设备码：{r.DeviceCode}");
                if (!string.IsNullOrEmpty(r.Guid)) sb.AppendLine($"  GUID：{r.Guid}");
                if (!string.IsNullOrEmpty(r.Ability)) sb.AppendLine($"  已配置找回方式：{r.Ability}");
                if (!string.IsNullOrEmpty(r.DeviceCodeError)) sb.AppendLine($"  失败原因：{r.DeviceCodeError}");
                sb.AppendLine();
            }
            System.Windows.Clipboard.SetText(sb.ToString());
            Toast($"已复制 {Recovery.Count} 条救援信息");
        }
        catch (Exception ex)
        {
            Toast($"复制失败：{ex.Message}", true);
        }
    });

    protected override void OnTargetsChanged()
    {
        base.OnTargetsChanged();
        LoadQrCommand.RaiseCanExecuteChanged();
        ReadMailboxCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(QrTargetHint));
    }

    // ==================== 执行 ====================

    protected override string? Validate()
    {
        if (string.IsNullOrEmpty(NewPassword)) return "请先设置新口令";
        if (!string.IsNullOrEmpty(ConfirmPassword) && NewPassword != ConfirmPassword) return "两次输入的新口令不一致";

        switch (ResetType)
        {
            case SadpConst.RESET_BY_QRCODE when string.IsNullOrWhiteSpace(Code):
                return "请填写重置口令（可点「读取设备码」+「生成二维码」，手机扫码后把口令贴回来）";
            case SadpConst.RESET_BY_FILE when string.IsNullOrWhiteSpace(AuthFile) || !File.Exists(AuthFile):
                return "请选择有效的授权文件（由海康官方密码重置工具导出）";
            case SadpConst.RESET_BY_GUID when string.IsNullOrWhiteSpace(Guid):
                return "请填写设备的 GUID 字符串";
            case SadpConst.RESET_BY_MAILBOX when string.IsNullOrWhiteSpace(MailBox):
                return "请填写设备预留的邮箱地址（可点「读取预留邮箱」自动获取）";
            case SadpConst.RESET_BY_MAILBOX when string.IsNullOrWhiteSpace(Code):
                return "请填写邮箱收到的安全码";
            case SadpConst.RESET_BY_PHONE when string.IsNullOrWhiteSpace(PhoneNo):
                return "请填写设备上预留的手机号（生成二维码要用它）";
            case SadpConst.RESET_BY_PHONE when string.IsNullOrWhiteSpace(Code):
                return "请填写手机扫码后给出的重置口令";
            case SadpConst.RESET_BY_QUESTION:
                if (Answers.Count == 0 || Answers.All(a => string.IsNullOrWhiteSpace(a.Answer)))
                    return "请至少填写一个安全问题的答案";
                break;
        }

        if (!Strength.Acceptable)
            Log.Warn($"新口令强度偏弱（{Strength.Hint}），设备可能拒绝；如被拒请改用强口令", LogSource);

        return null;
    }

    protected override (bool ok, string message) Execute(DiscoveredDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.UniformDevID))
            return (false, "设备唯一标识为空，无法重置");

        var input = new ResetInput
        {
            ResetType = ResetType,
            NewPassword = NewPassword,
            SyncIpcPassword = SyncIpcPassword,
            Code = Code?.Trim() ?? string.Empty,
            AuthFile = AuthFile?.Trim() ?? string.Empty,
            Guid = Guid?.Trim() ?? string.Empty,
            MailBox = MailBox?.Trim() ?? string.Empty,
            PhoneNo = PhoneNo?.Trim() ?? string.Empty,
        };
        foreach (var a in Answers)
        {
            if (!string.IsNullOrWhiteSpace(a.Answer)) input.Answers.Add(a.Answer.Trim());
        }

        // 防御：明明要 3 个答案却只填了 1 个，设备会直接判失败并消耗一次尝试机会
        if (ResetType == SadpConst.RESET_BY_QUESTION && input.Answers.Count < 2)
            return (false, "安全问题答案不足 2 个，为避免浪费设备尝试次数已跳过");

        var (ok, msg, retry, lockMinutes) = App.Sadp.ResetPasswd(device.UniformDevID, input);
        if (ok) return (true, "密码重置成功，请用新口令登录设备验证");

        // 剩余尝试次数很关键：SADP 连续失败会把设备锁定，必须显式告诉用户
        var tail = new List<string>();
        if (retry > 0) tail.Add($"剩余可尝试 {retry} 次");
        if (lockMinutes > 0) tail.Add($"设备剩余锁定 {lockMinutes} 分钟");
        return (false, tail.Count > 0 ? $"{msg}（{string.Join("，", tail)}）" : msg);
    }

    protected override void AfterRun()
    {
        if (FailCount > 0)
            Log.Warn($"密码重置完成：成功 {SuccessCount} 台，失败 {FailCount} 台。失败设备请核对设备码/答案后再试，避免触发锁定", LogSource);
    }

    public override void OnEnter()
    {
        // 二维码区依赖"清单里有没有设备"，切页时刷一下按钮可用性
        LoadQrCommand.RaiseCanExecuteChanged();
        ReadMailboxCommand.RaiseCanExecuteChanged();
    }

    /// <summary>本页不适用"投递未激活设备"，隐藏掉以免误操作。</summary>
    public bool ShowNotActivatedShortcut => false;
}

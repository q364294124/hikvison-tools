using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using HikDeployTool.Models;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>SDK 日志等级下拉项。</summary>
public sealed class LogLevelOption
{
    public int Value { get; init; }
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// 设置页：全局参数 + 环境自检 + 诊断工具。
///
/// 这一页承担"出问题时用户第一个该来的地方"的角色，所以自检结果不是简单的一行绿色对勾，
/// 而是每条都给"现象 + 原因 + 怎么办"，并且能一键复制成文本发给技术支持。
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private const string Source = "设置";

    private bool _checkedOnce;
    private bool _isChecking;
    private string _envSummary = string.Empty;
    private string _sadpStatus = string.Empty;
    private string _netSdkStatus = string.Empty;

    private RelayCommand? _saveCommand;
    private RelayCommand? _resetDefaultsCommand;
    private RelayCommand? _runCheckCommand;
    private RelayCommand? _copyReportCommand;
    private RelayCommand? _restartAsAdminCommand;
    private RelayCommand? _openBaseDirCommand;
    private RelayCommand? _openOutputDirCommand;
    private RelayCommand? _openSdkLogDirCommand;
    private RelayCommand? _openSettingsFileCommand;
    private RelayCommand? _initNetSdkCommand;
    private RelayCommand? _pickOutputDirCommand;
    private RelayCommand? _pickSdkLogDirCommand;

    public SettingsViewModel()
    {
        LogLevelOptions =
        [
            new LogLevelOption { Value = 0, Label = "0 · 关闭" },
            new LogLevelOption { Value = 1, Label = "1 · 仅致命错误" },
            new LogLevelOption { Value = 2, Label = "2 · 错误" },
            new LogLevelOption { Value = 3, Label = "3 · 警告（默认）" },
            new LogLevelOption { Value = 4, Label = "4 · 信息" },
            new LogLevelOption { Value = 5, Label = "5 · 调试" },
            new LogLevelOption { Value = 6, Label = "6 · 全部（日志极大）" },
        ];
    }

    /// <summary>直接暴露设置对象给界面双向绑定，避免为 12 个字段各写一层转发属性。</summary>
    public AppSettings Settings => App.Settings;

    public IReadOnlyList<LogLevelOption> LogLevelOptions { get; }

    public ObservableCollection<EnvironmentItem> EnvironmentItems { get; } = [];

    public override string Title => "设置与自检";

    public override string Glyph => "IconSettings";

    public override string Description => "全局参数、输出目录、运行环境自检与一键提权";

    public override string Usage => "换新机器部署时，先点「重新自检」；有红色「缺失」项就先补齐依赖库再开始干活。";

    // ==================== 自检 ====================

    public bool IsChecking
    {
        get => _isChecking;
        private set => Set(ref _isChecking, value);
    }

    public string EnvSummary { get => _envSummary; private set => Set(ref _envSummary, value); }

    public int CriticalCount => EnvironmentItems.Count(i => !i.Ok && i.Critical);

    public int WarningCount => EnvironmentItems.Count(i => !i.Ok && !i.Critical);

    public bool HasCritical => CriticalCount > 0;

    public string SadpStatus { get => _sadpStatus; private set => Set(ref _sadpStatus, value); }

    public string NetSdkStatus { get => _netSdkStatus; private set => Set(ref _netSdkStatus, value); }

    public bool IsAdministrator => EnvironmentCheck.IsAdministrator();

    public string PrivilegeText => IsAdministrator
        ? "当前已是管理员权限，SADP 链路层收发正常"
        : "当前为普通权限。SADP 需要收发链路层广播包，普通权限下会返回 2040（建议提权）";

    /// <summary>执行一次环境自检。返回问题条数，供主窗口状态栏显示。</summary>
    public int RunCheck()
    {
        IsChecking = true;
        try
        {
            var items = EnvironmentCheck.Run();
            EnvironmentItems.Clear();
            foreach (var item in items) EnvironmentItems.Add(item);

            int critical = items.Count(i => !i.Ok && i.Critical);
            int warn = items.Count(i => !i.Ok && !i.Critical);
            EnvSummary = critical == 0 && warn == 0
                ? $"{items.Count} 项检查全部通过"
                : $"{items.Count} 项检查：{critical} 项缺失、{warn} 项警告";

            RefreshStatusText();
            _checkedOnce = true;

            var n = critical + warn;
            foreach (var name in new[]
                     {
                         nameof(CriticalCount), nameof(WarningCount), nameof(HasCritical),
                         nameof(IsAdministrator), nameof(PrivilegeText),
                     })
                OnPropertyChanged(name);

            Log.Info($"环境自检完成：{EnvSummary}", Source);
            return n;
        }
        catch (Exception ex)
        {
            Log.Error("环境自检失败", ex, Source);
            EnvSummary = $"自检过程出错：{ex.Message}";
            return 1;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void RefreshStatusText()
    {
        var sadp = App.Sadp;
        SadpStatus = sadp.IsRunning
            ? $"SADP 已启动 · 版本 {sadp.VersionText} · 已发现 {App.Devices.Count} 台"
            : $"SADP 未启动 · 版本 {sadp.VersionText}";

        NetSdkStatus = App.NetSdk.IsInitialized
            ? "设备网络 SDK 已初始化，可以执行验收与登录操作"
            : "设备网络 SDK 尚未初始化（首次执行验收或登录时会自动初始化）";
    }

    // ==================== 命令 ====================

    public RelayCommand SaveCommand => _saveCommand ??= new(() =>
    {
        try
        {
            ClampSettings();
            App.SaveSettings();
            App.ApplySdkLogSettings();
            RefreshStatusText();
            Log.Success("设置已保存", Source);
            Toast("设置已保存");
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex, Source);
            Toast($"保存失败：{ex.Message}", true);
        }
    });

    public RelayCommand ResetDefaultsCommand => _resetDefaultsCommand ??= new(() =>
    {
        var s = Settings;
        s.DefaultUserName = "admin";
        s.SdkLogLevel = 3;
        s.MaxParallelism = 4;
        s.ConnectTimeoutMs = 5000;
        s.CaptureMaxChannels = 4;
        s.CaptureEnabled = true;
        s.DefaultPort = 8000;
        s.SdkLogDir = AppPaths.SdkLogDirDefault;
        s.OutputDir = AppPaths.OutputDir;
        App.SaveSettings();
        OnPropertyChanged(nameof(Settings));
        Log.Info("已恢复默认设置（项目名称与单位保留）", Source);
        Toast("已恢复默认设置");
    });

    /// <summary>
    /// 把用户输入夹到安全区间。
    /// 这些值会直接进 SDK 接口（超时、并发数），现场手滑输个 0 或 9999 都会引发很难懂的失败，
    /// 所以在保存前统一收口，而不是等到调用时报错。
    /// </summary>
    private static void ClampSettings()
    {
        var s = App.Settings;
        s.MaxParallelism = Math.Clamp(s.MaxParallelism, 1, 32);
        s.ConnectTimeoutMs = Math.Clamp(s.ConnectTimeoutMs, 500, 60000);
        s.CaptureMaxChannels = Math.Clamp(s.CaptureMaxChannels, 0, 64);
        s.SdkLogLevel = Math.Clamp(s.SdkLogLevel, 0, 6);
        if (s.DefaultPort == 0) s.DefaultPort = 8000;
        if (string.IsNullOrWhiteSpace(s.OutputDir)) s.OutputDir = AppPaths.OutputDir;
        if (string.IsNullOrWhiteSpace(s.SdkLogDir)) s.SdkLogDir = AppPaths.SdkLogDirDefault;
    }

    public RelayCommand RunCheckCommand => _runCheckCommand ??= new(() =>
    {
        int issues = RunCheck();
        Host?.SetEnvIssueCount(issues);
        Toast(issues == 0 ? "环境自检全部通过" : $"自检发现 {issues} 项需要处理", issues > 0);
    });

    public RelayCommand CopyReportCommand => _copyReportCommand ??= new(() =>
    {
        if (EnvironmentItems.Count == 0) RunCheck();
        try
        {
            var text = EnvironmentCheck.ToText(EnvironmentItems)
                       + Environment.NewLine
                       + $"操作系统：{Environment.OSVersion}"
                       + Environment.NewLine
                       + $".NET：{Environment.Version}"
                       + Environment.NewLine
                       + $"程序目录：{AppPaths.BaseDir}";
            System.Windows.Clipboard.SetText(text);
            Toast("自检报告已复制，可直接粘贴给技术支持");
        }
        catch (Exception ex)
        {
            Toast($"复制失败：{ex.Message}", true);
        }
    });

    /// <summary>
    /// 以管理员身份重启。
    /// app.manifest 刻意用 asInvoker：现场很多机器上 UAC 会被运维策略拦掉，
    /// 强制 requireAdministrator 会让程序干脆启动不了；改为运行时申请，被拒也还能用。
    /// </summary>
    public RelayCommand RestartAsAdminCommand => _restartAsAdminCommand ??= new(
        () =>
        {
            if (EnvironmentCheck.RestartAsAdministrator())
            {
                Log.Info("正在以管理员身份重启…", Source);
                System.Windows.Application.Current?.Shutdown();
            }
            else
            {
                Toast("提权被取消。可手动右键 exe →「以管理员身份运行」", true);
            }
        },
        () => !IsAdministrator);

    public RelayCommand OpenBaseDirCommand => _openBaseDirCommand ??= new(() =>
        EnvironmentCheck.OpenFolder(AppPaths.BaseDir));

    public RelayCommand OpenOutputDirCommand => _openOutputDirCommand ??= new(() =>
        EnvironmentCheck.OpenFolder(Settings.OutputDir));

    public RelayCommand OpenSdkLogDirCommand => _openSdkLogDirCommand ??= new(() =>
        EnvironmentCheck.OpenFolder(Settings.SdkLogDir));

    public RelayCommand OpenSettingsFileCommand => _openSettingsFileCommand ??= new(() =>
    {
        try
        {
            App.SaveSettings();
            Process.Start(new ProcessStartInfo(AppPaths.SettingsFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast($"打开配置文件失败：{ex.Message}", true);
        }
    });

    public RelayCommand PickOutputDirCommand => _pickOutputDirCommand ??= new(() =>
    {
        var dir = PickFolder("选择报告与导出目录", Settings.OutputDir);
        if (dir == null) return;
        Settings.OutputDir = dir;
        App.SaveSettings();
        OnPropertyChanged(nameof(Settings));
        Toast("输出目录已更新");
    });

    public RelayCommand PickSdkLogDirCommand => _pickSdkLogDirCommand ??= new(() =>
    {
        var dir = PickFolder("选择 SDK 日志目录", Settings.SdkLogDir);
        if (dir == null) return;
        Settings.SdkLogDir = dir;
        App.SaveSettings();
        App.ApplySdkLogSettings();
        OnPropertyChanged(nameof(Settings));
        Toast("SDK 日志目录已更新");
    });

    /// <summary>提前初始化设备网络 SDK，把"缺库 / 端口被占"之类的问题在验收之前就暴露出来。</summary>
    public RelayCommand InitNetSdkCommand => _initNetSdkCommand ??= new(() =>
    {
        try
        {
            var (ok, message) = App.NetSdk.Initialize(
                Settings.ConnectTimeoutMs,
                sdkLogDir: Settings.SdkLogDir,
                sdkLogLevel: Settings.SdkLogLevel);

            if (ok)
            {
                Log.Success($"设备网络 SDK 初始化成功：{message}", Source);
                Toast("设备网络 SDK 初始化成功");
            }
            else
            {
                Log.Error($"设备网络 SDK 初始化失败：{message}", Source);
                Toast($"初始化失败：{message}", true);
            }
            RefreshStatusText();
            OnPropertyChanged(nameof(NetSdkStatus));
        }
        catch (Exception ex)
        {
            Log.Error("初始化设备网络 SDK 异常", ex, Source);
            Toast($"初始化异常：{ex.Message}", true);
        }
    });

    private static string? PickFolder(string title, string current)
    {
        // WPF 没有内置的文件夹选择对话框，用 OpenFileDialog 的"选文件夹"技巧无需引入第三方包
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "选择此文件夹",
            Filter = "文件夹|*.folder",
        };

        try
        {
            if (Directory.Exists(current))
            {
                dialog.InitialDirectory = current;
            }
        }
        catch
        {
            // 目录不存在就用系统默认位置
        }

        if (dialog.ShowDialog() != true) return null;
        var path = Path.GetDirectoryName(dialog.FileName);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public override void OnEnter()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(IsAdministrator));
        OnPropertyChanged(nameof(PrivilegeText));
        RefreshStatusText();

        // 首次进入自动跑一遍自检，用户不必先点按钮
        if (!_checkedOnce && !IsChecking)
        {
            int issues = RunCheck();
            Host?.SetEnvIssueCount(issues);
        }
    }

    private static LogService Log => App.Log;
}

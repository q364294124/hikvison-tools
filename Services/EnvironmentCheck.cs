using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace HikDeployTool.Services;

/// <summary>一项环境自检结果。</summary>
public sealed class EnvironmentItem
{
    public string Name { get; init; } = string.Empty;
    public bool Ok { get; init; }
    public bool Critical { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;

    public string StatusText => Ok ? "正常" : Critical ? "缺失" : "警告";
}

/// <summary>
/// 运行环境自检。
///
/// 海康 SDK 的部署坑几乎都集中在"依赖库是否齐全"和"权限是否足够"两件事上，
/// 与其等到调用接口时报一个看不懂的错误码，不如启动时先体检一遍，
/// 把问题连同解决办法一起摆出来。
/// </summary>
public static class EnvironmentCheck
{
    /// <summary>SADP 必需/可选的原生库。</summary>
    private static readonly (string File, bool Critical, string Hint)[] SadpFiles =
    [
        ("Sadp.dll", true, "SADP SDK 主库，请从 HCSadpSDK/lib/win64 复制"),
        ("HCSadpSDK.xml", true, "SADP 运行时配置，必须与 Sadp.dll 同级，否则加载参数会失败"),
        ("HCNetSDK.dll", true, "SADP 内部依赖设备网络 SDK，缺了会导致启动失败"),
        ("HCOTAPDCM.dll", false, "OTAP 探测组件，涉及链路协议设备时才需要"),
        ("libcrypto-3-x64.dll", true, "OpenSSL 3.x 加密库（注意不是 1_1 版本）"),
        ("libssl-3-x64.dll", true, "OpenSSL 3.x SSL 库（注意不是 1_1 版本）"),
        ("zlib1.dll", false, "压缩库"),
    ];

    /// <summary>HCNetSDK 必需/可选的原生库。</summary>
    private static readonly (string File, bool Critical, string Hint)[] NetSdkFiles =
    [
        ("HCNetSDK.dll", true, "设备网络 SDK 主库"),
        ("HCCore.dll", true, "SDK 核心库，HCNetSDK.dll 的依赖"),
        ("HCNetSDKCom", true, "功能组件目录（整体复制，目录名不可改）"),
        ("libcrypto-3-x64.dll", true, "OpenSSL 3.x 加密库"),
        ("libssl-3-x64.dll", true, "OpenSSL 3.x SSL 库"),
        ("zlib1.dll", false, "压缩库"),
        ("hlog.dll", false, "SDK 日志组件"),
        ("hpr.dll", false, "SDK 子系统组件"),
    ];

    public static List<EnvironmentItem> Run()
    {
        var items = new List<EnvironmentItem>();

        // ---- 权限 ----
        bool admin = IsAdministrator();
        items.Add(new EnvironmentItem
        {
            Name = "管理员权限",
            Ok = admin,
            Critical = false,
            Detail = admin ? "当前已是管理员权限" : "当前为普通用户权限",
            Hint = admin
                ? string.Empty
                : "SADP 需要操作网卡收发广播包，普通权限下会返回 2040。点状态栏的「以管理员身份重启」即可。",
        });

        // ---- 依赖库 ----
        foreach (var (file, critical, hint) in SadpFiles)
            items.Add(CheckPath(file, critical, "SADP", hint));
        foreach (var (file, critical, hint) in NetSdkFiles)
            items.Add(CheckPath(file, critical, "NetSDK", hint));

        // ---- WinPcap / Npcap ----
        items.Add(CheckWpcap());

        // ---- SDK 结构体布局 ----
        var layout = Native.StructLayoutCheck.Run();
        var bad = layout.Where(x => !x.Ok).ToList();
        items.Add(new EnvironmentItem
        {
            Name = "结构体布局自检",
            Ok = bad.Count == 0,
            Critical = true,
            Detail = bad.Count == 0
                ? $"{layout.Count} 个结构体与头文件推算大小完全一致"
                : string.Join("；", bad.Select(x => $"{x.Name} 期望 {x.Expected} 实际 {x.Actual}")),
            Hint = bad.Count == 0
                ? string.Empty
                : "托管结构体与 C 头文件不一致，读写设备数据会错位，必须修正后再使用。",
        });

        // ---- 旧版 OpenSSL 干扰 ----
        items.Add(CheckSystem32OpenSsl());

        return items;
    }

    private static EnvironmentItem CheckPath(string relative, bool critical, string group, string hint)
    {
        var full = Path.Combine(AppPaths.BaseDir, relative);
        bool exists = File.Exists(full) || Directory.Exists(full);
        return new EnvironmentItem
        {
            Name = $"{group} · {relative}",
            Ok = exists,
            Critical = critical,
            Detail = exists ? "已就位" : "未找到",
            Hint = exists ? string.Empty : hint,
        };
    }

    private static EnvironmentItem CheckWpcap()
    {
        // SADP 通过 WinPcap/Npcap 收发链路层报文
        bool found = false;
        string detail = "未检测到";

        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpcap.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "wpcap.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap", "wpcap.dll"),
        };
        foreach (var c in candidates.Where(c => !string.IsNullOrEmpty(c)))
        {
            if (File.Exists(c)) { found = true; detail = c; break; }
        }

        if (!found)
        {
            // 已安装 Npcap 时通常能在注册表/服务中看到 npcap 服务
            try
            {
                var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (services?.GetSubKeyNames().Any(n => n.Contains("npcap", StringComparison.OrdinalIgnoreCase)
                                                        || n.Contains("npf", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    found = true;
                    detail = "检测到 npcap/npf 服务已安装（wpcap.dll 未在系统目录中被直接找到）";
                }
            }
            catch { /* 忽略注册表访问失败 */ }
        }

        return new EnvironmentItem
        {
            Name = "WinPcap / Npcap",
            Ok = found,
            Critical = false,
            Detail = detail,
            Hint = found
                ? string.Empty
                : "SADP 依赖 wpcap.dll 做链路层收发。缺了会返回 2030。安装 Npcap（勾选 WinPcap 兼容模式）后重启程序。",
        };
    }

    /// <summary>
    /// 检查 System32 里是否存在旧版 OpenSSL。
    /// 旧版 libssl-1_1 / libeay32 之类如果先被解析，会和 SDK 自带的 3.x 冲突，
    /// 表现为登录失败或直接崩溃，是最难排查的一类问题。
    /// </summary>
    private static EnvironmentItem CheckSystem32OpenSsl()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var legacy = new[] { "libssl-1_1-x64.dll", "libcrypto-1_1-x64.dll", "libeay32.dll", "ssleay32.dll" };
        var hits = legacy.Where(f => File.Exists(Path.Combine(sys, f))).ToList();

        return new EnvironmentItem
        {
            Name = "System32 旧版 OpenSSL 冲突检查",
            Ok = hits.Count == 0,
            Critical = false,
            Detail = hits.Count == 0 ? "未发现冲突" : "发现：" + string.Join("、", hits),
            Hint = hits.Count == 0
                ? string.Empty
                : "系统目录里的旧版 OpenSSL 可能被优先加载（DLL 搜索顺序），导致连接设备失败。建议将其移走或改名后重试。",
        };
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以管理员身份重新启动本程序（触发 UAC）。</summary>
    public static bool RestartAsAdministrator()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppPaths.BaseDir,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"提权重启被取消或失败：{ex.Message}", "环境");
            return false;
        }
    }

    /// <summary>打开目录（用资源管理器）。</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"打开目录失败：{ex.Message}", "环境");
        }
    }

    /// <summary>生成自检结果文本，便于用户复制反馈。</summary>
    public static string ToText(IEnumerable<EnvironmentItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"海康设备交付运维工具 环境自检  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 60));
        foreach (var i in items)
        {
            sb.AppendLine($"[{i.StatusText}] {i.Name}：{i.Detail}");
            if (!i.Ok && !string.IsNullOrEmpty(i.Hint)) sb.AppendLine($"        处理建议：{i.Hint}");
        }
        return sb.ToString();
    }
}

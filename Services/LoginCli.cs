using System.IO;
using System.Text;

namespace HikDeployTool.Services;

/// <summary>
/// 命令行登录测试：
/// <code>
///   HikDeployTool.exe --logintest 192.168.1.104 admin 密码 [SDK端口] [HTTP端口]
/// </code>
/// 位置参数依次是 IP、用户名、口令、SDK 端口（省略按 8000）、HTTP 端口（省略按 80）。
///
/// 现场排查"登录不上"时不用来回切界面：一条命令跑完整的登录诊断链
/// （私有协议 → ISAPI 回退 → 端口探测 → 多次重试），结果同时打印到
/// 控制台并写入程序目录的 logintest.txt，方便截图发回来。
///
/// 退出码：0 = 登录成功；1 = 登录失败；2 = 参数不对。
/// </summary>
public static class LoginCli
{
    public const string Switch = "--logintest";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        int idx = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        var rest = args[(idx + 1)..].Where(a => !a.StartsWith('-')).ToList();

        if (rest.Count is < 3 or > 5)
        {
            var usage = "用法：HikDeployTool.exe --logintest <IP> <用户名> <口令> [SDK端口] [HTTP端口]\n" +
                        "示例：HikDeployTool.exe --logintest 192.168.1.104 admin Abc12345 8000 80";
            Console.WriteLine(usage);
            WriteReport(usage);
            return 2;
        }

        string ip = rest[0];
        string user = rest[1];
        string password = rest[2];
        ushort port = rest.Count >= 4 && ushort.TryParse(rest[3], out var p) ? p : (ushort)8000;
        ushort http = rest.Count == 5 && ushort.TryParse(rest[4], out var h) ? h : (ushort)80;

        var sb = new StringBuilder();
        sb.AppendLine($"登录测试  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"目标：{ip}（SDK {port} / HTTP {http}）  用户：{user}");
        sb.AppendLine(new string('-', 50));

        var sdk = AppState.Current.NetSdk;
        var init = sdk.Initialize(sdkLogDir: Path.Combine(AppContext.BaseDirectory, "sdk_log"));
        if (!init.ok)
        {
            sb.AppendLine($"[失败] SDK 初始化失败：{init.message}");
            Console.WriteLine(sb.ToString());
            WriteReport(sb.ToString());
            return 1;
        }

        sb.AppendLine("SDK 初始化成功，开始登录（私有协议失败会自动回退 ISAPI 并重试，最多约 15 秒）…");
        Console.WriteLine(sb.ToString());
        sb.Clear();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var session = sdk.Login(ip, port, user, password, http);
        sw.Stop();

        if (session.Ok)
        {
            sb.AppendLine($"[成功] 登录成功（耗时 {sw.ElapsedMilliseconds}ms，通道：{session.LoginModeText}）");
            sb.AppendLine($"  序列号：{session.SerialNo}");
            sb.AppendLine($"  通道：模拟 {session.ChannelCount} 路（{session.StartChannel} 起），IP 通道 {session.IpChannelCount} 路（{session.StartDigitalChannel} 起）");
            if (session.LoginMode == 1)
                sb.AppendLine("  ⚠ 设备拒绝了私有协议（8000）登录，本次走的 ISAPI/HTTP 通道——功能不受影响，" +
                              "但建议到设备网页端（配置→系统→安全）检查 SDK 服务的相关设置");
            if (session.PasswordLevel is 0 or 3)
                sb.AppendLine("  ⚠ 设备提示密码强度低（默认密码/弱密码），建议尽快改密");
            Console.WriteLine(sb.ToString());
            WriteReport(sb.ToString());
            sdk.Logout(session.UserId);
            return 0;
        }

        sb.AppendLine($"[失败] {session.Message}");
        if (session.ErrorCode > 0) sb.AppendLine($"  SDK 错误码：{session.ErrorCode}");
        Console.WriteLine(sb.ToString());
        WriteReport(sb.ToString());
        return 1;
    }

    /// <summary>结果落一份到程序目录，现场手机拍屏不方便时直接把这个文件发回来。</summary>
    private static void WriteReport(string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.BaseDir, "logintest.txt"), text);
        }
        catch
        {
            // 写不了报告不影响结论（目录只读等情况），控制台输出已经在
        }
    }
}

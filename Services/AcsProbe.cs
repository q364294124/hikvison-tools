using System.IO;
using System.Text;
using System.Xml.Linq;

namespace HikDeployTool.Services;

/// <summary>
/// 门禁 ISAPI 诊断：
/// <code>
///   HikDeployTool.exe --acsprobe 192.168.1.2 [用户名] [口令] [SDK端口] [HTTP端口]
/// </code>
/// 只给 IP 时自动从台账（assets.json）取已存的加密口令解密使用。
///
/// 背景：DS-K1T671BM 这类人脸门禁终端，不同固件对门状态查询的支持
/// 不一致（有的不认 /Door/{n}/status），远程控制的请求体根节点也
/// 有新旧两版写法。本命令逐个候选 URL 实测并落盘 acsprobe.txt，
/// 现场不用抓包就能看清"这台设备到底支持什么"。
///
/// 退出码：0 = 登录成功（探测结果看 acsprobe.txt）；1 = 失败；2 = 参数错。
/// </summary>
public static class AcsProbe
{
    public const string Switch = "--acsprobe";

    /// <summary>逐个试的门状态候选 URL（第一个成功的会被程序采用）。</summary>
    internal static readonly string[] DoorStatusCandidates =
    [
        "/ISAPI/AccessControl/Door/{0}/status",
        "/ISAPI/AccessControl/Door/status",
        "/ISAPI/AccessControl/Door/{0}/status/format/json",
        "/ISAPI/Door/{0}/status",
    ];

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        int idx = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
        var rest = args[(idx + 1)..].Where(a => !a.StartsWith('-')).ToList();

        if (rest.Count is 0 or > 5)
        {
            var usage = "用法：HikDeployTool.exe --acsprobe <IP> [用户名] [口令] [SDK端口] [HTTP端口]\n" +
                        "只给 IP 时自动用台账里已保存的口令。示例：HikDeployTool.exe --acsprobe 192.168.1.2";
            Console.WriteLine(usage);
            WriteReport(usage);
            return 2;
        }

        string ip = rest[0];
        string user = rest.Count >= 2 ? rest[1] : "admin";
        string? password = rest.Count >= 3 ? rest[2] : null;
        ushort port = rest.Count >= 4 && ushort.TryParse(rest[3], out var p) ? p : (ushort)8000;
        ushort http = rest.Count == 5 && ushort.TryParse(rest[4], out var h) ? h : (ushort)80;

        var sb = new StringBuilder();
        sb.AppendLine($"门禁 ISAPI 诊断  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"目标：{ip}（SDK {port} / HTTP {http}）  用户：{user}");
        sb.AppendLine(new string('-', 60));

        // 没给口令 → 台账里找（序列号优先、IP 兜底），解密失败就停
        if (password is null)
        {
            password = FindStoredPassword(ip);
            if (password is null)
            {
                sb.AppendLine("[失败] 台账里没有这台设备的口令，请带用户名口令重跑：" +
                              $"HikDeployTool.exe {Switch} {ip} admin 口令");
                Console.WriteLine(sb.ToString());
                WriteReport(sb.ToString());
                return 1;
            }
            sb.AppendLine("口令：来自台账已保存凭据");
        }

        var sdk = AppState.Current.NetSdk;
        var init = sdk.Initialize(sdkLogDir: Path.Combine(AppContext.BaseDirectory, "sdk_log"));
        if (!init.ok)
        {
            sb.AppendLine($"[失败] SDK 初始化失败：{init.message}");
            Console.WriteLine(sb.ToString());
            WriteReport(sb.ToString());
            return 1;
        }

        var session = sdk.Login(ip, port, user, password, http);
        if (!session.Ok)
        {
            sb.AppendLine($"[失败] 登录失败：{session.Message}");
            Console.WriteLine(sb.ToString());
            WriteReport(sb.ToString());
            return 1;
        }

        sb.AppendLine($"[成功] 登录成功（{session.LoginModeText}），开始逐个探测 ISAPI 端点…");
        sb.AppendLine();

        try
        {
            // 1. 设备信息（确认链路通）
            Probe(sb, session.UserId, "GET /ISAPI/System/deviceInfo");

            // 2. 门禁能力集（看 DoorCap 结构和数量）
            Probe(sb, session.UserId, "GET /ISAPI/AccessControl/capabilities");

            // 3. 门状态候选 URL（哪个通了程序就用哪个）
            foreach (var tpl in DoorStatusCandidates)
                Probe(sb, session.UserId, "GET " + string.Format(tpl, 1));

            // 3b. 模块状态（人脸终端 isSupportModuleStatus=true，锁状态常走这里）
            Probe(sb, session.UserId, "GET /ISAPI/AccessControl/Module/status");

            // 4. 远程控制：新版根节点 RemoteControlDoor（历史上用过旧根节点被设备拒）
            //    用 cmd=close 测试——对已关闭的门无副作用，比 open 稳妥。
            Probe(sb, session.UserId, $"PUT /ISAPI/AccessControl/RemoteControl/door/1",
                "<RemoteControlDoor version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"><cmd>close</cmd></RemoteControlDoor>");

            // 5. 通行记录（确认事件查询可用）：界面用 AcsEventCond 根节点，
            //    这里把可能的写法都试一遍——命名空间/时间范围/顺序的差异。
            string searchId = Guid.NewGuid().ToString("N");
            Probe(sb, session.UserId, "POST /ISAPI/AccessControl/AcsEvent",
                "<AcsEventCond>" +
                $"<searchID>{searchId}</searchID>" +
                "<searchResultPosition>0</searchResultPosition>" +
                "<maxResults>10</maxResults></AcsEventCond>");
            Probe(sb, session.UserId, "POST /ISAPI/AccessControl/AcsEvent",
                "<AcsEventCond version=\"2.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\">" +
                $"<searchID>{searchId}</searchID>" +
                "<searchResultPosition>0</searchResultPosition>" +
                "<maxResults>10</maxResults></AcsEventCond>");
            string from = DateTime.Now.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss");
            string to = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            Probe(sb, session.UserId, "POST /ISAPI/AccessControl/AcsEvent",
                "<AcsEventCond>" +
                $"<searchID>{searchId}</searchID>" +
                "<searchResultPosition>0</searchResultPosition>" +
                "<maxResults>10</maxResults>" +
                "<timeRangeList><timeRange>" +
                $"<startTime>{from}</startTime><endTime>{to}</endTime>" +
                "</timeRange></timeRangeList></AcsEventCond>");
            Probe(sb, session.UserId, "POST /ISAPI/AccessControl/AcsEvent",
                "<AcsEventCond>" +
                $"<searchID>{searchId}</searchID>" +
                "<timeRangeList><timeRange>" +
                $"<startTime>{from}</startTime><endTime>{to}</endTime>" +
                "</timeRange></timeRangeList>" +
                "<searchResultPosition>0</searchResultPosition>" +
                "<maxResults>10</maxResults></AcsEventCond>");

            // 5b. 人脸终端社区实践：?format=json + JSON 请求体（设备实测：
            //     XML 一律 badJsonFormat；JSON 缺 major 会报 MessageParametersLack）。
            string jsonBody = "{" +
                "\"AcsEventCond\":{" +
                $"\"searchID\":\"{searchId}\"," +
                "\"searchResultPosition\":0," +
                "\"maxResults\":10," +
                "\"major\":0,\"minor\":0}}";
            Probe(sb, session.UserId, "POST /ISAPI/AccessControl/AcsEvent?format=json", jsonBody);
            string jsonBody2 = "{" +
                "\"AcsEventCond\":{" +
                $"\"searchID\":\"{searchId}\"," +
                "\"searchResultPosition\":0," +
                "\"maxResults\":10," +
                "\"major\":0,\"minor\":0," +
                $"\"startTime\":\"{from}\"," +
                $"\"endTime\":\"{to}\"}}}}";
            Probe(sb, session.UserId, "POST /ISAPI/AccessControl/AcsEvent?format=json", jsonBody2);
        }
        finally
        {
            sdk.Logout(session.UserId);
        }

        sb.AppendLine();
        sb.AppendLine("结论：上面「[通过]」的就是本机固件支持的端点；" +
                      "门状态用第一个通过的候选 URL，远程控制以 RemoteControlDoor 根节点为准。");
        Console.WriteLine(sb.ToString());
        WriteReport(sb.ToString());
        return 0;
    }

    private static void Probe(StringBuilder sb, int userId, string url, string? body = null)
    {
        var (ok, xml, msg) = body is null
            ? AppState.Current.NetSdk.IsapiGet(userId, url)
            : AppState.Current.NetSdk.IsapiPut(userId, url, body);

        sb.AppendLine($"[{(ok ? "通过" : "拒绝")}] {url}");
        if (!ok) sb.AppendLine($"        {msg}");
        if (!string.IsNullOrWhiteSpace(xml))
        {
            var text = xml.Trim();
            if (text.Length > 1200) text = text[..1200] + " …（截断）";
            foreach (var line in text.Replace("\r", "").Split('\n'))
                sb.AppendLine("        " + line);
        }
        sb.AppendLine();
    }

    /// <summary>从台账里找这台设备已存的口令（序列号优先、IP 兜底），找不到返回 null。</summary>
    private static string? FindStoredPassword(string ip)
    {
        try
        {
            var assets = JsonStore.Load<List<Models.AssetRecord>>(AppPaths.AssetsFile) ?? [];
            var hit = assets.FirstOrDefault(a => a.Ip == ip && !string.IsNullOrEmpty(a.PasswordEnc))
                   ?? assets.FirstOrDefault(a => a.Ip == ip);
            if (hit is null) return null;
            var plain = hit.PasswordEnc.Length > 0 ? SecretStore.Unprotect(hit.PasswordEnc) : string.Empty;
            return string.IsNullOrEmpty(plain) ? null : plain;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteReport(string text)
    {
        try { File.WriteAllText(Path.Combine(AppPaths.BaseDir, "acsprobe.txt"), text); }
        catch
        {
            // 写不了报告不影响结论（目录只读等情况），控制台输出已经在
        }
    }
}

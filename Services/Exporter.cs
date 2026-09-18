using System.IO;
using System.Text;
using HikDeployTool.Models;

namespace HikDeployTool.Services;

/// <summary>
/// 交付物导出：台账 CSV + 验收报告 HTML。
///
/// 验收报告刻意用单文件 HTML（内联样式、截图转 Base64）而不是 Word/PDF：
/// 现场没有 Office、也不一定装了 PDF 打印机，HTML 双击就能看、能直接发甲方，
/// 需要纸件时浏览器里 Ctrl+P 就能出 PDF。
/// </summary>
public static class Exporter
{
    // ==================== 资产台账 ====================

    private static readonly string[] AssetHeaders =
    [
        "台账编号", "设备名称", "安装位置", "序列号", "MAC地址", "IP地址", "SDK端口", "HTTP端口",
        "子网掩码", "网关", "设备型号", "固件版本", "DSP版本", "视频通道", "数字通道",
        "厂商", "激活状态", "管理员账号", "在线状态", "验收结论", "首次发现", "最近发现", "备注",
    ];

    public static string ExportAssets(IEnumerable<AssetRecord> assets, string path)
    {
        var rows = assets.Select(a => new List<string>
        {
            a.Id, a.Name, a.Location, a.SerialNo, a.Mac, a.Ip, a.Port.ToString(), a.HttpPort.ToString(),
            a.SubnetMask, a.Gateway, a.Model, a.FirmwareVersion, a.DspVersion,
            a.EncoderCount.ToString(), a.DigitalChannelCount.ToString(),
            a.Manufacturer, a.ActivateState, a.AdminUserName, a.Online, a.Acceptance,
            a.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss"), a.LastSeen.ToString("yyyy-MM-dd HH:mm:ss"), a.Note,
        }).ToList();

        CsvUtil.Write(path, AssetHeaders, rows);
        LogService.Current.Success($"台账已导出（{rows.Count} 条）：{path}", "导出");
        return path;
    }

    /// <summary>从 CSV 读回台账。返回 (成功条数, 错误信息)。</summary>
    public static (int count, string message) ImportAssets(string path)
    {
        if (!File.Exists(path)) return (0, "文件不存在");

        var rows = CsvUtil.Read(path);
        if (rows.Count == 0) return (0, "文件中没有数据行");

        int count = 0;
        foreach (var row in rows)
        {
            string Get(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                return string.Empty;
            }

            var id = Get("台账编号", "编号", "ID");
            var serial = Get("序列号", "SN", "SerialNo");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(serial)) continue;

            // 序列号优先去重，其次编号
            var exist = AppState.Current.Assets.FirstOrDefault(a =>
                (!string.IsNullOrWhiteSpace(serial) && a.SerialNo.Equals(serial, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(id) && a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

            var record = exist ?? new AssetRecord();
            if (string.IsNullOrWhiteSpace(record.Id)) record.Id = string.IsNullOrWhiteSpace(id) ? AppState.Current.NextAssetId() : id;

            record.Name = Get("设备名称", "名称") is { Length: > 0 } n ? n : record.Name;
            record.Location = Get("安装位置", "位置") is { Length: > 0 } loc ? loc : record.Location;
            record.SerialNo = Get("序列号", "SN", "SerialNo") is { Length: > 0 } sn ? sn : record.SerialNo;
            record.Mac = Get("MAC地址", "MAC") is { Length: > 0 } mac ? mac : record.Mac;
            record.Ip = Get("IP地址", "IP") is { Length: > 0 } ip ? ip : record.Ip;
            record.Gateway = Get("网关") is { Length: > 0 } gw ? gw : record.Gateway;
            record.SubnetMask = Get("子网掩码", "掩码") is { Length: > 0 } mask ? mask : record.SubnetMask;
            record.Model = Get("设备型号", "型号") is { Length: > 0 } model ? model : record.Model;
            record.FirmwareVersion = Get("固件版本", "版本") is { Length: > 0 } fw ? fw : record.FirmwareVersion;
            record.Manufacturer = Get("厂商") is { Length: > 0 } mf ? mf : record.Manufacturer;
            record.ActivateState = Get("激活状态") is { Length: > 0 } st ? st : record.ActivateState;
            record.AdminUserName = Get("管理员账号", "账号") is { Length: > 0 } u ? u : record.AdminUserName;
            record.Online = Get("在线状态") is { Length: > 0 } on ? on : record.Online;
            record.Acceptance = Get("验收结论") is { Length: > 0 } ac ? ac : record.Acceptance;
            record.Note = Get("备注") is { Length: > 0 } note ? note : record.Note;

            if (ushort.TryParse(Get("SDK端口", "端口"), out var port) && port > 0) record.Port = port;
            if (ushort.TryParse(Get("HTTP端口"), out var http) && http > 0) record.HttpPort = http;
            if (DateTime.TryParse(Get("首次发现"), out var first)) record.FirstSeen = first;
            if (DateTime.TryParse(Get("最近发现"), out var last)) record.LastSeen = last;

            if (exist == null) AppState.Current.Assets.Add(record);
            count++;
        }

        AppState.Current.ReindexAssetSequence();
        AppState.Current.SaveAssets();
        LogService.Current.Success($"台账已导入 {count} 条：{path}", "导入");
        return (count, $"成功导入 {count} 条");
    }

    // ==================== 验收报告 ====================

    /// <summary>生成单文件 HTML 验收报告，返回文件路径。</summary>
    public static string ExportAcceptanceReport(
        IEnumerable<AcceptanceRecord> records,
        string outputDir,
        string projectName,
        string reportedBy,
        string sdkNote = "")
    {
        var list = records.ToList();
        Directory.CreateDirectory(outputDir);

        var safeName = string.IsNullOrWhiteSpace(projectName) ? "设备验收" : Sanitize(projectName);
        var file = Path.Combine(outputDir, $"{safeName}_验收报告_{DateTime.Now:yyyyMMdd_HHmmss}.html");

        int total = list.Count;
        int passed = list.Count(r => r.ResultText == "通过");
        int partial = list.Count(r => r.ResultText == "部分未通过");
        int failed = list.Count(r => r.ResultText == "失败");
        int online = list.Count(r => r.Online);
        int channels = list.Sum(r => r.ChannelCount);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>{Esc(projectName)} 设备验收报告</title>");
        sb.AppendLine(ReportCss());
        sb.AppendLine("</head><body>");

        // ---------- 封面 ----------
        sb.AppendLine("<div class=\"cover\">");
        sb.AppendLine("<div class=\"logo\">设备交付验收报告</div>");
        sb.AppendLine($"<h1>{Esc(string.IsNullOrWhiteSpace(projectName) ? "视频监控系统工程" : projectName)}</h1>");
        sb.AppendLine("<table class=\"meta\">");
        sb.AppendLine($"<tr><th>报告生成时间</th><td>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</td></tr>");
        sb.AppendLine($"<tr><th>施工 / 验收单位</th><td>{Esc(string.IsNullOrWhiteSpace(reportedBy) ? "—" : reportedBy)}</td></tr>");
        sb.AppendLine($"<tr><th>验收设备总数</th><td>{total} 台</td></tr>");
        sb.AppendLine($"<tr><th>链路在线</th><td>{online} 台</td></tr>");
        sb.AppendLine($"<tr><th>验收结论</th><td>通过 {passed} 台 · 部分未通过 {partial} 台 · 失败 {failed} 台</td></tr>");
        if (!string.IsNullOrWhiteSpace(sdkNote)) sb.AppendLine($"<tr><th>SDK 环境</th><td>{Esc(sdkNote)}</td></tr>");
        sb.AppendLine("</table>");

        // 结论徽标：只要有一台失败就不是"全部通过"
        var overallClass = failed == 0 && partial == 0 && total > 0 ? "ok" : failed > 0 ? "bad" : "warn";
        var overallText = total == 0 ? "无数据" : failed == 0 && partial == 0 ? "全部通过" : failed > 0 ? "存在失败项" : "存在未通过项";
        sb.AppendLine($"<div class=\"verdict {overallClass}\">总体结论：{overallText}</div>");
        sb.AppendLine("</div>");

        // ---------- 汇总表 ----------
        sb.AppendLine("<h2>一、设备清单与验收结果</h2>");
        sb.AppendLine("<table class=\"grid\"><thead><tr>");
        foreach (var h in new[] { "序号", "IP地址", "序列号", "设备型号", "固件版本", "通道数", "设备时间", "检查项", "结论" })
            sb.AppendLine($"<th>{h}</th>");
        sb.AppendLine("</tr></thead><tbody>");

        int index = 0;
        foreach (var r in list)
        {
            index++;
            var cls = r.ResultText == "通过" ? "ok" : r.ResultText == "失败" ? "bad" : "warn";
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{index}</td>");
            sb.AppendLine($"<td>{Esc(r.Ip)}{(r.Port != 0 ? ":" + r.Port : string.Empty)}</td>");
            sb.AppendLine($"<td class=\"mono\">{Esc(r.SerialNo)}</td>");
            sb.AppendLine($"<td>{Esc(Fallback(r.Model, r.DeviceName))}</td>");
            sb.AppendLine($"<td>{Esc(r.FirmwareVersion)}{(string.IsNullOrWhiteSpace(r.FirmwareDate) ? string.Empty : " / " + Esc(r.FirmwareDate))}</td>");
            sb.AppendLine($"<td class=\"num\">{r.ChannelCount}</td>");
            sb.AppendLine($"<td>{Esc(r.DeviceTime)}</td>");
            sb.AppendLine($"<td>{Esc(r.CheckSummary)}</td>");
            sb.AppendLine($"<td><span class=\"pill {cls}\">{Esc(r.ResultText)}</span></td>");
            sb.AppendLine("</tr>");
        }
        if (list.Count == 0)
            sb.AppendLine("<tr><td colspan=\"9\" class=\"empty\">本次验收没有产生记录</td></tr>");
        sb.AppendLine("</tbody></table>");

        // ---------- 逐台明细 ----------
        sb.AppendLine("<h2>二、逐台验收明细</h2>");
        if (list.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">无明细</p>");
        }
        else
        {
            int seq = 0;
            foreach (var r in list)
            {
                seq++;
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine($"<div class=\"card-head\"><span class=\"idx\">{seq}</span><span class=\"title\">{Esc(Fallback(r.Model, r.DeviceName))}</span>");
                sb.AppendLine($"<span class=\"pill {(r.ResultText == "通过" ? "ok" : r.ResultText == "失败" ? "bad" : "warn")}\">{Esc(r.ResultText)}</span></div>");
                sb.AppendLine("<div class=\"kv\">");
                AppendKv(sb, "IP 地址", $"{r.Ip}{(r.Port != 0 ? ":" + r.Port : string.Empty)}");
                AppendKv(sb, "序列号", r.SerialNo);
                AppendKv(sb, "固件版本", string.Join(" ", new[] { r.FirmwareVersion, r.FirmwareDate }.Where(s => !string.IsNullOrWhiteSpace(s))));
                AppendKv(sb, "通道数", r.ChannelCount > 0 ? $"{r.ChannelCount}（起始通道 {r.StartChannel}）" : "—");
                AppendKv(sb, "设备时间", r.DeviceTime);
                AppendKv(sb, "耗时", $"{r.ElapsedMs} ms");
                sb.AppendLine("</div>");

                if (!string.IsNullOrEmpty(r.Error))
                    sb.AppendLine($"<div class=\"err\">错误信息：{Esc(r.Error)}</div>");

                if (r.Checks.Count > 0)
                {
                    sb.AppendLine("<table class=\"checks\"><thead><tr><th style=\"width:180px\">检查项</th><th style=\"width:80px\">结果</th><th>说明</th></tr></thead><tbody>");
                    foreach (var c in r.Checks)
                    {
                        var cls = c.Skipped ? "skip" : c.Passed ? "ok" : "bad";
                        sb.AppendLine($"<tr><td>{Esc(c.Name)}</td><td><span class=\"pill {cls}\">{Esc(c.StatusText)}</span></td><td>{Esc(c.Detail)}</td></tr>");
                    }
                    sb.AppendLine("</tbody></table>");
                }

                if (r.Snapshots.Count > 0)
                {
                    sb.AppendLine("<div class=\"shots\">");
                    foreach (var s in r.Snapshots)
                    {
                        var data = ToDataUri(s);
                        if (data != null)
                            sb.AppendLine($"<figure><img src=\"{data}\" alt=\"抓图\"><figcaption>{Esc(Path.GetFileName(s))}</figcaption></figure>");
                        else
                            sb.AppendLine($"<figure class=\"nofile\"><figcaption>截图未嵌入：{Esc(s)}</figcaption></figure>");
                    }
                    sb.AppendLine("</div>");
                }

                sb.AppendLine("</div>");
            }
        }

        sb.AppendLine("<div class=\"footer\">本报告由「海康设备交付运维工具」自动生成 · ");
        sb.AppendLine($"共 {total} 台设备 · 视频通道合计 {channels} 路 · 生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine("</body></html>");

        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        LogService.Current.Success($"验收报告已生成：{file}", "导出");
        return file;
    }

    private static void AppendKv(StringBuilder sb, string key, string? value)
        => sb.AppendLine($"<div><label>{Esc(key)}</label><span>{(string.IsNullOrWhiteSpace(value) ? "—" : Esc(value))}</span></div>");

    private static string Fallback(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a! : (!string.IsNullOrWhiteSpace(b) ? b! : "未知设备");

    /// <summary>把本地图片读成 Base64 DataURI，保证报告单文件可独立发送。</summary>
    private static string? ToDataUri(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            // 单张超过 5MB 就不嵌入了，否则报告会大到发不出去
            if (bytes.Length > 5 * 1024 * 1024) return null;
            return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString().Trim();
    }

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    private static string ReportCss() => """
        <style>
        :root{
          --ink:#1f2733; --ink2:#5b6675; --line:#e3e8ef; --bg:#f4f6fa; --card:#fff;
          --ok:#15803d; --okbg:#e8f7ee; --bad:#c0231d; --badbg:#fdecea;
          --warn:#b45309; --warnbg:#fef4e6; --accent:#1d4ed8;
        }
        *{box-sizing:border-box}
        body{margin:0;padding:32px 28px 60px;background:var(--bg);color:var(--ink);
             font:14px/1.7 "Microsoft YaHei","PingFang SC","Helvetica Neue",Arial,sans-serif}
        h1{font-size:26px;margin:8px 0 20px;letter-spacing:.5px}
        h2{font-size:18px;margin:36px 0 14px;padding-left:11px;border-left:4px solid var(--accent)}
        .cover{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:28px 30px;
               box-shadow:0 1px 3px rgba(16,24,40,.06)}
        .logo{font-size:12px;letter-spacing:3px;color:var(--accent);font-weight:700;margin-bottom:6px}
        table{border-collapse:collapse;width:100%}
        .meta td,.meta th{border:1px solid var(--line);padding:9px 12px;text-align:left;font-size:13px}
        .meta th{background:#f7f9fc;width:190px;color:var(--ink2);font-weight:600}
        .verdict{margin-top:18px;padding:12px 16px;border-radius:8px;font-weight:700;font-size:15px}
        .verdict.ok{background:var(--okbg);color:var(--ok);border:1px solid #bfe6cd}
        .verdict.bad{background:var(--badbg);color:var(--bad);border:1px solid #f5c6c2}
        .verdict.warn{background:var(--warnbg);color:var(--warn);border:1px solid #f5ddb4}
        .grid{background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden}
        .grid thead th{background:#f1f4f9;color:var(--ink2);font-size:12.5px;font-weight:600;
                       padding:10px 10px;border-bottom:1px solid var(--line);text-align:left;white-space:nowrap}
        .grid td{padding:9px 10px;border-bottom:1px solid #eef1f6;font-size:13px;vertical-align:top}
        .grid tbody tr:nth-child(even){background:#fafbfd}
        .num,.mono{font-family:Consolas,"Courier New",monospace}
        .num{text-align:center}
        .pill{display:inline-block;padding:2px 9px;border-radius:20px;font-size:12px;font-weight:600;white-space:nowrap}
        .pill.ok{background:var(--okbg);color:var(--ok)}
        .pill.bad{background:var(--badbg);color:var(--bad)}
        .pill.warn{background:var(--warnbg);color:var(--warn)}
        .pill.skip{background:#eef1f5;color:var(--ink2)}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin-bottom:14px}
        .card-head{display:flex;align-items:center;gap:10px;padding-bottom:12px;border-bottom:1px dashed var(--line);margin-bottom:12px}
        .card-head .idx{width:24px;height:24px;border-radius:50%;background:var(--accent);color:#fff;
                        font-size:12px;display:flex;align-items:center;justify-content:center;font-weight:700}
        .card-head .title{font-weight:700;font-size:15px;flex:1}
        .kv{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:6px 22px}
        .kv div{display:flex;gap:8px;font-size:13px;border-bottom:1px dotted #eef1f6;padding:3px 0}
        .kv label{color:var(--ink2);min-width:80px}
        .kv span{color:var(--ink);word-break:break-all}
        .err{margin-top:10px;background:var(--badbg);color:var(--bad);border-radius:6px;padding:8px 12px;font-size:13px}
        .checks{margin-top:12px;border:1px solid var(--line);border-radius:8px;overflow:hidden}
        .checks thead th{background:#f7f9fc;font-size:12px;color:var(--ink2);padding:8px 10px;
                         border-bottom:1px solid var(--line);text-align:left}
        .checks td{padding:7px 10px;border-bottom:1px solid #eef1f6;font-size:12.5px}
        .checks tbody tr:last-child td{border-bottom:none}
        .shots{display:flex;flex-wrap:wrap;gap:12px;margin-top:14px}
        .shots figure{margin:0;width:230px;border:1px solid var(--line);border-radius:8px;overflow:hidden;background:#fbfcfe}
        .shots img{width:100%;display:block}
        .shots figcaption{font-size:11px;color:var(--ink2);padding:5px 8px;text-align:center;
                          white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .nofile figcaption{white-space:normal;padding:14px 8px}
        .empty{text-align:center;color:var(--ink2);padding:22px}
        .footer{margin-top:40px;text-align:center;color:#8a94a3;font-size:12px;line-height:2}
        @media print{body{background:#fff;padding:0}.cover,.grid,.card{box-shadow:none;break-inside:avoid}
                     h2{break-after:avoid}.card{break-inside:avoid}}
        </style>
        """;
}

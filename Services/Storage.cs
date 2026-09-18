using System.IO;
using System.Text;
using System.Text.Json;
using HikDeployTool.Models;

namespace HikDeployTool.Services;

/// <summary>设置与台账的 JSON 持久化（System.Text.Json，无第三方依赖）。</summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"读取配置文件失败：{Path.GetFileName(path)} - {ex.Message}", "配置");
            return null;
        }
    }

    public static bool Save<T>(string path, T value)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 先写临时文件再替换，避免掉电/崩溃把好文件写坏
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Current.Error($"保存配置文件失败：{Path.GetFileName(path)}", ex, "配置");
            return false;
        }
    }
}

/// <summary>极简 CSV 读写（Excel 可直接打开，带 UTF-8 BOM 保证中文不乱码）。</summary>
public static class CsvUtil
{
    public static void Write(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(Escape)));
        // Excel 需要 BOM 才能正确识别 UTF-8 中文
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public static List<Dictionary<string, string>> Read(string path)
    {
        var result = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return result;

        var lines = Parse(File.ReadAllText(path, Encoding.UTF8));
        if (lines.Count < 1) return result;
        var headers = lines[0];
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Count == 1 && string.IsNullOrWhiteSpace(line[0])) continue;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < headers.Count; c++)
                dict[headers[c].Trim()] = c < line.Count ? line[c] : string.Empty;
            result.Add(dict);
        }
        return result;
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    /// <summary>解析 CSV 文本为行列（支持引号包裹与内嵌逗号/换行）。</summary>
    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        field.Clear();
                        rows.Add(row);
                        row = [];
                        break;
                    default:
                        field.Append(ch);
                        break;
                }
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}

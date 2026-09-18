namespace HikDeployTool.Services;

/// <summary>口令强度评估结果。</summary>
public sealed class PasswordStrength
{
    public int Score { get; init; }            // 1~4
    public string Label { get; init; } = "";   // 弱 / 中 / 强 / 很强
    public string Hint { get; init; } = "";    // 给用户的改进建议
    public bool Acceptable { get; init; }      // 是否满足海康新固件的最低要求
    public double Percent { get; init; }       // 进度条百分比
}

/// <summary>
/// 口令策略。
///
/// 海康 2019 年之后的新固件在激活 / 改密码时会强制要求"强口令"：
/// 长度 ≥ 8，且包含大写、小写、数字、特殊字符中的至少 2~3 类。
/// 不同固件版本要求略有差异，本类只做"提前提醒"，
/// 真正的判定仍由设备返回的错误码决定——不替设备做决定。
/// </summary>
public static class PasswordPolicy
{
    private const string SpecialChars = "!@#$%^&*()-_=+[]{};:,.?/|~";

    public static string Generate(int length = 12)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // 去掉易混淆的 I O
        const string lower = "abcdefghijkmnopqrstuvwxyz";  // 去掉 l
        const string digit = "23456789";                   // 去掉 0 1
        const string upperSpecial = "!@#$%^&*-_=+?";

        var rnd = Random.Shared;
        var chars = new List<char>
        {
            upper[rnd.Next(upper.Length)],
            lower[rnd.Next(lower.Length)],
            digit[rnd.Next(digit.Length)],
            upperSpecial[rnd.Next(upperSpecial.Length)],
        };

        string all = upper + lower + digit + upperSpecial;
        while (chars.Count < Math.Max(8, length)) chars.Add(all[rnd.Next(all.Length)]);

        // 洗牌，避免前四位固定成"大写+小写+数字+符号"
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string([.. chars]);
    }

    public static PasswordStrength Evaluate(string? password, int minLength = 8)
    {
        if (string.IsNullOrEmpty(password))
            return new PasswordStrength { Score = 0, Label = "未填写", Percent = 0, Hint = "请输入密码", Acceptable = false };

        int len = password.Length;
        bool hasUpper = password.Any(char.IsUpper);
        bool hasLower = password.Any(char.IsLower);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSpecial = password.Any(ch => SpecialChars.Contains(ch));
        bool hasOther = password.Any(ch => !char.IsLetterOrDigit(ch) && !SpecialChars.Contains(ch));

        int classes = (hasUpper ? 1 : 0) + (hasLower ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial || hasOther ? 1 : 0);

        int score;
        if (len < minLength || classes <= 1) score = 1;
        else if (len < 10 || classes == 2) score = 2;
        else if (len < 12 || classes == 3) score = 3;
        else score = 4;

        // 只由数字或只由字母组成，直接降级
        if (password.All(char.IsDigit) || password.All(char.IsLetter)) score = 1;

        bool acceptable = len >= minLength && classes >= 3;

        var label = score switch { 1 => "弱", 2 => "中", 3 => "强", _ => "很强" };

        var hints = new List<string>();
        if (len < minLength) hints.Add($"长度至少 {minLength} 位");
        if (!hasUpper) hints.Add("缺少大写字母");
        if (!hasLower) hints.Add("缺少小写字母");
        if (!hasDigit) hints.Add("缺少数字");
        if (!hasSpecial && !hasOther) hints.Add("缺少特殊字符");
        var hint = hints.Count == 0 ? "符合设备强口令要求" : string.Join("、", hints);

        return new PasswordStrength
        {
            Score = score,
            Label = label,
            Hint = hint,
            Acceptable = acceptable,
            Percent = score * 25,
        };
    }
}

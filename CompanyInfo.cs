namespace HikDeployTool;

/// <summary>
/// 公司信息与版权声明的唯一来源。
///
/// 刻意集中成一个静态类而不是各处写字面量：公司名会同时出现在窗口标题、
/// 标题栏署名、状态栏版权、关于页抬头、以及导出报告封面这五个地方，
/// 分散写迟早会出现某一处改了另一处没改的情况——交付现场被甲方看到
/// 两个不一样的公司名，比排版难看严重得多。
/// </summary>
public static class CompanyInfo
{
    /// <summary>公司全称（工商登记名称，不要简写）。</summary>
    public const string Name = "青岛明远达电子设备有限公司";

    /// <summary>对外简称，只在空间实在不够时用。</summary>
    public const string ShortName = "明远达电子";

    /// <summary>一句话业务定位，用在关于页抬头的副标题。</summary>
    public const string Tagline = "安防工程 · 系统集成 · 网络工程 · 楼宇智能化";

    /// <summary>版权年份。写死而不是取 DateTime.Now.Year：软件是按版本发布的，
    /// 版权年份应当随发布更新，不该在跨年那天自动跳变。</summary>
    public const int CopyrightYear = 2026;

    /// <summary>完整版权行。</summary>
    public static string Copyright => $"© {CopyrightYear} {Name} 版权所有";

    /// <summary>窗口标题用的完整标题。</summary>
    public static string WindowTitle => $"{AppInfo.ProductName} · {Name}";

    // ==================== 关于页三段业务介绍 ====================
    // 正文保留公司对外介绍的原句，不做改写：这类文字是给甲方看的，
    // 措辞改动可能影响资质表述的准确性。

    public const string CoreBusiness =
        "主要从事电子产品、计算机及配件、办公自动化设备、网络设备等的批发与零售。";

    public const string CoreBusinessTags = "电子产品|计算机及配件|办公自动化设备|网络设备";

    public const string EngineeringService =
        "具备安防工程、系统集成、网络工程、楼宇智能化工程等资质，可承接室内外装饰装潢、亮化及照明工程。";

    public const string EngineeringServiceTags =
        "安防工程|系统集成|网络工程|楼宇智能化|装饰装潢|亮化照明";

    public const string Partners =
        "作为海康威视、萤石、华为机器视觉等品牌的经销商，业务覆盖山东省内数千家政府、企事业单位及工厂学校。";

    public const string PartnerTags = "海康威视|萤石|华为机器视觉";

    public const string PartnerCoverage = "业务覆盖：山东省内数千家政府、企事业单位及工厂学校";

    /// <summary>关于页底部的用途说明。</summary>
    public const string ToolUsageNote =
        "本工具用于海康威视设备的现场交付与运维，随工程项目提供给施工与售后人员使用。";
}

/// <summary>软件自身的名称与版本信息。</summary>
public static class AppInfo
{
    public const string ProductName = "海康设备交付运维工具";

    /// <summary>对外版本号，与 csproj 的 &lt;Version&gt; 保持一致。</summary>
    public const string Version = "1.0.0";

    public static string VersionText => $"v{Version}";
}

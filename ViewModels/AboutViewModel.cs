using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HikDeployTool.Services;

namespace HikDeployTool.ViewModels;

/// <summary>关于页里的一张业务介绍卡。</summary>
public sealed class AboutSection
{
    public string Title { get; init; } = string.Empty;
    public string Glyph { get; init; } = string.Empty;

    /// <summary>正文，保留公司对外介绍的原句。</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>从正文里抽出来的关键词，做成一排小标签，让版面不至于变成三坨文字。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>补充说明（目前只有"合作伙伴"用到）。</summary>
    public string Footnote { get; init; } = string.Empty;

    public bool HasFootnote => !string.IsNullOrWhiteSpace(Footnote);
}

/// <summary>关于页里"标签 → 值"的一行。</summary>
public sealed class AboutFact
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;

    /// <summary>值下面的小字说明，用来解释这一项为什么值得看。</summary>
    public string Hint { get; init; } = string.Empty;

    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    /// <summary>路径 / 版本号这类用等宽字体更好认，也方便一眼比对字符。</summary>
    public bool IsMono { get; init; }
}

/// <summary>
/// 关于页：公司简介 + 业务范围 + 软件版本与环境信息。
///
/// 为什么要单独做一页而不是塞进设置页：
///   1. 交付现场交接时，甲方或监理会问"你们是哪家单位、有没有资质、这软件什么版本"，
///      这一页要能直接指着屏幕回答，而不是让工程师去翻合同；
///   2. 排障时技术支持第一句就是问版本号和运行环境，
///      这里把这几项集中起来并支持一键复制，省掉来回问。
///
/// 公司名、版权、三段业务介绍全部取自 <see cref="CompanyInfo"/>，
/// 与标题栏署名、窗口标题、exe 文件属性共用一个来源，不在这里写第二份字面量。
/// </summary>
public sealed class AboutViewModel : PageViewModel
{
    private const string Source = "关于";

    private RelayCommand? _copyInfoCommand;
    private RelayCommand? _openBaseDirCommand;

    public AboutViewModel()
    {
        Sections =
        [
            new AboutSection
            {
                Title = "核心业务",
                Glyph = "IconList",
                Body = CompanyInfo.CoreBusiness,
                Tags = SplitTags(CompanyInfo.CoreBusinessTags),
            },
            new AboutSection
            {
                Title = "工程服务",
                Glyph = "IconShield",
                Body = CompanyInfo.EngineeringService,
                Tags = SplitTags(CompanyInfo.EngineeringServiceTags),
            },
            new AboutSection
            {
                Title = "合作伙伴",
                Glyph = "IconBrand",
                Body = CompanyInfo.Partners,
                Tags = SplitTags(CompanyInfo.PartnerTags),
                Footnote = CompanyInfo.PartnerCoverage,
            },
        ];

        ReloadFacts();
    }

    public override string Title => "关于";
    // 品牌标识用新版"镜头 + 信号"，与标题栏、任务栏程序图标同源（IconBrandMark）；
    // 旧版 IconBrand 仍留在「合作伙伴」卡上，指代"经销品牌"正合适。
    public override string Glyph => "IconBrandMark";
    public override string Description => "公司简介、业务范围与软件版本信息";
    public override string Usage => "交接或排障时看这一页：公司资质、三个业务方向，以及软件版本、SADP / HCNetSDK 状态与程序目录，可一键复制发技术支持。";

    /// <summary>
    /// 每次切到本页都重算环境信息。
    /// 构造时机太早：那会儿 SADP 还没 Start、HCNetSDK 还是"未初始化"，
    /// 用户进来看一眼会误以为依赖缺失 —— 这类"显示成故障"的误报比不显示更麻烦。
    /// </summary>
    public override void OnEnter() => ReloadFacts();

    // ==================== 公司 ====================

    public string CompanyName => CompanyInfo.Name;

    public string CompanyTagline => CompanyInfo.Tagline;

    public string CompanyShortName => CompanyInfo.ShortName;

    public string Copyright => CompanyInfo.Copyright;

    public string ToolUsageNote => CompanyInfo.ToolUsageNote;

    public string ProductName => AppInfo.ProductName;

    public string VersionText => AppInfo.VersionText;

    /// <summary>三段业务介绍的卡片。</summary>
    public IReadOnlyList<AboutSection> Sections { get; }

    // 界面按固定三列摆放，这里给三个具名入口。
    // 不直接在 XAML 里写 {Binding Sections[0]}：索引器绑定要靠运行时类型反射找 Item 属性，
    // 一旦实现类型变了就会静默绑不上（表现为卡片空白，只在绑定追踪里留一行警告）。
    public AboutSection CoreBusinessSection => Sections[0];

    public AboutSection EngineeringSection => Sections[1];

    public AboutSection PartnerSection => Sections[2];

    // ==================== 软件与环境 ====================

    public ObservableCollection<AboutFact> Facts { get; } = [];

    private void ReloadFacts()
    {
        Facts.Clear();

        Facts.Add(new AboutFact
        {
            Label = "软件名称",
            Value = AppInfo.ProductName,
        });

        Facts.Add(new AboutFact
        {
            Label = "版本",
            Value = AppInfo.VersionText,
            Hint = "与交付清单上的版本号一致",
            IsMono = true,
        });

        Facts.Add(new AboutFact
        {
            Label = "构建时间",
            Value = BuildTimeText(),
            Hint = "主程序文件的最后写入时间",
            IsMono = true,
        });

        Facts.Add(new AboutFact
        {
            Label = "交付单位",
            Value = CompanyInfo.Name,
        });

        Facts.Add(new AboutFact
        {
            Label = "运行环境",
            Value = RuntimeInformation.FrameworkDescription,
            Hint = $"{RuntimeInformation.ProcessArchitecture} 进程",
            IsMono = true,
        });

        Facts.Add(new AboutFact
        {
            Label = "SADP 组件",
            Value = SadpText(),
            Hint = "设备发现与参数配置依赖，缺失时搜索页会报 2015 / 2030",
            IsMono = true,
        });

        Facts.Add(new AboutFact
        {
            Label = "HCNetSDK",
            Value = NetSdkText(),
            Hint = "按需初始化：执行验收、抓图或改配置时会自动加载",
            IsMono = true,
        });

        Facts.Add(new AboutFact
        {
            Label = "程序目录",
            Value = AppPaths.BaseDir,
            Hint = "所有 SDK 运行库必须与主程序同目录",
            IsMono = true,
        });

        Facts.Add(new AboutFact
        {
            Label = "日志目录",
            Value = AppPaths.LogDir,
            Hint = "排障时把这个目录整体打包发给技术支持",
            IsMono = true,
        });
    }

    private static string SadpText()
    {
        var sadp = App.Sadp;
        string version = sadp.VersionText;
        if (string.IsNullOrWhiteSpace(version) || version == "未知") return "未就绪";

        return sadp.IsRunning ? $"{version}（运行中）" : $"{version}（未启动）";
    }

    private static string NetSdkText()
        => App.NetSdk.IsInitialized ? "已初始化" : "未初始化（按需加载）";

    private static string BuildTimeText()
    {
        try
        {
            // 取 exe 自身的写入时间而不是 Assembly.Location：
            // 单文件发布时 Location 会指向临时解包目录，那个时间没有意义。
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception ex)
        {
            Log.Warn($"读取构建时间失败：{ex.Message}", Source);
        }

        return "未知";
    }

    // ==================== 复制信息 ====================

    public RelayCommand CopyInfoCommand => _copyInfoCommand ??= new RelayCommand(CopyInfo);

    private void CopyInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppInfo.ProductName} {AppInfo.VersionText}");
        sb.AppendLine(CompanyInfo.Name);
        sb.AppendLine(new string('-', 40));

        foreach (var fact in Facts)
        {
            sb.AppendLine($"{fact.Label}：{fact.Value}");
        }

        sb.AppendLine(new string('-', 40));
        sb.AppendLine(CompanyInfo.Copyright);
        sb.AppendLine(CompanyInfo.ToolUsageNote);

        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            Toast("软件与环境信息已复制，可直接粘贴给技术支持");
            Log.Info("复制关于信息", Source);
        }
        catch (Exception ex)
        {
            // 剪贴板被其它进程占用时会抛 COMException，这里不能让它冒到界面上
            Log.Warn($"复制失败：{ex.Message}", Source);
            Toast("复制失败，剪贴板可能被其它程序占用", true);
        }
    }

    public RelayCommand OpenBaseDirCommand => _openBaseDirCommand ??= new RelayCommand(OpenBaseDir);

    private void OpenBaseDir()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.BaseDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开程序目录失败：{ex.Message}", Source);
            Toast("打开目录失败", true);
        }
    }

    private static LogService Log => App.Log;

    /// <summary>把 "A|B|C" 拆成标签数组，顺便丢掉空项。</summary>
    private static string[] SplitTags(string packed)
        => packed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

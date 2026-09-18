using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HikDeployTool.Services;

/// <summary>
/// 自检（无界面 / 实机界面两种）。
///
/// 现场交付的时候总会碰到"这台笔记本不许开图形界面"或者"远程桌面卡到没法操作"的情况，
/// 这时候至少得能把依赖库、权限、界面资源这三件事问清楚，而不是靠猜。
/// 另外，这些自检也是本工程自己的回归手段：XAML 里 StaticResource 写错一个键，
/// 编译期是警告、运行期才是崩溃，只有真把每个视图加载一次才会暴露。
///
/// 用法（可组合，也可单独用）：
///   HikDeployTool.exe --selfcheck              环境自检，结果写入 selfcheck.txt
///   HikDeployTool.exe --selfcheck D:\out.txt   环境自检，结果写入指定文件
///   HikDeployTool.exe --uicheck                界面自检（只解析 XAML，不开窗口）
///   HikDeployTool.exe --livecheck              实机自检（真开窗口、逐页切换、抓绑定错误）
///   HikDeployTool.exe --selfcheck --uicheck --livecheck
///   HikDeployTool.exe --livecheck --shots      实机自检的同时，把每一页各存一张 PNG
///   HikDeployTool.exe --livecheck --shots=D:\ui  指定截图目录
///
/// 退出码：0 = 全部通过；2 = 关键依赖缺失；4 = 界面加载失败；8 = 实机切换失败；可相加。
///
/// ---- 为什么还要有 --livecheck ----
/// --uicheck 只是把视图 LoadContent() 出来，元素没进可视树、绑定根本没生效。
/// 而"只读属性被 TwoWay 绑定"这类错误，恰恰要到窗口真正布局（AttachToContext）时才抛，
/// 于是出现过"自检全绿、用户一打开就弹异常"的尴尬。--livecheck 补的就是这一段：
/// 真的把主窗口 Show 出来（挪到屏幕外），再逐页切换并强制 UpdateLayout。
///
/// ---- 为什么还要有 --shots ----
/// livecheck 能告诉你"没报错"，但告诉不了你"好不好看"。三列卡是否等高、
/// 长路径有没有把卡片撑破、标签换行后留白是否难看 —— 这些只能看图。
/// 截图走的是同一套真实布局，所以看到的就是用户看到的。
/// </summary>
public static class HeadlessCheck
{
    public const string EnvSwitch = "--selfcheck";
    public const string UiSwitch = "--uicheck";
    public const string LiveSwitch = "--livecheck";
    public const string ShotsSwitch = "--shots";

    /// <summary>
    /// 是否正处在自检流程中。
    /// 全局异常兜底靠它决定"记录后直接吞掉"还是"弹窗提醒用户" ——
    /// 自检期间弹窗会把自动化跑飞，还可能把检查项当成真故障。
    /// </summary>
    public static bool IsRunning { get; private set; }

    /// <summary>命令行里是否要求了任一自检模式。</summary>
    public static bool IsRequested(string[] args) =>
        args.Any(a => IsSwitch(a, EnvSwitch) || IsSwitch(a, UiSwitch) || IsSwitch(a, LiveSwitch));

    /// <summary>执行要求的自检并返回进程退出码。</summary>
    public static int Run(string[] args)
    {
        bool wantEnv = args.Any(a => IsSwitch(a, EnvSwitch));
        bool wantUi = args.Any(a => IsSwitch(a, UiSwitch));
        bool wantLive = args.Any(a => IsSwitch(a, LiveSwitch));

        IsRunning = true;

        // 实机自检会开一个窗口，窗口关掉时不能顺手把整个应用带走 ——
        // 结果文件还没写完呢。退出时机由这里显式控制。
        if (Application.Current != null)
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"海康设备交付运维工具 自检报告  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序目录：{AppPaths.BaseDir}");
        sb.AppendLine($"进程权限：{(EnvironmentCheck.IsAdministrator() ? "管理员" : "普通用户")}");
        sb.AppendLine(new string('-', 66));

        int exitCode = 0;

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            // 目录都建不出来说明部署位置有问题（比如放在了只读目录），这本身就是结论
            sb.AppendLine($"[失败] 无法创建输出目录：{ex.Message}");
            exitCode |= 2;
        }

        if (wantEnv)
        {
            sb.AppendLine();
            sb.AppendLine("【环境自检】");
            try
            {
                var items = EnvironmentCheck.Run();

                // ToText() 自带标题和分隔线，这里已经有总标题了，去掉前两行免得重复
                foreach (string line in EnvironmentCheck.ToText(items).Replace("\r\n", "\n").Split('\n').Skip(2))
                {
                    if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line.TrimEnd());
                }

                if (items.Any(i => i.Critical && !i.Ok)) exitCode |= 2;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[失败] 环境自检执行异常：{ex.GetBaseException().Message}");
                exitCode |= 2;
            }
        }

        if (wantUi)
        {
            sb.AppendLine();
            sb.AppendLine("【界面自检】");

            // 先把配置和台账读进来，让页面拿到的是真实数据而不是空对象 ——
            // 有些绑定在空数据下不报错、有数据才炸，拿空壳跑一遍等于没跑。
            try
            {
                AppState.Current.Load();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[警告] 配置/台账加载失败（继续检查界面）：{ex.GetBaseException().Message}");
            }

            if (RunUiCheck(sb) > 0) exitCode |= 4;
        }

        if (wantLive)
        {
            sb.AppendLine();
            sb.AppendLine("【界面实机自检】");

            // 与上面一样，先把真实配置/台账读进来：有些绑定空数据不报错，有数据才炸。
            try
            {
                AppState.Current.Load();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[警告] 配置/台账加载失败（继续检查界面）：{ex.GetBaseException().Message}");
            }

            // --shots 只在 --livecheck 下有意义：截图要真布局之后的画面，
            // 单独给 --shots 不会自己把窗口开起来（那属于意外行为）。
            string? shotsDir = ResolveShotsDir(args);
            if (RunLiveCheck(sb, shotsDir) > 0) exitCode |= 8;

            if (shotsDir != null) sb.AppendLine($"[提示] 各页截图已写入：{shotsDir}");
        }

        sb.AppendLine();
        sb.AppendLine(new string('-', 66));
        sb.AppendLine(exitCode switch
        {
            0 => "结论：全部通过。",
            _ when (exitCode & 8) != 0 => "结论：实机界面自检发现问题，请按上面的明细处理（可能存在打不开的页面）。",
            _ when (exitCode & 4) != 0 && (exitCode & 2) != 0 => "结论：环境存在缺失项，且界面加载失败，请按上面的建议逐条处理。",
            _ when (exitCode & 4) != 0 => "结论：界面加载失败（不影响搜索设备，但对应页面可能打不开）。",
            _ => "结论：环境存在缺失项，请按上面的建议处理。",
        });

        string text = sb.ToString();
        Console.WriteLine(text);

        // WinExe 双击运行时没有控制台，文件才是主出口
        foreach (string target in ResolveOutputFiles(args, wantEnv, wantUi || wantLive))
        {
            try
            {
                File.WriteAllText(target, text, Encoding.UTF8);
                Console.WriteLine($"结果已写入：{target}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入 {target} 失败：{ex.Message}");
            }
        }

        IsRunning = false;
        return exitCode;
    }

    /// <summary>
    /// 把每个页面视图真正加载一遍，返回失败数量。
    ///
    /// 走的路径和运行时完全一致：先按 ViewModel 类型在 App.xaml 里查数据模板，
    /// 再 LoadContent() 实例化视图。主窗口的 XAML 也解析一次 ——
    /// App.xaml 里没写 StartupUri，主窗口完全由代码 new 出来，
    /// 少了这一步它的资源键就永远没人验证。
    /// </summary>
    private static int RunUiCheck(StringBuilder sb)
    {
        int failed = 0;
        // 自检不允许产生真实网络副作用（预览页 OnEnter 会自动登录入库设备）
        ViewModels.PreviewViewModel.HeadlessMode = true;

        MainWindow? window = null;
        try
        {
            window = new MainWindow();
            sb.AppendLine("[正常] 全局资源字典 + MainWindow.xaml：解析成功");
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine($"[失败] MainWindow.xaml 解析失败：{ex.GetBaseException().Message}");
        }

        if (window?.DataContext is not ViewModels.MainViewModel main)
        {
            sb.AppendLine("[跳过] 主窗口视图模型未建立，无法继续检查各页面");
            return failed + 1;
        }

        foreach (var page in main.Pages)
        {
            string name = page.GetType().Name;
            try
            {
                var key = new DataTemplateKey(page.GetType());
                if (Application.Current?.TryFindResource(key) is not DataTemplate template)
                {
                    failed++;
                    sb.AppendLine($"[失败] {name}：App.xaml 里没有对应的数据模板（页面会显示空白）");
                    continue;
                }

                template.LoadContent();
                sb.AppendLine($"[正常] {name}：视图加载成功");
            }
            catch (Exception ex)
            {
                failed++;
                sb.AppendLine($"[失败] {name}：视图加载失败 —— {ex.GetBaseException().Message}");
            }
        }

        return failed;
    }

    /// <summary>
    /// 实机界面自检：真的把主窗口显示出来，逐页切换并强制布局，返回失败数量。
    ///
    /// 为什么非得开窗口：WPF 的绑定是在元素进入可视树、布局跑起来那一刻才挂到数据源上的
    /// （BindingExpression.AttachToContext）。在窗口 Show 之前，绑定等于不存在 ——
    /// 所以"只读属性被 TwoWay 绑定"这种错，LoadContent() 查不出来，用户一点开就炸。
    ///
    /// 窗口挪到屏幕外（-32000），既不打扰用户，也能拿到真实的布局过程。
    /// </summary>
    private static int RunLiveCheck(StringBuilder sb, string? shotsDir = null)
    {
        int failed = 0;
        // 自检不允许产生真实网络副作用：关掉预览页的"入库设备自动登录"
        // 和云台能力探测（否则自检会真去连现场设备）。
        ViewModels.PreviewViewModel.HeadlessMode = true;
        MainWindow? window = null;

        // 自检期间收集异常，而不是让它冒到全局兜底去弹窗
        var exceptions = new List<string>();
        DispatcherUnhandledExceptionEventHandler onException = (_, e) =>
        {
            exceptions.Add($"{e.Exception.GetType().Name}：{e.Exception.Message}");
            e.Handled = true;
        };

        // 顺带把 WPF 的绑定诊断接过来：
        // 有些错误（属性名写错、转换器类型不匹配）不抛异常，只写跟踪日志，
        // 界面表现是"这一列永远空白"，不看日志根本发现不了。
        var bindingErrors = new BindingErrorListener();
        var bindingSource = PresentationTraceSources.DataBindingSource;
        try
        {
            bindingSource.Listeners.Add(bindingErrors);
            bindingSource.Switch.Level = SourceLevels.Warning;
        }
        catch
        {
            // 跟踪源不可用不影响主流程
        }

        if (Application.Current != null)
        {
            Application.Current.DispatcherUnhandledException += onException;
        }

        try
        {
            window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,          // 屏幕外：任务栏不出现，也不会抢焦点
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };

            window.Show();
            Pump(window);
            sb.AppendLine("[正常] 主窗口已实际显示并完成布局（窗口置于屏幕外）");
        }
        catch (Exception ex)
        {
            failed++;
            // 首次布局抛异常时窗口其实已经建出来了，别写成"无法显示"误导现场排查
            sb.AppendLine($"[失败] 主窗口首次布局抛出异常：{ex.GetBaseException().Message}");
        }

        if (window?.DataContext is ViewModels.MainViewModel main)
        {
            if (failed > 0)
            {
                // 首次布局就炸了，说明可视树已经处于异常状态，
                // 后面逐页的"正常"不能当准，得先把上面那条修掉。
                sb.AppendLine("[提示] 首次布局已失败，以下逐页结论仅供参考，请先修上面那条。");
            }

            foreach (var page in main.Pages)
            {
                string name = page.GetType().Name;
                exceptions.Clear();
                bindingErrors.Messages.Clear();

                bool probeOk = true;   // 只有预览页会置成 false
                bool enlargeChecked = false;
                bool enlargeOk = false;
                string enlargeDetail = string.Empty;
                bool shrinkChecked = false;
                bool shrinkOk = false;
                string shrinkDetail = string.Empty;

                try
                {
                    // 走真正的导航路径：切 CurrentPage 会触发 OnEnter，
                    // 页面里"进页就自动刷新"的逻辑也一并被验证到。
                    main.CurrentPage = page;

                    // 预览页额外塞 4 格"自检探针"：不取流，验证两件事 ——
                    //   ① DataTemplate → TileHost → WinForms 句柄回灌 → StartTileAsync
                    //      这条链真的通（它曾静默断掉：模板内 DataContext 是 PreviewTile 而非 VM，
                    //      表现为登录成功但画面全黑、日志零取流记录）；
                    //   ② 双击放大的布局：放大**第二格**必须正好铺满宫格
                    //      （曾"只有第一格正常，其余错位/消失"）。
                    if (page is ViewModels.PreviewViewModel probe) probe.AddProbeTiles(4);

                    // 布局要跑两轮：第一轮挂绑定、第二轮才是数据到位后的真实表现
                    Pump(window);
                    Pump(window);

                    if (shotsDir != null) SaveShot(window, name, shotsDir);

                    if (page is ViewModels.PreviewViewModel preview)
                    {
                        probeOk = preview.AttachProbeCount > 0;

                        // 固定放大第 2 格：历史 bug 只有第 1 格正常，试第一格永远试不出来
                        if (probeOk)
                        {
                            preview.EnlargeProbeTile(1);
                            Pump(window);
                            Pump(window);
                            (enlargeOk, enlargeDetail) = CheckEnlargeLayout(window, preview, 1);

                            // 放大态再存一张图：布局对不对，看一眼比读数字快
                            if (shotsDir != null) SaveShot(window, name + "_放大", shotsDir);
                            enlargeChecked = true;
                        }

                        // "从多切到少"布局：4 格切到 1 分屏，第 1 格必须独占铺满、其余 3 格隐藏。
                        // 曾缺失"超容量隐藏"：切分屏只改 UniformGrid 行列数，多出的格被排到
                        // 宫格外，WPF 边框 + WinForms 视频 HWND 压住下方操作条 —— 错位。
                        if (probeOk)
                        {
                            preview.SetProbeGridSize(1);
                            Pump(window);
                            Pump(window);
                            (shrinkOk, shrinkDetail) = CheckEnlargeLayout(window, preview, 0);
                            if (shotsDir != null) SaveShot(window, name + "_缩分屏", shotsDir);
                            shrinkChecked = true;
                            preview.SetProbeGridSize(4);
                            Pump(window);
                        }

                        preview.RemoveProbeTile();
                        Pump(window);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add($"{ex.GetBaseException().GetType().Name}：{ex.GetBaseException().Message}");
                }

                if (exceptions.Count == 0 && bindingErrors.Messages.Count == 0)
                {
                    sb.AppendLine($"[正常] {name}：切换 + 布局无异常");
                    if (!probeOk)
                    {
                        failed++;
                        sb.AppendLine("        [失败] 视频宿主句柄回灌链路未打通：格子建出来后没有回调取流，画面会一直黑屏");
                    }
                    else if (page is ViewModels.PreviewViewModel okPage)
                    {
                        sb.AppendLine($"        [正常] 视频宿主句柄回灌链路已打通（探针命中 {okPage.AttachProbeCount} 次）");
                    }

                    if (enlargeChecked)
                    {
                        if (enlargeOk) sb.AppendLine($"        [正常] 双击放大布局：{enlargeDetail}");
                        else
                        {
                            failed++;
                            sb.AppendLine($"        [失败] 双击放大布局：{enlargeDetail}");
                        }
                    }

                    if (shrinkChecked)
                    {
                        if (shrinkOk) sb.AppendLine($"        [正常] 缩分屏布局（4→1）：{shrinkDetail}");
                        else
                        {
                            failed++;
                            sb.AppendLine($"        [失败] 缩分屏布局（4→1）：{shrinkDetail}");
                        }
                    }
                    continue;
                }

                failed++;
                if (exceptions.Count > 0)
                {
                    sb.AppendLine($"[失败] {name}：切换到该页抛出 {exceptions.Count} 个异常");
                    foreach (string line in exceptions.Take(3)) sb.AppendLine($"        {line}");
                }

                if (bindingErrors.Messages.Count > 0)
                {
                    sb.AppendLine($"[失败] {name}：存在 {bindingErrors.Messages.Count} 条绑定错误");
                    foreach (string line in bindingErrors.Messages.Take(3)) sb.AppendLine($"        {Shorten(line)}");
                }
            }
        }
        else if (window != null)
        {
            failed++;
            sb.AppendLine("[失败] 主窗口视图模型未建立，无法逐页切换");
        }

        // ---- 收尾 ----
        try { window?.Close(); } catch { /* 关窗失败不影响结论 */ }

        if (Application.Current != null)
        {
            Application.Current.DispatcherUnhandledException -= onException;
        }

        try { bindingSource.Listeners.Remove(bindingErrors); } catch { /* 忽略 */ }

        return failed;
    }

    /// <summary>
    /// 强制把界面"跑一会儿"：布局 + 把 Background 优先级的活儿干完。
    /// 光调 UpdateLayout() 不够 —— 绑定挂载和数据模板展开都在数据绑定引擎的任务队列里，
    /// 得让 Dispatcher 真正转几圈才会执行。
    /// </summary>
    private static void Pump(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            try { window.UpdateLayout(); } catch { /* 布局异常由异常处理器收集 */ }

            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        try { window.UpdateLayout(); } catch { /* 同上 */ }
    }

    /// <summary>把 WPF 绑定跟踪日志收集起来。</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message.Trim()); }

        public override void WriteLine(string? message) => Write(message);
    }

    /// <summary>
    /// 自检：放大指定格之后，它的宿主必须正好铺满宫格区域，且其余格必须真的不可见。
    ///
    /// 这条断言是被逼出来的：双击放大先后踩过两次坑（点击被 WinForms 空域吃掉、
    /// UniformGrid 把没真正隐藏的格子排到可视区外），两次都是"代码看着没毛病、
    /// 上手一试不对"，而且第一格永远看起来是对的 —— 随手试一格试不出来。
    /// 所以自检固定放大第 2 格，并且把宿主的位置/尺寸量出来当证据。
    /// </summary>
    private static (bool ok, string detail) CheckEnlargeLayout(Window window, ViewModels.PreviewViewModel vm, int index)
    {
        var view = FindDescendant<Views.PreviewView>(window);
        var grid = view?.TileGrid;
        if (view == null || grid == null) return (false, "找不到预览宫格容器");

        if (index < 0 || index >= vm.Tiles.Count) return (false, "探针格数量不足，无法验证放大");

        var hosts = new List<Views.TileHost>();
        CollectVisuals(grid, hosts);
        var target = hosts.FirstOrDefault(h => ReferenceEquals(h.Tile, vm.Tiles[index]));
        if (target == null) return (false, "放大格没有生成视频宿主");
        if (!target.IsVisible) return (false, "放大格自己被隐藏了");

        double gw = grid.ActualWidth, gh = grid.ActualHeight;
        if (gw < 1 || gh < 1) return (false, "宫格尚未完成布局");

        var rect = target.TransformToAncestor(grid).TransformBounds(new Rect(target.RenderSize));

        if (rect.Left < -2 || rect.Top < -2 || rect.Right > gw + 2 || rect.Bottom > gh + 2)
            return (false, $"放大格跑到宫格区域外（宿主 {rect.X:F0},{rect.Y:F0} "
                + $"{rect.Width:F0}×{rect.Height:F0}；宫格 {gw:F0}×{gh:F0}）");

        if (rect.Width < gw * 0.9 || rect.Height < gh * 0.5)
            return (false, $"放大格没有铺开（{rect.Width:F0}×{rect.Height:F0}；宫格 {gw:F0}×{gh:F0}）");

        foreach (var h in hosts)
            if (!ReferenceEquals(h, target) && h.IsVisible)
                return (false, "放大时其它格仍然可见（WinForms 画面会盖在放大画面上）");

        return (true, $"第 {index + 1} 格放大后铺满 {rect.Width:F0}×{rect.Height:F0}"
            + $"（宫格 {gw:F0}×{gh:F0}），其余 {hosts.Count - 1} 格已隐藏");
    }

    private static void CollectVisuals<T>(DependencyObject root, List<T> sink) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) sink.Add(hit);
            CollectVisuals(child, sink);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) return hit;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// 把当前页面存成 PNG。
    ///
    /// 渲染的是窗口的内容根元素，而不是 Window 本身：
    /// Window 的视觉树里包含窗口边框/标题栏那部分非 WPF 内容，
    /// 直接渲染 Window 常常得到一张全黑的图，反而让人误判成"界面没画出来"。
    ///
    /// 截图失败一律不当成自检失败 —— 截图只是辅助手段，
    /// 不该因为它把一次本来通过的自检判定成失败。
    /// </summary>
    private static void SaveShot(Window window, string pageName, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);

            if (window.Content is not FrameworkElement root) return;

            Size size = root.RenderSize;
            if (size.Width < 1 || size.Height < 1) return;

            int w = (int)Math.Ceiling(size.Width);
            int h = (int)Math.Ceiling(size.Height);

            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using var fs = File.Create(Path.Combine(dir, $"{pageName}.png"));
            encoder.Save(fs);
        }
        catch
        {
            // 忽略：截图不是判定依据
        }
    }

    /// <summary>
    /// 截图目录：支持 --shots=D:\dir，也支持裸 --shots（落到程序目录下的 ui_shots）。
    /// 用 --shots= 这种自带参数的形式，是为了不跟"开关后面第一个非 '-' 参数就是结果文件"
    /// 那条既有解析规则打架（那个参数可能正是 selfcheck.txt 的路径）。
    /// </summary>
    private static string? ResolveShotsDir(string[] args)
    {
        foreach (string a in args)
        {
            string trimmed = a.Trim();
            foreach (string prefix in new[] { ShotsSwitch + "=", "/" + ShotsSwitch.TrimStart('-') + "=" })
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string dir = trimmed[prefix.Length..].Trim().Trim('"');
                    return string.IsNullOrWhiteSpace(dir) ? Path.Combine(AppPaths.BaseDir, "ui_shots") : dir;
                }
            }
        }

        return args.Any(a => IsSwitch(a, ShotsSwitch)) ? Path.Combine(AppPaths.BaseDir, "ui_shots") : null;
    }

    /// <summary>跟踪日志一行可能有几百字，报告里只要最有信息量的开头。</summary>
    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "…";

    /// <summary>
    /// 结果文件：命令行给了路径就用它，否则用程序目录下的默认文件名。
    /// </summary>
    private static IEnumerable<string> ResolveOutputFiles(string[] args, bool wantEnv, bool wantUi)
    {
        // 约定：紧跟在开关后面的、不以 - 或 / 开头的第一个参数视为输出文件
        string? explicitPath = null;
        bool afterSwitch = false;
        foreach (string a in args)
        {
            if (IsSwitch(a, EnvSwitch) || IsSwitch(a, UiSwitch) || IsSwitch(a, LiveSwitch)) { afterSwitch = true; continue; }
            if (!afterSwitch) continue;
            if (a.StartsWith('-') || a.StartsWith('/')) continue;
            explicitPath = a;
            break;
        }

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
            yield break;
        }

        if (wantEnv) yield return Path.Combine(AppPaths.BaseDir, "selfcheck.txt");
        if (wantUi) yield return Path.Combine(AppPaths.BaseDir, "uicheck.txt");
    }

    private static bool IsSwitch(string arg, string name) =>
        string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "/" + name.TrimStart('-'), StringComparison.OrdinalIgnoreCase);
}

using System.Windows;
using System.Windows.Threading;
using HikDeployTool.Services;

namespace HikDeployTool;

/// <summary>
/// 应用入口。
/// 这里只做四件事：无界面自检分支、创建主窗口、加载配置与台账、安装全局异常兜底。
/// </summary>
public partial class App : Application
{
    private bool _fatalShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局兜底必须在任何业务代码之前装上：
        // 原生 SDK 的回调线程里抛出的异常不会经过 MainWindow，
        // 没有这一层的话现场看到的就是"程序突然没了"，一点线索都留不下。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        // ---- 无界面自检模式：--selfcheck（环境） / --uicheck（界面） ----
        // 现场排查用，也供自动化验证"这套程序拷过去还能不能跑"。
        if (HeadlessCheck.IsRequested(e.Args))
        {
            Shutdown(HeadlessCheck.Run(e.Args));
            return;
        }

        // ---- 命令行登录测试：--logintest <IP> <用户名> <口令> [端口] ----
        // 现场"登不上设备"时一条命令复测，走和界面完全相同的登录诊断链。
        if (LoginCli.IsRequested(e.Args))
        {
            Shutdown(LoginCli.Run(e.Args));
            return;
        }

        // ---- 门禁 ISAPI 诊断：--acsprobe <IP> [用户名] [口令] [端口] ----
        // 逐个实测门禁端点（能力集/门状态候选 URL/远程控制/通行记录），
        // 结果落 acsprobe.txt——不同固件支持的 URL 不一样，靠猜不如靠问设备。
        if (AcsProbe.IsRequested(e.Args))
        {
            Shutdown(AcsProbe.Run(e.Args));
            return;
        }

        var state = AppState.Current;
        state.Load();
        state.ApplySdkLogSettings();

        // 主窗口必须显式创建 —— App.xaml 里没有 StartupUri。
        //
        // 这里曾经漏过：App.xaml 不写 StartupUri、代码里也不 new MainWindow()，
        // 编译、运行都不报错，进程也活着，但桌面上永远不出现窗口，
        // 表现就是"双击了图标却像什么都没发生"。排查花了很久，别再删掉这几行。
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        state.Log.Info("程序启动完成，等待设备搜索", "启动");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Current.Error("界面线程未处理异常", e.Exception);

        // 自检流程里异常是被当成"检查结果"用的，弹窗反而会把自动化卡死。
        if (HeadlessCheck.IsRunning)
        {
            e.Handled = true;
            return;
        }

        // 保持程序活着：现场正在批量操作中途崩掉，比弹个提示继续用要糟得多。
        // 只提示一次，避免连续异常刷屏。
        if (!_fatalShown)
        {
            _fatalShown = true;
            try
            {
                MessageBox.Show(
                    $"程序遇到一个问题，已记录到运行日志：\n\n{e.Exception.Message}\n\n" +
                    "可以继续操作；若反复出现，请到「运行日志」页导出日志后联系技术支持。",
                    "出现异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // 连弹窗都失败的话就别再折腾了
            }
        }

        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogService.Current.Error("非界面线程未处理异常", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogService.Current.Warn($"后台任务异常未被观察：{e.Exception?.GetBaseException().Message}");
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 正常退出时 MainWindow.Closing 已经收过尾了，这里是兜底（例如直接调用 Shutdown 的情况）
        try
        {
            AppState.Current.Shutdown();
        }
        catch
        {
            // 退出路径上的异常不再上报
        }

        base.OnExit(e);
    }
}

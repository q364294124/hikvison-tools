using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HikDeployTool.Services;
using HikDeployTool.ViewModels;

namespace HikDeployTool;

/// <summary>
/// 主窗口。职责很窄：装配 ViewModel、在窗口显示后跑一次启动自检、退出时收尾。
/// 业务逻辑一律放在 ViewModel / Service 里，这里不写任何 if 判断业务的分支。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        // 自检放在 Loaded 里，而不是构造函数里：
        // 构造函数里跑会卡住窗口显示，用户看到的是"点了图标半天没反应"。
        // 先让界面出来，再在后台把依赖库和权限查一遍。
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 让自检先排到 UI 队列后面，确保窗口已经完成首帧渲染
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _vm.RunStartupChecks();
            }
            catch (Exception ex)
            {
                LogService.Current.Error("启动自检失败", ex);
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            // 停掉 SADP 搜索线程、注销 SDK、落盘设置与台账
            AppState.Current.Shutdown();
        }
        catch (Exception ex)
        {
            LogService.Current.Warn($"退出清理时出现问题：{ex.Message}");
        }
        finally
        {
            _vm.Dispose();
        }
    }

    private void DismissToast_Click(object sender, RoutedEventArgs e) => _vm.DismissToast();
}

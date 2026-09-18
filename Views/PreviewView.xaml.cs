using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HikDeployTool.ViewModels;

namespace HikDeployTool.Views;

/// <summary>
/// 实时预览页的视图（多画面版）。
///
/// 代码后置只做三件事：
///   1. 树节点的选中 / 双击转发给 ViewModel（TreeViewItem 的事件无法直接绑定命令）；
///   2. 页面卸载时停掉全部预览 —— WPF 换页会销毁整棵视图树，
///      所有 WindowsFormsHost 连同 HWND 一起没了，继续推流等于往已销毁的窗口上画；
///   3. 双击要在 ItemContainerStyle 的 EventSetter 里接：TreeViewItem 会先把
///      双击标记为已处理，挂在模板上的 MouseDoubleClick 收不到。
/// </summary>
public partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();
        Unloaded += OnViewUnloaded;
    }

    private PreviewViewModel? ViewModel => DataContext as PreviewViewModel;

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } vm) vm.SelectedNode = e.NewValue as PreviewTreeNode;
    }

    /// <summary>双击树节点：设备节点 = 登录并展开通道；通道节点 = 开一格画面。</summary>
    private async void OnTreeItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem { DataContext: PreviewTreeNode node }) return;
        var vm = ViewModel;
        if (vm == null) return;

        e.Handled = true;

        if (node.IsDevice) await vm.ToggleDeviceAsync(node);
        else await vm.OpenChannelAsync(node);
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel?.StopAll();
    }

    // ==================== 虚拟云台 ====================
    // "按住转动、松开停止"的交互没法用命令表达（命令只有 Click），
    // 所以按钮 Tag 存 PTZ 命令码，三个事件统一转发给 ViewModel。

    /// <summary>按住：开始转动（Tag = 命令码，见 NetSdkService.PtzCommand）。</summary>
    private void OnPtzButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && uint.TryParse(tag, out var cmd))
            ViewModel?.PtzStart(cmd);
    }

    /// <summary>松开：停止。</summary>
    private void OnPtzButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && uint.TryParse(tag, out var cmd))
            ViewModel?.PtzStop(cmd);
    }

    /// <summary>鼠标滑出按钮也算松开（兜底，防止"按住了却停不下来"）。</summary>
    private void OnPtzButtonLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && uint.TryParse(tag, out var cmd))
            ViewModel?.PtzStop(cmd);
    }
}

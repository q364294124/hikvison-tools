using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HikDeployTool.Views;

/// <summary>
/// 一格预览画面的视频宿主。
///
/// 为什么需要这个控件：宫格是 ItemsControl + DataTemplate 生成的，
/// 每格都要有一个 WinForms Panel 来给 SDK 递 HWND。但句柄只能在
/// Loaded 之后才稳定可用，而"取流"这个动作在 ViewModel 里。
/// 于是把这段衔接单独抽成一个控件：
///   Loaded   → 建 Panel、把 Handle 交给 VM，VM 据此取流；
///   Unloaded → 通知 VM 收掉该格（分屏数变化会重建格子，旧 Panel 随即销毁，
///              不停流就会往死窗口上推流）；
///   MouseDown→ 选中本格（抓图针对选中格）、双击放大/还原。
///
/// 重要：**不要在构造函数/OnInitialized 里建 Panel**。
/// WinForms Panel 一建出来就创建 HWND，而那时控件还没进可视树；
/// 在窗口离屏显示（自检的 --livecheck）场景下，HWND 的创建会和
/// WPF 的 Dispatcher 循环互相等待，表现为整个进程卡死。
/// 一律等到 Loaded（此时可视树已就绪）再建。
///
/// 同样因为空域：**鼠标事件必须挂在 Panel 上，不能指望 WPF 的 MouseDown**。
/// Panel 是真 HWND、永远盖在 WPF 内容之上，点在画面上的每一次点击
/// 都被 WinForms 吃掉，WPF 的 MouseDown 只在格子边缘的缝隙里才收得到——
/// 曾经"双击放大"就是这么静默失效的（单击选中其实也没生效，只是没人注意）。
/// 所以点击事件在 WinForms 侧接收，再转发给 VM。
/// </summary>
public sealed class TileHost : ContentControl
{
    private System.Windows.Forms.Panel? _panel;
    private System.Windows.Forms.Integration.WindowsFormsHost? _host;
    private bool _eventsWired;

    public static readonly DependencyProperty TileProperty =
        DependencyProperty.Register(nameof(Tile), typeof(ViewModels.PreviewTile), typeof(TileHost),
            new PropertyMetadata(null, OnTileChanged));

    /// <summary>本格对应的预览数据对象。</summary>
    public ViewModels.PreviewTile? Tile
    {
        get => (ViewModels.PreviewTile?)GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    /// <summary>
    /// 所在页面的 ViewModel，**必须由 XAML 显式绑定**
    /// （Owner="{Binding DataContext, RelativeSource={RelativeSource AncestorType=ItemsControl}}"）。
    ///
    /// 为什么不能靠 DataContext：本控件在 ItemsControl 的 DataTemplate 里，
    /// DataContext 继承的是当前项（PreviewTile），不是 PreviewViewModel。
    /// 曾经这里直接判断 DataContext is PreviewViewModel —— 永远不成立，
    /// 于是句柄从未回灌、取流一次都没被调用，界面一直黑屏等待，
    /// 运行日志里连一条取流记录都没有（连带"选中格子""离开页面停流"一起失效）。
    /// </summary>
    public static readonly DependencyProperty OwnerProperty =
        DependencyProperty.Register(nameof(Owner), typeof(ViewModels.PreviewViewModel), typeof(TileHost),
            new PropertyMetadata(null, OnOwnerChanged));

    public ViewModels.PreviewViewModel? Owner
    {
        get => (ViewModels.PreviewViewModel?)GetValue(OwnerProperty);
        set => SetValue(OwnerProperty, value);
    }

    /// <summary>取归属 VM：优先显式绑定，退化时沿可视树向上找（绑定漏了也不至于静默黑屏）。</summary>
    private ViewModels.PreviewViewModel? Vm => Owner ?? FindViewModel();

    private ViewModels.PreviewViewModel? FindViewModel()
    {
        DependencyObject? cur = this;
        while (cur != null)
        {
            if (cur is FrameworkElement { DataContext: ViewModels.PreviewViewModel vm }) return vm;
            cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : null;
        }
        return null;
    }

    private static void OnOwnerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 绑定值可能晚于 Loaded 到达，到了就补一次回灌
        if (d is TileHost host && host.IsLoaded) host.AttachToViewModel();
    }

    public TileHost()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        // 事件只挂一次（Rebuild 会被反复调用，重复挂钩会一次点击触发多次）
        Loaded += OnHostLoaded;
        Unloaded += OnHostUnloaded;
        IsVisibleChanged += OnHostIsVisibleChanged;
        MouseDown += OnHostMouseDown;
        _eventsWired = true;
    }

    /// <summary>上次点击的时刻：WinForms 侧双击的兜底判定（见 OnPanelMouseDown）。</summary>
    private DateTime _lastPanelClickUtc;

    /// <summary>
    /// 空域兜底：本控件被 Collapsed（双击放大时其余格隐藏）时，WindowsFormsHost
    /// 里的 WinForms 子窗口不会跟着消失，它会以旧位置、旧画面盖在 WPF 内容之上，
    /// 看上去就是"放大后画面错位/别人的画面还压在上面"。这里显式同步 Visible。
    /// 注意**不要在这里停流**：隐藏只是看不见，流留着，还原时立刻就有画面。
    /// </summary>
    private void OnHostIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_panel != null) _panel.Visible = IsVisible;
    }

    private static void OnTileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TileHost host) return;
        // Tile 换了 → 旧内容作废；但真正建 Panel 还是要等 Loaded。
        // 已经加载过的（重建场景）这里立即建，否则等 Loaded 回调。
        if (host.IsLoaded) host.Rebuild();
    }

    private void OnHostLoaded(object sender, RoutedEventArgs e) => Rebuild();

    /// <summary>建/重建视频宿主。只在已加载状态下调用。</summary>
    private void Rebuild()
    {
        if (!_eventsWired) return;
        if (Tile == null) { return; }

        // 已经有宿主且内容没变：什么都不做（Loaded 会因布局变化重复触发）
        if (_host != null && _panel != null) return;

        DestroyPanel();

        var panel = new System.Windows.Forms.Panel
        {
            BackColor = System.Drawing.Color.Black,
            Dock = System.Windows.Forms.DockStyle.Fill,
            Visible = IsVisible,   // 建出来时可能正处于"放大隐藏"状态
        };

        _host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = panel };
        _panel = panel;

        // 事件先挂再挂进可视树：句柄创建可能就在 Content 赋值的那一瞬间同步发生
        panel.HandleCreated += OnPanelHandleCreated;
        // 鼠标事件挂在 WinForms 侧：空域原因，点在画面上的点击 WPF 收不到（见类注释）
        panel.MouseDown += OnPanelMouseDown;
        Content = _host;

        // 句柄通常在宿主挂进可视树后才创建。这里两种时机都兜：
        // 已创建就直接推，没创建就等 HandleCreated。
        if (panel.IsHandleCreated) AttachToViewModel();
    }

    private void OnPanelHandleCreated(object? sender, EventArgs e) => AttachToViewModel();

    private void AttachToViewModel()
    {
        if (Tile is null) return;
        if (_panel is not { IsHandleCreated: true }) return;
        var vm = Vm;
        if (vm is null) return;

        // 交给 VM 去取流。同一格重复调用是安全的（VM 内部会检查是否已开过）。
        _ = vm.StartTileAsync(Tile, _panel.Handle);
    }

    private void OnHostUnloaded(object sender, RoutedEventArgs e)
    {
        // 分屏数变化 / 换页都会走到这里：本格 Panel 即将销毁，
        // 必须让 VM 把这一路的流停掉，否则 SDK 会往已销毁的 HWND 上画。
        if (Tile != null && Vm is { } vm) vm.StopTile(Tile);
        DestroyPanel();
    }

    private void OnHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 兜底路径：正常情况下点击都被 Panel 吃掉（见类注释），走到这里的只有
        // TileHost 自身露出的边缝。判定逻辑与 WinForms 侧一致。
        if (Tile is null) return;
        var vm = Vm;
        if (vm is null) return;

        if (e.ClickCount == 2) vm.ToggleEnlargeFromView(Tile);
        else vm.SelectTileFromView(Tile);
    }

    /// <summary>
    /// WinForms Panel 上的点击：单击选中、双击放大/还原。
    ///
    /// 双击用「e.Clicks ≥ 2 或与上次点击间隔 ≤ 系统双击时限」双保险判定：
    /// Panel 的 StandardDoubleClick 样式理论上默认开着，但它是 Control 的
    /// 内部样式、没有公开保证，万一哪天不开了 Clicks 恒为 1，
    /// 时间兜底能保证双击放大不至于再静默失效一次。
    /// </summary>
    private void OnPanelMouseDown(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button != System.Windows.Forms.MouseButtons.Left || Tile is null) return;
        var vm = Vm;
        if (vm is null) return;

        bool dbl = e.Clicks >= 2 ||
                   (DateTime.Now - _lastPanelClickUtc).TotalMilliseconds
                       <= System.Windows.Forms.SystemInformation.DoubleClickTime;
        _lastPanelClickUtc = DateTime.Now;

        if (dbl) vm.ToggleEnlargeFromView(Tile);
        else vm.SelectTileFromView(Tile);
    }

    private void DestroyPanel()
    {
        if (_panel != null)
        {
            _panel.HandleCreated -= OnPanelHandleCreated;
            _panel.MouseDown -= OnPanelMouseDown;
            _panel = null;
        }
        _host = null;
        Content = null;
    }
}

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace HikDeployTool.Views;

/// <summary>
/// 让 PasswordBox 支持数据绑定。
///
/// PasswordBox.Password 刻意被设计成不可绑定；但在"批量激活 / 密码重置"这类场景里，
/// 口令必须交给 ViewModel 去调 SDK 接口，绕不开。这里用附加属性把它接上，
/// 并加了一道重入保护——否则"用户输入 → 写 VM → 属性通知回来 → 再写回 PasswordBox"
/// 会形成回环，表现为光标跳到末尾、中文输入法下字符顺序错乱。
/// </summary>
public static class PasswordBoxHelper
{
    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBoxHelper),
            new PropertyMetadata(false));

    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    public static readonly DependencyProperty AttachProperty =
        DependencyProperty.RegisterAttached(
            "Attach",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnAttachChanged));

    public static string GetBoundPassword(DependencyObject obj) => (string)obj.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject obj, string value) => obj.SetValue(BoundPasswordProperty, value);

    public static bool GetAttach(DependencyObject obj) => (bool)obj.GetValue(AttachProperty);

    public static void SetAttach(DependencyObject obj, bool value) => obj.SetValue(AttachProperty, value);

    private static bool GetUpdating(DependencyObject obj) => (bool)obj.GetValue(UpdatingProperty);

    private static void SetUpdating(DependencyObject obj, bool value) => obj.SetValue(UpdatingProperty, value);

    private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        box.PasswordChanged -= OnPasswordChanged;

        if (e.NewValue is not true) return;

        box.PasswordChanged += OnPasswordChanged;
        // 初始同步一次，避免界面上是空的而 ViewModel 里已经有值
        if (!GetUpdating(box)) box.Password = GetBoundPassword(box) ?? string.Empty;
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (GetUpdating(box)) return;

        var incoming = e.NewValue as string ?? string.Empty;
        if (box.Password == incoming) return;

        SetUpdating(box, true);
        try
        {
            box.Password = incoming;
        }
        finally
        {
            SetUpdating(box, false);
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        if (GetUpdating(box)) return;

        SetUpdating(box, true);
        try
        {
            SetBoundPassword(box, box.Password);
        }
        finally
        {
            SetUpdating(box, false);
        }
    }
}

/// <summary>
/// 列表自动跟随最新一行（日志页用）。
///
/// KeepAtEnd 单独做成可绑定的附加属性，这样"暂停自动滚动"直接绑到 ViewModel 的
/// AutoScroll 上，View 里一行代码都不用写。
///
/// 两个实现细节：
///   1. 滚动用 Background 优先级投递，保证发生在容器生成之后，否则会滚到"还不存在的位置"；
///   2. 订阅前先反注册旧处理器——附加属性可能被重复设置（页面切换、DataContext 变化），
///      不解除就会出现重复滚动甚至滚两次。
/// </summary>
public static class AutoScrollHelper
{
    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached("Handler", typeof(NotifyCollectionChangedEventHandler),
            typeof(AutoScrollHelper), new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(AutoScrollHelper),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty KeepAtEndProperty =
        DependencyProperty.RegisterAttached(
            "KeepAtEnd",
            typeof(bool),
            typeof(AutoScrollHelper),
            new PropertyMetadata(true));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static bool GetKeepAtEnd(DependencyObject obj) => (bool)obj.GetValue(KeepAtEndProperty);

    public static void SetKeepAtEnd(DependencyObject obj, bool value) => obj.SetValue(KeepAtEndProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl control) return;

        if (e.NewValue is not true)
        {
            Unsubscribe(control);
            return;
        }

        if (control.ItemsSource != null)
        {
            Subscribe(control);
            return;
        }

        // ItemsSource 可能比附加属性晚设置（XAML 属性顺序不确定），
        // 所以挂一次 Loaded 兜底，保证最终一定能接上。
        control.Loaded -= OnControlLoaded;
        control.Loaded += OnControlLoaded;
    }

    private static void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsControl control) return;
        control.Loaded -= OnControlLoaded;
        Subscribe(control);
    }

    private static void Subscribe(ItemsControl control)
    {
        Unsubscribe(control);
        if (control.ItemsSource is not INotifyCollectionChanged source) return;

        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Add) return;
            if (!GetKeepAtEnd(control)) return;

            control.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    FindScrollViewer(control)?.ScrollToEnd();
                }
                catch
                {
                    // 滚动失败不影响功能
                }
            }));
        };

        source.CollectionChanged += handler;
        control.SetValue(HandlerProperty, handler);
    }

    private static void Unsubscribe(ItemsControl control)
    {
        if (control.GetValue(HandlerProperty) is not NotifyCollectionChangedEventHandler old) return;
        if (control.ItemsSource is INotifyCollectionChanged source) source.CollectionChanged -= old;
        control.SetValue(HandlerProperty, null);
    }

    internal static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found != null) return found;
        }
        return null;
    }
}

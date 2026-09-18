using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace HikDeployTool.ViewModels;

/// <summary>轻量 INotifyPropertyChanged 基类（不引入任何第三方 MVVM 框架，保证离线可编译）。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>设置字段并在值变化时触发通知，返回是否真的发生变化。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            // 兜底：任何未处理异常都不应让整个界面崩溃
            Services.LogService.Current.Error("命令执行异常", ex);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令：内置"执行中"标记，避免用户连点造成重复执行 / 原生库并发异常。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _running;
        private set
        {
            _running = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter)
        => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        IsRunning = true;
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            Services.LogService.Current.Error("异步命令执行异常", ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 页面基类：每个功能页一个 ViewModel，标题/图标/顺序由子类提供。
/// 支持从"设备发现"页把选中的设备"投递"到其它页面。
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _isSelected;

    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public abstract string Description { get; }

    /// <summary>是否为当前显示的页面（由 MainViewModel 维护，用于导航高亮）。</summary>
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }

    /// <summary>命令面板里显示的一行说明，用于解释本页适用场景。</summary>
    public virtual string Usage => string.Empty;

    /// <summary>接收从其它页面投递过来的设备（默认不处理）。</summary>
    public virtual void OnDevicesInjected(IReadOnlyList<Models.DiscoveredDevice> devices) { }

    /// <summary>页面被切换到前台时调用（可用于懒加载 / 刷新列表）。</summary>
    public virtual void OnEnter() { }

    /// <summary>由 MainViewModel 注入的页面切换器。</summary>
    internal MainViewModel? Host { get; set; }

    /// <summary>全局共享状态（设备列表 / 设置 / 原生服务）。</summary>
    protected static Services.AppState App => Services.AppState.Current;

    /// <summary>当前在"设备发现"页勾选的设备。</summary>
    protected static IReadOnlyList<Models.DiscoveredDevice> PickedDevices => App.SelectedDevices();

    protected void Toast(string message, bool isError = false)
        => Host?.Notify(message, isError);

    /// <summary>切到指定页面（供"下一步"之类的引导按钮使用）。</summary>
    protected void GoTo<T>() where T : PageViewModel => Host?.NavigateTo<T>();

    /// <summary>在状态栏显示"正在执行"提示，返回可用于 using 的作用域。</summary>
    protected IDisposable Busy(string text) => Host?.BeginBusy(text) ?? EmptyScope.Instance;

    /// <summary>后台线程执行 + 统一异常兜底。UI 更新请自行回到 UI 线程。</summary>
    protected static Task RunAsync(Action action) => Task.Run(action);

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>把 ObservableCollection 的批量操作封装得简洁一些。</summary>
public static class CollectionExtensions
{
    public static void AddRange<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        foreach (var item in items) target.Add(item);
    }

    public static void ReplaceAll<T>(this ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

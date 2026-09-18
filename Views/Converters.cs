using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HikDeployTool.Models;

namespace HikDeployTool.Views;

/// <summary>
/// 把 ViewModel 里的"资源键名"（如 IconSearch）转成实际 Geometry。
///
/// 为什么绕这一下：页面 ViewModel 不应该引用 System.Windows.Media（那是视图层的职责），
/// 但导航栏又必须按页面显示不同图标。于是让 ViewModel 只吐一个字符串键，
/// 由这个转换器去资源字典里查——两边各守各的边界。
/// </summary>
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key)) return null;
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>日志等级 → 颜色（走资源字典，便于统一改配色）。</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is LogLevel level
            ? level switch
            {
                LogLevel.Debug => "BrushLogDebug",
                LogLevel.Success => "BrushLogSuccess",
                LogLevel.Warn => "BrushLogWarn",
                LogLevel.Error => "BrushLogError",
                _ => "BrushLogInfo",
            }
            : "BrushTextSecondary";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>日志等级 → 中文短标签（列表里做前置标记）。</summary>
public sealed class LogLevelToTagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel level
            ? level switch
            {
                LogLevel.Debug => "调试",
                LogLevel.Success => "成功",
                LogLevel.Warn => "警告",
                LogLevel.Error => "错误",
                _ => "信息",
            }
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 布尔 → 可见性。默认 true 显示；传参数 "Invert" 则反过来。
/// 也接受 null（当作 false），这样绑定到可空 bool 的属性时不会抛异常。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
        return visible;
    }
}

/// <summary>布尔取反。常用于 IsEnabled 与"正在执行"之间的联动。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>非空（或有内容）→ Visible。用于"有结果才显示某块区域"。</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 多绑定引用相等比较：两个输入是同一实例（或同为 null）→ true，否则 false。
/// 用于「清单里哪台设备被选中」的 RadioButton 单选联动：
/// 绑定 [VM.SelectedTarget, 当前项]，选中项打点、其余熄灭；
/// 写方向（点选）由 Command 设置 VM 属性，本转换器只负责读方向的视觉同步。
/// </summary>
public sealed class ReferenceEqualsToBoolConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length == 2 && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => [Binding.DoNothing];
}

/// <summary>空 → Visible（与上面相反），用于"没有数据时显示引导文案"。</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            int i => i == 0,
            System.Collections.ICollection c => c.Count == 0,
            _ => false,
        };
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 取整数数量的前 N 项是否大于 0 → 用于集合"有没有内容"的可见性判断。
/// 之所以不直接用 Count 绑定，是因为 ObservableCollection 的 Count 属性变化通知
/// 在部分模板场景下不会自动刷新可见性，用转换器 + 显式 PropertyChanged 更稳。
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        int threshold = 1;
        if (parameter is string s && int.TryParse(s, out var t)) threshold = t;
        return count >= threshold ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

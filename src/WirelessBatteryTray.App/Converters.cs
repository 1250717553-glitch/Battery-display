using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WirelessBatteryTray.App;

/// <summary>bool -> Visibility。true 显示，false 折叠。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>
/// 资源键字符串 -> 实际资源对象（Geometry / Brush 皆可）。
/// 用于把 Model 层的「键名」解耦成 XAML 可绑定的几何与颜色，避免在 XAML 里堆 DataTrigger。
/// </summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrEmpty(key) && Application.Current is { } app)
        {
            return app.TryFindResource(key);
        }

        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System;

namespace WirelessBatteryTray.Core;

/// <summary>
/// 进程内服务门面：App 启动时装配一次，UI 从这里取依赖。
/// 装配方（App.xaml.cs）负责 new DeviceService(...)，UI 不自行构造具体实现。
/// </summary>
public static class AppServices
{
    public static IConfigStore Config { get; set; } = new JsonConfigStore();

    public static ILog Log { get; set; } = new FileLog();

    public static IDeviceService? Device { get; set; }

    public static IDeviceService RequireDevice() =>
        Device ?? throw new InvalidOperationException("AppServices.Device 尚未装配。");
}

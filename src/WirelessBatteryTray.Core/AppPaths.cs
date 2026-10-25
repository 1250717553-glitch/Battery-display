using System;
using System.IO;

namespace WirelessBatteryTray.Core;

/// <summary>落盘位置：%LOCALAPPDATA%\\WirelessBatteryTray\\（决策 Q4=A）。</summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WirelessBatteryTray");

    public static string ConfigFile => Path.Combine(Root, "config.json");

    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
    }
}

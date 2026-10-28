using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WirelessBatteryTray.Core;

public sealed class AppConfig
{
    public Dictionary<string, string> Aliases { get; set; } = new();

    /// <summary>
    /// 后台刷新间隔（秒）。默认 120：太短会频繁重建蓝牙 GATT 连接（可能干扰音频设备），
    /// 太长则托盘数字明显滞后。120 秒是两者折中。
    /// </summary>
    public int RefreshIntervalSeconds { get; set; } = 120;

    public int WarningThresholdPercent { get; set; } = 20;

    /// <summary>低电量报警触发阈值（默认 20）。在线设备电量 ≤ 此值时报警一次。</summary>
    public int LowBatteryThreshold { get; set; } = 20;

    /// <summary>
    /// 报警重置阈值（默认 25，必须大于 LowBatteryThreshold）。
    /// 电量回升到此值以上才清除「已报警」状态，形成滞回，避免在阈值附近反复响铃。
    /// </summary>
    public int LowBatteryResetThreshold { get; set; } = 25;

    /// <summary>低电量报警是否播放提示音。关闭后仍会变红，只是不响。</summary>
    public bool LowBatterySoundEnabled { get; set; } = true;

    public int CriticalThresholdPercent { get; set; } = 10;

    public bool ShowPercentOnTray { get; set; } = true;

    /// <summary>超过此分钟数的电量读数标注为「过期」。</summary>
    public int StaleAfterMinutes { get; set; } = 10;

    /// <summary>P2 自动隐藏触发热区厚度（物理像素）。默认 3，设置页后续做成可调。</summary>
    public int HotzoneThickness { get; set; } = 3;

    /// <summary>
    /// 上次已知电量快照（StableId -> 读数）。必须持久化：
    /// 设备关机或程序重启后，UI 仍要能显示「上次已知 N%」而不是退化成未知。
    /// </summary>
    public Dictionary<string, PersistedBattery> LastKnownBatteries { get; set; } = new();
}

/// <summary>持久化的电量读数（只存必要字段，避免把易变的连接状态写进配置）。</summary>
public sealed class PersistedBattery
{
    public int Percent { get; set; }

    public DateTimeOffset ObservedAt { get; set; }
}

public interface IConfigStore
{
    AppConfig Load();

    void Save(AppConfig config);
}

public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly object _gate = new();

    public AppConfig Load()
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();
                if (!File.Exists(AppPaths.ConfigFile))
                {
                    return new AppConfig();
                }

                var json = File.ReadAllText(AppPaths.ConfigFile);
                AppConfig config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
                return Migrate(config);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new AppConfig();
            }
        }
    }

    /// <summary>
    /// 一次性配置迁移。老版本把后台刷新间隔默认成 45 秒，那会导致蓝牙 GATT
    /// 连接过于频繁；新版本改为 120 秒。若用户没手动改过（仍是 45），升级它。
    /// 手动改成其它值的保持不动。
    /// </summary>
    private static AppConfig Migrate(AppConfig config)
    {
        const int LegacyDefaultInterval = 45;
        const int NewDefaultInterval = 120;

        if (config.RefreshIntervalSeconds == LegacyDefaultInterval)
        {
            config.RefreshIntervalSeconds = NewDefaultInterval;
        }

        return config;
    }

    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(config, Options);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
        }
    }
}

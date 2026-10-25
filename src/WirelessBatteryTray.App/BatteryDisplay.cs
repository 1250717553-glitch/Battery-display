using System;
using System.Globalization;
using WirelessBatteryTray.Core;

namespace WirelessBatteryTray.App;

public enum BatteryTier
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>
/// 显示层唯一的电量文案出口。需求 §5 硬约束：读不到电量绝不显示 0%。
/// 分三条互不相同的分支：Available（真实读数，0% 就显示 0%）/
/// ConnectionOnly（仅连接状态）/ Unknown（未知设备）。
/// </summary>
public static class BatteryDisplay
{
    public const string UnknownText = "未知";
    public const string ConnectedNoBatteryText = "已连接，电量未知";
    public const string OfflineNoBatteryText = "离线，电量未知";

    /// <summary>只有 Available 且确实带读数，才算「有电量」。</summary>
    public static bool HasPercent(DeviceSnapshot device) =>
        device.Availability == BatteryAvailability.Available && device.Battery is not null;

    /// <summary>电量列文案：三条分支互斥，非 Available 分支永远不会走到数字格式化。</summary>
    public static string PercentText(DeviceSnapshot device)
    {
        if (device.Availability == BatteryAvailability.Available)
        {
            // Available 但读数缺失时仍然不能回落成 0%。
            return device.Battery is null
                ? UnknownText
                : device.Battery.Percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        if (device.Availability == BatteryAvailability.ConnectionOnly)
        {
            // 离线但有上次缓存电量的，显示「上次已知 N%」，避免用户以为这台设备从来没读到过电量。
            if (device.Connection != ConnectionStatus.Connected && device.Battery is not null)
            {
                return "上次已知 " + device.Battery.Percent.ToString(CultureInfo.InvariantCulture) + "%";
            }

            // 「已连接」只在真的在线时才成立；离线且无缓存时明确写「离线，电量未知」。
            return device.Connection == ConnectionStatus.Connected
                ? ConnectedNoBatteryText
                : OfflineNoBatteryText;
        }

        return UnknownText;
    }

    public static string StatusText(DeviceSnapshot device)
    {
        return device.Connection switch
        {
            ConnectionStatus.Connected => device.IsCharging ? "在线·充电中" : "在线",
            ConnectionStatus.Disconnected => "离线",
            _ => UnknownText,
        };
    }

    public static string KindText(DeviceSnapshot device) => device.Kind switch
    {
        DeviceKind.Mouse => "鼠标",
        DeviceKind.Keyboard => "键盘",
        DeviceKind.Headset => "耳机",
        _ => "其他",
    };

    public static string TransportText(DeviceSnapshot device) => device.Transport switch
    {
        DeviceTransport.Hid24Ghz => "2.4G",
        DeviceTransport.BluetoothLE => "BLE",
        DeviceTransport.BluetoothClassic => "经典蓝牙",
        _ => "未知",
    };

    /// <summary>UI 侧对别名的叠加：key 就是 StableId 原值，不做任何再加工。</summary>
    public static string DisplayName(DeviceSnapshot device, AppConfig config)
    {
        if (config.Aliases.TryGetValue(device.StableId, out var alias) && !string.IsNullOrWhiteSpace(alias))
        {
            return alias;
        }

        return device.SystemName;
    }

    /// <summary>设备类型 -> 矢量图标资源键（在 Themes/DeviceIcons.xaml 中定义）。</summary>
    public static string KindIconKey(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "IconMouse",
        DeviceKind.Keyboard => "IconKeyboard",
        DeviceKind.Headset => "IconHeadset",
        _ => "IconGeneric",
    };

    /// <summary>电量分档对应的前景色资源键，供卡片百分比文字绑定。</summary>
    public static string PercentBrushKey(DeviceSnapshot device, AppConfig config)
    {
        if (!HasPercent(device))
        {
            return "TierNoneBrush";
        }

        var percent = device.Battery!.Percent;
        if (percent <= config.CriticalThresholdPercent)
        {
            return "TierLowBrush";
        }

        return percent <= config.WarningThresholdPercent ? "TierMediumBrush" : "TierHighBrush";
    }

    /// <summary>电量归一化到 0..1，用于卡片进度条；读不到电量时为 0。</summary>
    public static double LevelOf(DeviceSnapshot device, AppConfig config) =>
        HasPercent(device) ? Math.Clamp(device.Battery!.Percent / 100.0, 0.0, 1.0) : 0.0;

    public static BatteryTier TierOf(DeviceSnapshot device, AppConfig config)
    {
        if (!HasPercent(device))
        {
            return BatteryTier.None;
        }

        var percent = device.Battery!.Percent;
        if (percent <= config.CriticalThresholdPercent)
        {
            return BatteryTier.Low;
        }

        return percent <= config.WarningThresholdPercent ? BatteryTier.Medium : BatteryTier.High;
    }
}

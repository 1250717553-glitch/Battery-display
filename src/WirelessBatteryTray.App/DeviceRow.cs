using System;
using System.ComponentModel;
using System.Globalization;
using WirelessBatteryTray.Core;

namespace WirelessBatteryTray.App;

/// <summary>
/// 设备卡片的一行数据。所有展示文案/颜色/图标都由这里决定，
/// 窗口 XAML 只做绑定——避免同一套判定散落在多处。
/// </summary>
public sealed class DeviceRow : INotifyPropertyChanged
{
    private string _displayName;
    private bool _alerted;

    public DeviceRow(DeviceSnapshot snapshot, AppConfig config, DateTimeOffset now)
    {
        Snapshot = snapshot;
        _displayName = BatteryDisplay.DisplayName(snapshot, config);
        PercentText = BatteryDisplay.PercentText(snapshot);
        StatusText = BatteryDisplay.StatusText(snapshot);
        KindText = BatteryDisplay.KindText(snapshot);
        TransportText = BatteryDisplay.TransportText(snapshot);
        HasAlias = config.Aliases.ContainsKey(snapshot.StableId);
        IsStale = snapshot.IsStale(now, TimeSpan.FromMinutes(Math.Max(1, config.StaleAfterMinutes)));
        IsOnline = snapshot.Connection == ConnectionStatus.Connected;

        // 图标用几何路径而不是图片，避免额外资源文件与 DPI 模糊。
        KindIcon = BatteryDisplay.KindIconKey(snapshot.Kind);
        TransportBadge = TransportText;
        Level = BatteryDisplay.LevelOf(snapshot, config);
        _percentBrushKey = BatteryDisplay.PercentBrushKey(snapshot, config);
    }

    /// <summary>
    /// 由窗口在「低电量报警」触发后调用，把该设备的电量字变红。
    /// 单独一个属性而不是塞进构造，是因为报警状态由 TrayIconController 持有并随时间变化。
    /// </summary>
    public bool IsLowBatteryAlerted
    {
        get => _alerted;
        set
        {
            if (_alerted != value)
            {
                _alerted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLowBatteryAlerted)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PercentBrushKey)));
            }
        }
    }

    public DeviceSnapshot Snapshot { get; }

    public string StableId => Snapshot.StableId;

    public string SystemName => Snapshot.SystemName;

    /// <summary>副标题：系统名 · 来源（有别名时才显示系统名，避免重复）。</summary>
    public string Subtitle => HasAlias
        ? SystemName + " · " + TransportBadge
        : TransportBadge;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }
    }

    public string PercentText { get; }

    /// <summary>可用 DynamicResource 键名，供 XAML 绑定到主题色。</summary>
    public string PercentBrushKey => _alerted ? "AlertBrush" : _percentBrushKey;

    private readonly string _percentBrushKey = "TierNoneBrush";

    /// <summary>电量分档（0..1）。</summary>
    public double Level { get; }

    /// <summary>进度条前景宽度（像素）。轨道宽 180，按 Level 比例给值。</summary>
    public double LevelWidth => Level * 180.0;

    /// <summary>是否有真实电量读数（决定进度条与百分比是否显示）。</summary>
    public bool HasPercent => Snapshot.Availability == BatteryAvailability.Available && Snapshot.Battery is not null;

    public string KindIcon { get; }

    public string KindText { get; }

    public string TransportText { get; }

    public string TransportBadge { get; }

    public string StatusText { get; }

    public bool HasAlias { get; }

    public bool IsStale { get; }

    public bool IsOnline { get; }

    public string StaleText => IsStale ? "过期" : string.Empty;

    /// <summary>在线绿点 / 离线灰点。</summary>
    public string StatusDotBrushKey => IsOnline ? "OnlineDotBrush" : "OfflineDotBrush";

    public event PropertyChangedEventHandler? PropertyChanged;
}

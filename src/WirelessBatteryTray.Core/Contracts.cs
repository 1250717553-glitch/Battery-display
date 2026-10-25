using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessBatteryTray.Core;

/// <summary>设备来源通道。</summary>
public enum DeviceTransport
{
    Hid24Ghz,
    BluetoothLE,
    BluetoothClassic,
}

public enum DeviceKind
{
    Mouse,
    Keyboard,
    Headset,
    Other,
}

public enum ConnectionStatus
{
    Connected,
    Disconnected,
    Unknown,
}

/// <summary>支持级别三档：电量可用 / 仅连接状态 / 未知设备。</summary>
public enum BatteryAvailability
{
    Available,
    ConnectionOnly,
    Unknown,
}

public sealed record BatteryReading(int Percent, DateTimeOffset ObservedAt);

/// <summary>一次枚举得到的设备快照，UI 只消费这个类型。</summary>
public sealed record DeviceSnapshot
{
    public required string StableId { get; init; }
    public required string SystemName { get; init; }
    public string? DisplayAlias { get; init; }
    public required DeviceKind Kind { get; init; }
    public required DeviceTransport Transport { get; init; }
    public required ConnectionStatus Connection { get; init; }
    public BatteryReading? Battery { get; init; }
    public bool IsCharging { get; init; }
    public required BatteryAvailability Availability { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(DisplayAlias) ? SystemName : DisplayAlias!;

    /// <summary>电量读数是否已过期（无读数视为过期）。</summary>
    public bool IsStale(DateTimeOffset now, TimeSpan staleAfter) =>
        Availability == BatteryAvailability.Available
            ? Battery is null || now - Battery.ObservedAt > staleAfter
            : true;
}

/// <summary>适配层接口（需求 §4）。每个协议族一个实现。</summary>
public interface IDeviceProvider
{
    string ProviderId { get; }
    DeviceTransport Transport { get; }

    Task<IReadOnlyList<DeviceSnapshot>> ProbeAsync(CancellationToken ct);

    Task<BatteryReading?> ReadBatteryAsync(string stableId, CancellationToken ct);

    Task<bool?> ReadChargingAsync(string stableId, CancellationToken ct);

    BatteryAvailability CapabilitiesFor(string stableId);
}

/// <summary>UI 唯一依赖的服务门面。</summary>
public interface IDeviceService
{
    Task<IReadOnlyList<DeviceSnapshot>> GetSnapshotAsync(bool refreshBatteries, CancellationToken ct = default);

    Task<bool?> RefreshBatteryAsync(string stableId, CancellationToken ct = default);

    event EventHandler? DevicesChanged;

    void Start();

    void Stop();

    /// <summary>请求尽快重算一轮（配置变更后调用，避免等满一个刷新间隔）。</summary>
    void RequestRefresh();
}

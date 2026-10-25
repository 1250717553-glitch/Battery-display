using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WirelessBatteryTray.Core;

namespace WirelessBatteryTray.App;

/// <summary>
/// 仅 --demo 使用的本地假数据（界面开发/联调用）。交付默认路径走 AppServices，不碰这个类。
///
/// 关键设计（供「别名按 StableId 持久化」行为验证）：
///  - StableId 只由「设备身份」组成（VID/PID/MI/COL/UsagePage/Usage/序列号 的组合），不含易变部分。
///  - --demo-replug 只改易变部分（换 USB 口/重插），它出现在 SystemName 里，StableId 不变 -> 别名必须仍然命中。
///  - --demo-serial 改的是身份的一部分 -> StableId 变化 -> 别名不应命中（证明 key 真的是 StableId）。
/// </summary>
public sealed class DemoDeviceService : IDeviceService
{
    private const string Vid = "VID_046D";
    private const string Pid = "PID_B034";

    private readonly object _gate = new();
    private readonly string _serial;
    private readonly string _replug;
    private IReadOnlyList<DeviceSnapshot> _cache;

    public DemoDeviceService(string? serial, string? replug)
    {
        _serial = string.IsNullOrWhiteSpace(serial) ? "SN-DEMO-0001" : serial.Trim();
        _replug = string.IsNullOrWhiteSpace(replug) ? string.Empty : replug.Trim();
        _cache = Build(DateTimeOffset.Now);
        AppServices.Log.Info("demo 设备已生成：serial=" + _serial + " replug=" + (_replug.Length == 0 ? "(none)" : _replug));
        foreach (var device in _cache)
        {
            AppServices.Log.Info("demo device StableId=" + device.StableId + " name=" + device.SystemName);
        }
    }

    public event EventHandler? DevicesChanged;

    public void Start() => AppServices.Log.Info("DemoDeviceService.Start");

    public void Stop() => AppServices.Log.Info("DemoDeviceService.Stop");

    public void RequestRefresh()
    {
        // demo 数据无外部状态，重新生成一次即可
        lock (_gate)
        {
            _cache = Build(DateTimeOffset.Now);
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> GetSnapshotAsync(bool refreshBatteries, CancellationToken ct = default)
    {
        if (refreshBatteries)
        {
            // 模拟一次真实读取的耗时，避免 UI 侧误以为同步返回。
            await Task.Delay(120, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _cache = Build(DateTimeOffset.Now);
            }

            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        lock (_gate)
        {
            return _cache.ToArray();
        }
    }

    public Task<bool?> RefreshBatteryAsync(string stableId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var device = _cache.FirstOrDefault(d => d.StableId == stableId);
            return Task.FromResult(device?.Battery is { } ? (bool?)true : null);
        }
    }

    private string StableIdFor(string transport, string suffix) =>
        "demo://" + transport + "/" + Vid + "&" + Pid + "/" + suffix + "/SN_" + _serial;

    private IReadOnlyList<DeviceSnapshot> Build(DateTimeOffset now)
    {
        var volatileTag = _replug.Length == 0 ? string.Empty : " [" + _replug + "]";
        var replugged = _replug.Length != 0;

        // 1) 正常 2.4G 鼠标，85%
        var mouse = new DeviceSnapshot
        {
            StableId = StableIdFor("hid", "MI_02/COL_01/UP_0001/U_0002"),
            SystemName = "Logitech MX Master 3S" + volatileTag,
            Kind = DeviceKind.Mouse,
            Transport = DeviceTransport.Hid24Ghz,
            Connection = ConnectionStatus.Connected,
            Battery = new BatteryReading(85, replugged ? now : now.AddMinutes(-1)),
            IsCharging = false,
            Availability = BatteryAvailability.Available,
        };

        // 2) BLE 键盘，45%，充电中
        var keyboard = new DeviceSnapshot
        {
            StableId = StableIdFor("ble", "MI_00/COL_01/UP_0001/U_0006"),
            SystemName = "Keychron K3" + volatileTag,
            Kind = DeviceKind.Keyboard,
            Transport = DeviceTransport.BluetoothLE,
            Connection = ConnectionStatus.Connected,
            Battery = new BatteryReading(45, now.AddMinutes(-1)),
            IsCharging = true,
            Availability = BatteryAvailability.Available,
        };

        // 3) 经典蓝牙耳机，30%，读数已过期（30 分钟前）
        var headset = new DeviceSnapshot
        {
            StableId = StableIdFor("classic", "MI_03/COL_02/UP_0002/U_0001"),
            SystemName = "Sony WH-1000XM4" + volatileTag,
            Kind = DeviceKind.Headset,
            Transport = DeviceTransport.BluetoothClassic,
            Connection = ConnectionStatus.Connected,
            Battery = new BatteryReading(30, now.AddMinutes(-30)),
            IsCharging = false,
            Availability = BatteryAvailability.Available,
        };

        // 4) 真实 0%：Available 且 Percent=0 —— 允许显示 0%
        var deadMouse = new DeviceSnapshot
        {
            StableId = StableIdFor("hid", "MI_01/COL_01/UP_0001/U_0002"),
            SystemName = "Razer Viper 8K (0%)" + volatileTag,
            Kind = DeviceKind.Mouse,
            Transport = DeviceTransport.Hid24Ghz,
            Connection = ConnectionStatus.Connected,
            Battery = new BatteryReading(0, now.AddMinutes(-2)),
            IsCharging = false,
            Availability = BatteryAvailability.Available,
        };

        // 5) ConnectionOnly：在线但读不到电量 —— 必须显示「已连接，电量未知」，绝不能是 0%
        var connectionOnly = new DeviceSnapshot
        {
            StableId = StableIdFor("hid", "MI_04/COL_01/UP_0001/U_0001"),
            SystemName = "Dell 无线接收器 (仅连接)" + volatileTag,
            Kind = DeviceKind.Other,
            Transport = DeviceTransport.Hid24Ghz,
            Connection = ConnectionStatus.Connected,
            Battery = null,
            IsCharging = false,
            Availability = BatteryAvailability.ConnectionOnly,
        };

        // 6) Unknown：未知设备，连接与电量都未知
        var unknownDevice = new DeviceSnapshot
        {
            StableId = StableIdFor("classic", "MI_05/COL_03/UP_0003/U_0002"),
            SystemName = "未识别蓝牙设备" + volatileTag,
            Kind = DeviceKind.Other,
            Transport = DeviceTransport.BluetoothClassic,
            Connection = ConnectionStatus.Unknown,
            Battery = null,
            IsCharging = false,
            Availability = BatteryAvailability.Unknown,
        };

        return new[] { mouse, keyboard, deadMouse, headset, connectionOnly, unknownDevice };
    }
}

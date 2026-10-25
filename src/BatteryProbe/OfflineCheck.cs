using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WirelessBatteryTray.Core;

namespace BatteryProbe;

/// <summary>
/// 离线降级的真实路径验证：直接驱动生产代码 DeviceService（而非重写一份逻辑），
/// 让假 provider 先在线报 69%，再离线，检查缓存电量是否保留、快照是否降级。
/// </summary>
internal static class OfflineCheck
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 离线降级路径验证（驱动真实 DeviceService）===");

        var provider = new FakeProvider();
        var service = new DeviceService(new IDeviceProvider[] { provider });
        service.Start();
        await Task.Delay(300);

        Console.WriteLine("provider 被探测次数(初始)=" + provider.ProbeCount);
        var online = await service.GetSnapshotAsync(true);
        Console.WriteLine("provider 被探测次数(在线后)=" + provider.ProbeCount);
        Console.WriteLine("在线快照: " + Describe(online));

        // 用户「关掉鼠标再点刷新」在时间上必然远超 400ms 合并窗口，这里如实模拟该延迟。
        await Task.Delay(500);

        provider.GoOffline();
        Console.WriteLine("provider 被探测次数(置离线后)=" + provider.ProbeCount);
        var offline = await service.GetSnapshotAsync(true);
        Console.WriteLine("provider 被探测次数(离线后)=" + provider.ProbeCount);
        Console.WriteLine("离线快照: " + Describe(offline));

        service.Stop();
        ((IDisposable)service).Dispose();

        var device = offline.Count > 0 ? offline[0] : null;
        bool ok = device is not null
            && device.Connection == ConnectionStatus.Disconnected
            && device.Battery is not null
            && device.Battery.Percent == 69;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "结论: 通过 —— 离线后仍保留上次已知 69%，未变成未知/0%。"
            : "结论: 失败 —— 离线后电量缓存丢失。");
        return ok ? 0 : 1;
    }

    private static string Describe(IReadOnlyList<DeviceSnapshot> list)
    {
        if (list.Count == 0)
        {
            return "(空)";
        }

        var d = list[0];
        string battery;
        if (d.Battery is not null && d.Connection != ConnectionStatus.Connected)
        {
            battery = "上次已知 " + d.Battery.Percent + "%";
        }
        else if (d.Battery is not null)
        {
            battery = d.Battery.Percent + "%";
        }
        else
        {
            battery = d.Availability == BatteryAvailability.ConnectionOnly ? "已连接，电量未知" : "未知";
        }

        return d.SystemName + " | " + d.Connection + " | " + battery;
    }

    private sealed class FakeProvider : IDeviceProvider
    {
        private bool _online = true;

        public string ProviderId => "fake";

        public int ProbeCount { get; private set; }

        public DeviceTransport Transport => DeviceTransport.BluetoothLE;

        public void GoOffline() => _online = false;

        public Task<IReadOnlyList<DeviceSnapshot>> ProbeAsync(CancellationToken ct)
        {
            ProbeCount++;
            var snapshot = new DeviceSnapshot
            {
                StableId = "bt:fake",
                SystemName = "Inphic IN9",
                Kind = DeviceKind.Mouse,
                Transport = DeviceTransport.BluetoothLE,
                Connection = _online ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                Battery = _online ? new BatteryReading(69, DateTimeOffset.Now) : null,
                IsCharging = false,
                Availability = _online ? BatteryAvailability.Available : BatteryAvailability.ConnectionOnly,
            };

            return Task.FromResult<IReadOnlyList<DeviceSnapshot>>(new[] { snapshot });
        }

        public Task<BatteryReading?> ReadBatteryAsync(string stableId, CancellationToken ct) =>
            Task.FromResult<BatteryReading?>(null);

        public Task<bool?> ReadChargingAsync(string stableId, CancellationToken ct) => Task.FromResult<bool?>(null);

        public BatteryAvailability CapabilitiesFor(string stableId) => BatteryAvailability.ConnectionOnly;
    }
}

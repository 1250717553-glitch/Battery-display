using System;
using System.Collections.Generic;
using WirelessBatteryTray.Core;

namespace BatteryProbe;

/// <summary>低电量报警滞回逻辑验证：直接驱动生产代码 LowBatteryMonitor。</summary>
internal static class AlertCheck
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 低电量报警滞回验证 ===");

        var config = new AppConfig { LowBatteryThreshold = 20, LowBatteryResetThreshold = 25 };
        var monitor = new LowBatteryMonitor();
        int pass = 0, fail = 0;

        // 1) 25% 不报警
        Check(monitor.Evaluate(One(25, ConnectionStatus.Connected), config, N).Count == 0,
            "25% 高于阈值，不报警", ref pass, ref fail);

        // 2) 降到 20%（等于阈值）应报警
        var first = monitor.Evaluate(One(20, ConnectionStatus.Connected), config, N);
        Check(first.Count == 1 && first[0].Percent == 20, "20% 首次跌破，报警一次", ref pass, ref fail);

        // 3) 再次 19%，不应重复
        Check(monitor.Evaluate(One(19, ConnectionStatus.Connected), config, N).Count == 0,
            "继续 19%，不重复报警（防抖）", ref pass, ref fail);

        // 4) 回升到 24%（低于重置 25）仍不应重复
        Check(monitor.Evaluate(One(24, ConnectionStatus.Connected), config, N).Count == 0,
            "回升到 24% 未达重置阈值，仍不报警", ref pass, ref fail);

        // 5) 回升到 25% 解除状态
        Check(monitor.Evaluate(One(25, ConnectionStatus.Connected), config, N).Count == 0,
            "回升到 25% 解除已报警状态", ref pass, ref fail);
        Check(!monitor.IsAlerted("dev"), "状态已清除", ref pass, ref fail);

        // 6) 再次跌破 20% 应重新报警
        Check(monitor.Evaluate(One(18, ConnectionStatus.Connected), config, N).Count == 1,
            "重新跌破，再次报警（滞回有效）", ref pass, ref fail);

        // 7) 充电中不报警
        var monitor2 = new LowBatteryMonitor();
        Check(monitor2.Evaluate(One(10, ConnectionStatus.Connected, charging: true), config, N).Count == 0,
            "充电中不报警", ref pass, ref fail);

        // 8) 读不到电量不报警
        Check(monitor2.Evaluate(One(0, ConnectionStatus.Connected, available: false), config, N).Count == 0,
            "电量未知不报警（不显示 0% 的延伸）", ref pass, ref fail);

        // 9) 离线不报警
        Check(monitor2.Evaluate(One(5, ConnectionStatus.Disconnected), config, N).Count == 0,
            "离线设备不报警", ref pass, ref fail);

        // 10) 断开重连后重置状态
        var monitor3 = new LowBatteryMonitor();
        Check(monitor3.Evaluate(One(15, ConnectionStatus.Connected), config, N).Count == 1,
            "15% 报警", ref pass, ref fail);
        Check(monitor3.Evaluate(One(15, ConnectionStatus.Disconnected), config, N).Count == 0,
            "断开不报警", ref pass, ref fail);
        Check(monitor3.Evaluate(One(15, ConnectionStatus.Connected), config, N).Count == 1,
            "重连后再次跌破，重新报警", ref pass, ref fail);

        Console.WriteLine();
        Console.WriteLine("结果: " + pass + " 通过 / " + fail + " 失败");
        return fail == 0 ? 0 : 1;
    }

    private static string N(DeviceSnapshot d) => d.SystemName;

    private static IReadOnlyList<DeviceSnapshot> One(
        int percent,
        ConnectionStatus connection,
        bool charging = false,
        bool available = true) =>
        new[]
        {
            new DeviceSnapshot
            {
                StableId = "dev",
                SystemName = "TestDevice",
                Kind = DeviceKind.Mouse,
                Transport = DeviceTransport.BluetoothLE,
                Connection = connection,
                Battery = available ? new BatteryReading(percent, DateTimeOffset.Now) : null,
                IsCharging = charging,
                Availability = available ? BatteryAvailability.Available : BatteryAvailability.ConnectionOnly,
            },
        };

    private static void Check(bool ok, string label, ref int pass, ref int fail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + label);
        if (ok) { pass++; } else { fail++; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WirelessBatteryTray.Core.Providers;

namespace WirelessBatteryTray.Core;

/// <summary>
/// 2.4G 接收器族适配器（默认目标 VID 0x249A / PID 0x5C2F）+ 通用 HID Battery 探测。
/// 换用其它接收器时，修改下面的 TargetVendorId / TargetProductId 即可。
/// 严格只读：只枚举、只读输入/Feature 报文，绝不向设备写任何数据。
/// </summary>
public sealed class Hid24GhzProvider : IDeviceProvider
{
    private const ushort TargetVendorId = 0x249A;
    private const ushort TargetProductId = 0x5C2F;

    public string ProviderId => "hid-24g";

    public DeviceTransport Transport => DeviceTransport.Hid24Ghz;

    /// <summary>
    /// 电量解析的唯一收口点。当前无报文规格，恒返回 false，
    /// 上层据此降级为「已连接，电量未知」，绝不伪造 0%。
    /// 嗅探工具（BatteryProbe）产出对照表后，只改这个方法即可上线解析。
    /// </summary>
    public static bool TryParseBatteryFromInputReport(byte[] report, out int percent)
    {
        // TODO(协议): 待 BatteryProbe 的逐字节对照表确定电量字节位置/量纲后实现。
        // 约束：宁可返回 false 降级，也不返回猜测值。
        percent = 0;
        return false;
    }

    public Task<IReadOnlyList<DeviceSnapshot>> ProbeAsync(CancellationToken ct)
    {
        return Task.Run<IReadOnlyList<DeviceSnapshot>>(() =>
        {
            var result = new List<DeviceSnapshot>();
            List<HidInterfaceInfo> interfaces;
            try
            {
                interfaces = HidEnumerator.Enumerate();
            }
            catch (Exception ex)
            {
                AppServices.Log.Error("HID 枚举失败", ex);
                return result;
            }

            foreach (HidInterfaceInfo info in interfaces)
            {
                ct.ThrowIfCancellationRequested();
                DeviceSnapshot? snapshot = Describe(info);
                if (snapshot is not null)
                {
                    result.Add(snapshot);
                }
            }

            return result;
        }, ct);
    }

    private static DeviceSnapshot? Describe(HidInterfaceInfo info)
    {
        if (!info.IsTargetDevice)
        {
            // 非目标族的 HID 设备不归本 provider 管；VID_001F(Sunplus) 等由通用降级路径处理。
            return null;
        }

        BatteryAvailability availability = BatteryAvailability.ConnectionOnly;
        BatteryReading? reading = null;

        // 通用 HID Battery 探测：只有存在 Feature 报文才可能读到 Battery Level 字段。
        if (info.FeatureReportByteLength > 0 && info.Readable)
        {
            if (TryReadFeatureBatteryLevel(info, out int percent))
            {
                reading = new BatteryReading(percent, DateTimeOffset.Now);
                availability = BatteryAvailability.Available;
            }
        }

        return new DeviceSnapshot
        {
            StableId = info.StableId,
            // 同一接收器的多个顶层集合会共享同一 ProductString，必须带上接口/用途后缀才能区分，
            // 否则列表里会出现多条同名且无法分别改名的条目。
            SystemName = BuildSystemName(info),
            Kind = InferKind(info),
            Transport = DeviceTransport.Hid24Ghz,
            Connection = ConnectionStatus.Connected,
            Battery = reading,
            IsCharging = false,
            Availability = availability,
        };
    }

    private static string BuildSystemName(HidInterfaceInfo info)
    {
        string baseName = string.IsNullOrWhiteSpace(info.ProductString)
            ? "2.4G 接收器"
            : info.ProductString;
        string suffix = info.UsagePage switch
        {
            0x01 when info.Usage == 0x02 => "鼠标",
            0x01 when info.Usage == 0x06 => "键盘",
            0x0C => "消费类控制",
            0x01 when info.Usage == 0x00 && info.InterfaceNumber == 0x02 => "厂商通道",
            _ => info.InterfaceSummary,
        };

        return baseName + " · " + suffix;
    }

    private static DeviceKind InferKind(HidInterfaceInfo info) => info.UsagePage switch
    {
        0x01 when info.Usage == 0x02 => DeviceKind.Mouse,
        0x01 when info.Usage == 0x06 => DeviceKind.Keyboard,
        _ => DeviceKind.Other,
    };

    /// <summary>
    /// 只读探测 Feature 报文里的 Battery Level（Usage Page 0x06, Usage 0x20）。
    /// 用 HidD_GetFeature（设备到主机方向）逐 report id 试读；任何失败都视为「设备不暴露」而非异常。
    /// </summary>
    private static bool TryReadFeatureBatteryLevel(HidInterfaceInfo info, out int percent)
    {
        percent = 0;
        if (info.FeatureReportByteLength <= 0)
        {
            return false;
        }

        using SafeFileHandle handle = HidNative.CreateFileW(
            info.InterfacePath,
            HidNative.GenericRead,
            HidNative.FileShareRead | HidNative.FileShareWrite,
            IntPtr.Zero,
            HidNative.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        for (byte reportId = 0; reportId < 4; reportId++)
        {
            var buffer = new byte[Math.Max(info.FeatureReportByteLength, 2)];
            buffer[0] = reportId;
            if (!HidNative.HidD_GetFeature(handle, buffer, buffer.Length))
            {
                continue;
            }

            // 只认「第 2 字节恰好在 0..100」这一种保守形态，其余一律不猜。
            int candidate = buffer[1];
            if (candidate is >= 0 and <= 100)
            {
                percent = candidate;
                return true;
            }
        }

        return false;
    }

    public Task<BatteryReading?> ReadBatteryAsync(string stableId, CancellationToken ct)
    {
        return Task.Run<BatteryReading?>(() =>
        {
            HidInterfaceInfo? info = HidEnumerator.Enumerate()
                .FirstOrDefault(i => i.IsTargetDevice && i.StableId == stableId);
            if (info is null || info.FeatureReportByteLength <= 0 || !info.Readable)
            {
                return null;
            }

            return TryReadFeatureBatteryLevel(info, out int percent)
                ? new BatteryReading(percent, DateTimeOffset.Now)
                : null;
        }, ct);
    }

    public Task<bool?> ReadChargingAsync(string stableId, CancellationToken ct) =>
        Task.FromResult<bool?>(null);

    public BatteryAvailability CapabilitiesFor(string stableId) => BatteryAvailability.ConnectionOnly;
}

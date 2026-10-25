using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace WirelessBatteryTray.Core;

/// <summary>
/// 蓝牙侧：同时枚举「经典蓝牙」与「纯 BLE」两类已配对设备（含离线），
/// 对已连接的尝试读 GATT Battery Service 0x180F / Battery Level 0x2A19。
/// 读不到一律降级为「仅连接状态」，绝不返回 0%。所有 WinRT 调用都带超时。
/// </summary>
public sealed class BluetoothProvider : IDeviceProvider
{
    private static readonly TimeSpan PerDeviceTimeout = TimeSpan.FromSeconds(4);

    private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    public string ProviderId => "bluetooth";

    public DeviceTransport Transport => DeviceTransport.BluetoothLE;

    public async Task<IReadOnlyList<DeviceSnapshot>> ProbeAsync(CancellationToken ct)
    {
        var result = new List<DeviceSnapshot>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAddresses = new HashSet<ulong>();

        // 关键：BluetoothDevice.GetDeviceSelector() 只覆盖经典蓝牙，纯 BLE 设备（如切换为
        // 纯 BLE 外设）不在其中，必须再用 BluetoothLEDevice 的选择器枚举一次。
        foreach (DeviceSelectorKind kind in new[] { DeviceSelectorKind.Classic, DeviceSelectorKind.LowEnergy })
        {
            ct.ThrowIfCancellationRequested();
            DeviceInformationCollection infos;
            try
            {
                string selector = kind == DeviceSelectorKind.Classic
                    ? BluetoothDevice.GetDeviceSelector()
                    : BluetoothLEDevice.GetDeviceSelector();
                infos = await DeviceInformation.FindAllAsync(selector).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppServices.Log.Error("蓝牙枚举失败（" + kind + "）", ex);
                continue;
            }

            foreach (DeviceInformation info in infos)
            {
                ct.ThrowIfCancellationRequested();
                if (!seenIds.Add(info.Id))
                {
                    continue;
                }

                DeviceSnapshot? snapshot = await DescribeAsync(info, kind, ct).ConfigureAwait(false);
                if (snapshot is null)
                {
                    continue;
                }

                // 同一台设备可能同时以经典与 BLE 出现，按蓝牙地址去重。
                if (snapshot.StableId.StartsWith("bt:", StringComparison.Ordinal)
                    && ulong.TryParse(snapshot.StableId.Substring(3), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out ulong address)
                    && address != 0
                    && !seenAddresses.Add(address))
                {
                    continue;
                }

                result.Add(snapshot);
            }
        }

        return result;
    }

    private enum DeviceSelectorKind
    {
        Classic,
        LowEnergy,
    }

    private static async Task<DeviceSnapshot?> DescribeAsync(
        DeviceInformation info,
        DeviceSelectorKind kind,
        CancellationToken ct)
    {
        if (kind == DeviceSelectorKind.LowEnergy)
        {
            BluetoothLEDevice? leDevice = null;
            try
            {
                leDevice = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppServices.Log.Info("BLE 设备暂不可打开（跳过）：" + info.Name + " :: " + ex.GetType().Name
                    + " HResult=" + ex.HResult.ToString("x8"));
            }

            if (leDevice is null)
            {
                return Offline(info, DeviceTransport.BluetoothLE);
            }

            using (leDevice)
            {
                bool connected = leDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
                string stableId = leDevice.BluetoothAddress != 0
                    ? "bt:" + leDevice.BluetoothAddress.ToString("x12")
                    : StableIdOf(info);

                BatteryReading? reading = null;
                if (connected)
                {
                    reading = await ReadBatteryFromLeDeviceAsync(leDevice, ct).ConfigureAwait(false);
                }

                return new DeviceSnapshot
                {
                    StableId = stableId,
                    SystemName = string.IsNullOrWhiteSpace(leDevice.Name) ? info.Name : leDevice.Name,
                    Kind = InferKindByText(leDevice.Name ?? info.Name),
                    Transport = DeviceTransport.BluetoothLE,
                    Connection = connected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                    Battery = reading,
                    IsCharging = false,
                    Availability = reading is null ? BatteryAvailability.ConnectionOnly : BatteryAvailability.Available,
                };
            }
        }

        BluetoothDevice? device = null;
        try
        {
            device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 常见于「已配对但当前不可达」，属正常降级，不应每轮刷屏。
            AppServices.Log.Info("蓝牙设备暂不可打开（跳过）：" + info.Name + " :: " + ex.GetType().Name
                + " HResult=" + ex.HResult.ToString("x8"));
        }

        if (device is null)
        {
            return Offline(info, DeviceTransport.BluetoothClassic);
        }

        using (device)
        {
            bool connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            string stableId = device.BluetoothAddress != 0
                ? "bt:" + device.BluetoothAddress.ToString("x12")
                : StableIdOf(info);

            BatteryReading? reading = null;
            if (connected && device.BluetoothAddress != 0)
            {
                reading = await TryReadBatteryByAddressAsync(device.BluetoothAddress, ct).ConfigureAwait(false);
            }

            return new DeviceSnapshot
            {
                StableId = stableId,
                SystemName = string.IsNullOrWhiteSpace(device.Name) ? info.Name : device.Name,
                Kind = InferKindByText(device.Name ?? info.Name),
                Transport = DeviceTransport.BluetoothClassic,
                Connection = connected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
                Battery = reading,
                IsCharging = false,
                Availability = reading is null ? BatteryAvailability.ConnectionOnly : BatteryAvailability.Available,
            };
        }
    }

    /// <summary>已配对但当前不可达：仍然显示（需求 §3），电量走「仅连接状态」。</summary>
    private static DeviceSnapshot Offline(DeviceInformation info, DeviceTransport transport) => new()
    {
        StableId = StableIdOf(info),
        SystemName = string.IsNullOrWhiteSpace(info.Name) ? "(未命名蓝牙设备)" : info.Name,
        Kind = InferKindByText(info.Name),
        Transport = transport,
        Connection = ConnectionStatus.Disconnected,
        Battery = null,
        IsCharging = false,
        Availability = BatteryAvailability.ConnectionOnly,
    };

    private static string StableIdOf(DeviceInformation info)
    {
        if (info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object? address)
            && address is string text
            && ulong.TryParse(text.Replace(":", string.Empty), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ulong value)
            && value != 0)
        {
            return "bt:" + value.ToString("x12");
        }

        return "bt:" + info.Id;
    }

    private static DeviceKind InferKindByText(string? rawName)
    {
        string name = (rawName ?? string.Empty).ToLowerInvariant();
        if (name.Contains("mouse") || name.Contains("鼠标") || name.Contains("in9"))
        {
            return DeviceKind.Mouse;
        }

        if (name.Contains("keyboard") || name.Contains("键盘"))
        {
            return DeviceKind.Keyboard;
        }

        if (name.Contains("head") || name.Contains("buds") || name.Contains("耳机") || name.Contains("audio")
            || name.Contains("xiberia") || name.Contains("k03"))
        {
            return DeviceKind.Headset;
        }

        return DeviceKind.Other;
    }

    private static async Task<BatteryReading?> TryReadBatteryByAddressAsync(ulong address, CancellationToken ct)
    {
        BluetoothLEDevice? leDevice = null;
        try
        {
            leDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false);
            if (leDevice is null)
            {
                return null;
            }

            return await ReadBatteryFromLeDeviceAsync(leDevice, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("读取蓝牙电量失败（地址 " + address.ToString("x12") + "）：" + ex.Message);
            return null;
        }
        finally
        {
            leDevice?.Dispose();
        }
    }

    private static async Task<BatteryReading?> ReadBatteryFromLeDeviceAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerDeviceTimeout);
        try
        {
            var servicesResult = await device.GetGattServicesForUuidAsync(BatteryServiceUuid).AsTask(timeout.Token)
                .ConfigureAwait(false);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                AppServices.Log.Info("电量服务不可用（" + device.Name + "）：" + servicesResult.Status);
                return null;
            }

            foreach (GattDeviceService service in servicesResult.Services)
            {
                using (service)
                {
                    var charsResult = await service.GetCharacteristicsForUuidAsync(BatteryLevelUuid)
                        .AsTask(timeout.Token).ConfigureAwait(false);
                    if (charsResult.Status != GattCommunicationStatus.Success)
                    {
                        continue;
                    }

                    foreach (GattCharacteristic characteristic in charsResult.Characteristics)
                    {
                        var read = await characteristic.ReadValueAsync().AsTask(timeout.Token).ConfigureAwait(false);
                        if (read.Status != GattCommunicationStatus.Success || read.Value.Length < 1)
                        {
                            AppServices.Log.Info("电量特征读取失败（" + device.Name + "）：" + read.Status);
                            continue;
                        }

                        var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
                        byte level = reader.ReadByte();
                        if (level <= 100)
                        {
                            AppServices.Log.Info("蓝牙电量读取成功：" + device.Name + " = " + level + "%");
                            return new BatteryReading(level, DateTimeOffset.Now);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppServices.Log.Info("读取蓝牙电量超时（" + device.Name + "），降级为仅连接状态。");
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("读取蓝牙电量异常（" + device.Name + "）：" + ex.Message);
        }

        return null;
    }

    public async Task<BatteryReading?> ReadBatteryAsync(string stableId, CancellationToken ct)
    {
        if (!stableId.StartsWith("bt:", StringComparison.Ordinal))
        {
            return null;
        }

        if (ulong.TryParse(stableId.Substring(3), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ulong address) && address != 0)
        {
            return await TryReadBatteryByAddressAsync(address, ct).ConfigureAwait(false);
        }

        return null;
    }

    public Task<bool?> ReadChargingAsync(string stableId, CancellationToken ct) =>
        Task.FromResult<bool?>(null);

    public BatteryAvailability CapabilitiesFor(string stableId) => BatteryAvailability.ConnectionOnly;
}

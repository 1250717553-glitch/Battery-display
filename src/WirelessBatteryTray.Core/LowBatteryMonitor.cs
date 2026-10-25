using System;
using System.Collections.Generic;
using System.Linq;

namespace WirelessBatteryTray.Core;

/// <summary>一次报警事件的描述。</summary>
public sealed record LowBatteryAlert
{
    public required string StableId { get; init; }

    public required string DisplayName { get; init; }

    public required int Percent { get; init; }
}

/// <summary>
/// 低电量报警判定（滞回防抖）。纯逻辑、无副作用，便于测试与复用。
///
/// 规则：
///  1. 设备必须在线（Connected）且电量为真实读数（Available）。
///  2. 电量 ≤ LowBatteryThreshold 时触发一次。
///  3. 触发后进入「已报警」状态，后续刷新不再重复触发。
///  4. 只有电量回升到 ≥ LowBatteryResetThreshold（滞回），
///     或设备断开后重新连接，才清除「已报警」状态。
///  5. 充电中不报警。
/// </summary>
public sealed class LowBatteryMonitor
{
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lastConnected = new(StringComparer.Ordinal);

    /// <summary>
    /// 根据最新快照计算本轮需要报警的设备。
    /// 返回的列表每次最多包含「本轮新跌破阈值」的设备。
    /// </summary>
    public IReadOnlyList<LowBatteryAlert> Evaluate(
        IReadOnlyList<DeviceSnapshot> snapshot,
        AppConfig config,
        Func<DeviceSnapshot, string> nameSelector)
    {
        var fired = new List<LowBatteryAlert>();

        int low = Math.Clamp(config.LowBatteryThreshold, 1, 100);
        int reset = Math.Clamp(config.LowBatteryResetThreshold, low + 1, 101);

        foreach (DeviceSnapshot device in snapshot)
        {
            bool connected = device.Connection == ConnectionStatus.Connected;

            // 断开 -> 记录；重新连接时清掉「已报警」，让下次跌破能再提醒一次。
            bool wasConnected = _lastConnected.Contains(device.StableId);
            if (connected && !wasConnected)
            {
                _alerted.Remove(device.StableId);
            }

            if (connected)
            {
                _lastConnected.Add(device.StableId);
            }
            else
            {
                _lastConnected.Remove(device.StableId);
            }

            // 只有真实读数才参与预警；读不到电量不报警（需求 §5）。
            if (!connected
                || device.Availability != BatteryAvailability.Available
                || device.Battery is null
                || device.IsCharging)
            {
                continue;
            }

            int percent = device.Battery.Percent;

            // 滞回：回升到 reset 以上才解除已报警状态。
            if (percent >= reset)
            {
                _alerted.Remove(device.StableId);
                continue;
            }

            if (percent > low)
            {
                continue;
            }

            if (_alerted.Contains(device.StableId))
            {
                continue;
            }

            _alerted.Add(device.StableId);
            fired.Add(new LowBatteryAlert
            {
                StableId = device.StableId,
                DisplayName = nameSelector(device),
                Percent = percent,
            });
        }

        return fired;
    }

    /// <summary>某设备当前是否处于「已报警且仍低电量」状态（供 UI 显示红色）。</summary>
    public bool IsAlerted(string stableId) => _alerted.Contains(stableId);

    public IReadOnlyCollection<string> AlertedIds => _alerted;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WirelessBatteryTray.Core;

/// <summary>桥接文件中单个设备条目（字段名刻意简短，前端解析方便）。</summary>
public sealed class WhaleDeviceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>设备类型：mouse / keyboard / headset / other。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "other";

    /// <summary>电量百分比；读不到时为 null（绝不写 0）。</summary>
    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    /// <summary>是否在线。</summary>
    [JsonPropertyName("online")]
    public bool Online { get; set; }

    /// <summary>是否处于低电量报警状态（前端据此变红）。</summary>
    [JsonPropertyName("alerted")]
    public bool Alerted { get; set; }
}

/// <summary>桥接文件整体载荷。</summary>
public sealed class WhaleBridgePayload
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    /// <summary>写入时间（ISO8601），供前端判断数据新鲜度。</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>报警阈值，前端可据此自行判断。</summary>
    [JsonPropertyName("lowThreshold")]
    public int LowThreshold { get; set; }

    /// <summary>在线设备（前端电量条只显示这些）。</summary>
    [JsonPropertyName("devices")]
    public List<WhaleDeviceDto> Devices { get; set; } = new();

    /// <summary>离线但有上次已知电量的设备，供悬停浮窗显示。</summary>
    [JsonPropertyName("offline")]
    public List<WhaleDeviceDto> Offline { get; set; } = new();
}

/// <summary>
/// 鲸鱼娘桥接：把设备快照写到本地 JSON，供 DSH 鲸鱼娘插件的 /dsh-whale/battery.json 路由读取。
///
/// 为什么用文件而不是 socket：
///  鲸鱼娘挂件跑在浏览器里，跨源 fetch 会受 CORS 与私有网络访问策略限制；
///  而插件本身已有「宿主写文件 → 宿主路由 → 前端轮询」这条成熟通路（last-turn.json），
///  复用它零新增依赖、零端口、零防火墙规则。
///
/// 稳定性：写入采用「临时文件 + 原子替换」，前端永远不会读到半个 JSON；
///  本进程退出后文件保留最后一次内容，前端据此显示「托盘未运行」而不是崩溃。
/// </summary>
public static class WhaleBridge
{
    private static readonly object Gate = new();

    // 中文不转义成 \uXXXX：这个文件主要给人看/排查，可读性比体积重要。
    // 前端 JSON.parse 对两种形式都能正确解析。
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FilePath => Path.Combine(AppPaths.Root, "whale-bridge.json");

    /// <summary>把当前快照写入桥接文件。任何异常都只记日志，绝不影响主程序。</summary>
    public static void Write(
        IReadOnlyList<DeviceSnapshot> snapshot,
        AppConfig config,
        Func<string, bool> isAlerted,
        Func<DeviceSnapshot, string> nameSelector)
    {
        try
        {
            var payload = BuildPayload(snapshot, config, isAlerted, nameSelector);
            string json = JsonSerializer.Serialize(payload, Options);

            lock (Gate)
            {
                AppPaths.EnsureCreated();

                string path = FilePath;
                string tmp = path + ".tmp";

                // 原子替换：先写临时文件再 Move，避免前端读到半截 JSON。
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("写入鲸鱼娘桥接文件失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 进程退出时标记托盘已离线。前端据此显示「托盘未运行」，
    /// 而不是把最后一次数据当成实时的继续展示。
    /// </summary>
    public static void MarkOffline()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                return;
            }

            // 只把文件里的 ok 改为 false，其余内容（最后一次电量）保留，
            // 这样前端仍能显示「托盘未运行 · 最后已知 58%」。
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var payload = new WhaleBridgePayload
            {
                Ok = false,
                UpdatedAt = DateTimeOffset.Now,
                LowThreshold = root.TryGetProperty("lowThreshold", out var lt) ? lt.GetInt32() : 20,
            };

            if (root.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement d in devs.EnumerateArray())
                {
                    payload.Devices.Add(ReadDevice(d));
                }
            }

            if (root.TryGetProperty("offline", out var off) && off.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement d in off.EnumerateArray())
                {
                    payload.Offline.Add(ReadDevice(d));
                }
            }

            lock (Gate)
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, Options));
                File.Move(tmp, path, overwrite: true);
            }

            AppServices.Log.Info("鲸鱼娘桥接已标记托盘离线。");
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("标记鲸鱼娘桥接离线失败：" + ex.Message);
        }
    }

    private static WhaleDeviceDto ReadDevice(JsonElement e) => new()
    {
        Id = e.TryGetProperty("id", out var v1) ? v1.GetString() ?? string.Empty : string.Empty,
        Name = e.TryGetProperty("name", out var v2) ? v2.GetString() ?? string.Empty : string.Empty,
        Kind = e.TryGetProperty("kind", out var v3) ? v3.GetString() ?? "other" : "other",
        Percent = e.TryGetProperty("percent", out var v4) && v4.ValueKind == JsonValueKind.Number
            ? v4.GetInt32()
            : null,
        Online = e.TryGetProperty("online", out var v5) && v5.ValueKind == JsonValueKind.True,
        Alerted = e.TryGetProperty("alerted", out var v6) && v6.ValueKind == JsonValueKind.True,
    };

    private static WhaleBridgePayload BuildPayload(
        IReadOnlyList<DeviceSnapshot> snapshot,
        AppConfig config,
        Func<string, bool> isAlerted,
        Func<DeviceSnapshot, string> nameSelector)
    {
        var online = new List<WhaleDeviceDto>();
        var offline = new List<WhaleDeviceDto>();

        foreach (DeviceSnapshot device in snapshot)
        {
            bool connected = device.Connection == ConnectionStatus.Connected;

            // 只有真实读数才带 percent；读不到写 null，前端显示「--」而不是 0。
            int? percent = device.Availability == BatteryAvailability.Available
                           && device.Battery is not null
                ? device.Battery.Percent
                : null;

            var dto = new WhaleDeviceDto
            {
                Id = device.StableId,
                Name = nameSelector(device),
                Kind = KindKey(device.Kind),
                Percent = percent,
                Online = connected,
                Alerted = isAlerted(device.StableId),
            };

            if (connected)
            {
                online.Add(dto);
            }
            else if (percent is not null)
            {
                // 离线但有上次已知电量的也带上，供悬停浮窗展示。
                offline.Add(dto);
            }
        }

        // 在线按电量升序（最需要关注的排前面），未知电量排末尾。
        online.Sort((a, b) => (a.Percent ?? int.MaxValue).CompareTo(b.Percent ?? int.MaxValue));

        return new WhaleBridgePayload
        {
            Ok = true,
            UpdatedAt = DateTimeOffset.Now,
            LowThreshold = config.LowBatteryThreshold,
            Devices = online,
            Offline = offline.Take(5).ToList(),
        };
    }

    private static string KindKey(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "mouse",
        DeviceKind.Keyboard => "keyboard",
        DeviceKind.Headset => "headset",
        _ => "other",
    };
}

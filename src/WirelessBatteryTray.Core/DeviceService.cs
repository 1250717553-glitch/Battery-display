using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessBatteryTray.Core;

/// <summary>
/// 正式设备服务：聚合各 provider，按 StableId 去重，落实 D4 刷新策略
/// （平时只回缓存；显式 refresh 才真读；低电量时由 UI 触发单设备复核）。
/// 由 ServiceBootstrap 通过公共无参构造自动装配。
/// </summary>
public sealed class DeviceService : IDeviceService, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, DeviceSnapshot> _cache = new();
    private readonly List<IDeviceProvider> _providers;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private AppConfig _config = new();
    private bool _disposed;
    private DateTimeOffset _lastFullRefresh = DateTimeOffset.MinValue;

    public DeviceService()
        : this(new IDeviceProvider[]
        {
            new Hid24GhzProvider(),
            new BluetoothProvider(),
        })
    {
    }

    internal DeviceService(IEnumerable<IDeviceProvider> providers)
    {
        _providers = providers.ToList();
    }

    public event EventHandler? DevicesChanged;

    public void Start()
    {
        _config = AppServices.Config.Load();
        SeedCacheFromPersisted();
        AppServices.Log.Info("DeviceService.Start providers=" + _providers.Count
            + " 已恢复上次已知电量=" + _cache.Count);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>
    /// 从配置恢复「上次已知电量」到缓存。设备关机 + 程序重启后，
    /// 列表仍应显示「上次已知 N%」，而不是变成「离线，电量未知」。
    /// </summary>
    private void SeedCacheFromPersisted()
    {
        if (_config.LastKnownBatteries.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (KeyValuePair<string, PersistedBattery> pair in _config.LastKnownBatteries)
            {
                if (pair.Value.Percent is < 0 or > 100)
                {
                    continue;
                }

                // 只做「读数」占位；真实连接状态由 provider 在首轮探测时覆盖。
                _cache[pair.Key] = new DeviceSnapshot
                {
                    StableId = pair.Key,
                    SystemName = pair.Key,
                    Kind = DeviceKind.Other,
                    Transport = DeviceTransport.BluetoothLE,
                    Connection = ConnectionStatus.Disconnected,
                    Battery = new BatteryReading(pair.Value.Percent, pair.Value.ObservedAt),
                    IsCharging = false,
                    Availability = BatteryAvailability.ConnectionOnly,
                };
            }
        }
    }

    public void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 请求尽快重算一轮。配置变更（如调整报警阈值）后调用，
    /// 让托盘立即按新阈值重新评估，而不是等满一个刷新间隔。
    /// </summary>
    public void RequestRefresh()
    {
        if (_disposed)
        {
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await RefreshNowAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppServices.Log.Warn("立即刷新失败：" + ex.Message);
            }
        });
    }

    private async Task RefreshNowAsync(CancellationToken ct)
    {
        await GetSnapshotAsync(refreshBatteries: true, ct).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // 首次立即刷新一次，让托盘尽快有数据。
        await SafeRefreshAsync(readBatteries: true, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            int seconds = Math.Clamp(ReloadConfig().RefreshIntervalSeconds, 15, 3600);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 定时轮询只读缓存 + 仅对已连接的 HID 做轻量探测，蓝牙不在后台主动建连（D4）。
            await SafeRefreshAsync(readBatteries: false, ct).ConfigureAwait(false);
        }
    }

    private AppConfig ReloadConfig()
    {
        try
        {
            _config = AppServices.Config.Load();
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("重新加载配置失败，沿用上次：" + ex.Message);
        }

        return _config;
    }

    private async Task SafeRefreshAsync(bool readBatteries, CancellationToken ct)
    {
        try
        {
            await GetSnapshotAsync(readBatteries, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("刷新设备快照失败", ex);
        }
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> GetSnapshotAsync(
        bool refreshBatteries,
        CancellationToken ct = default)
    {
        if (!refreshBatteries)
        {
            lock (_gate)
            {
                return _cache.Values.ToArray();
            }
        }

        // 并发刷新（托盘定时器 + UI 打开窗口 + DevicesChanged）会重复枚举蓝牙并重复建 GATT 连接，
        // 这里串行化，后到者直接复用现有缓存而不是再跑一轮昂贵的探测。
        if (!await _refreshLock.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
        {
            lock (_gate)
            {
                return _cache.Values.ToArray();
            }
        }

        try
        {
            // 合并「同时发生」的重复请求：托盘启动、窗口打开、DevicesChanged 往往在几十毫秒内
            // 一起要求真读，若每次都全量探测会重复枚举蓝牙并重复建 GATT 连接。
            // 窗口必须很短：用户关掉鼠标再点刷新时，必须立刻真实探测到「已断连」，
            // 否则会长时间显示过期的「在线 69%」。
            DateTimeOffset now = DateTimeOffset.Now;
            lock (_gate)
            {
                if (now - _lastFullRefresh < TimeSpan.FromMilliseconds(400) && _cache.Count > 0)
                {
                    return _cache.Values.ToArray();
                }
            }

            var snapshot = await RefreshCoreAsync(ct).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<DeviceSnapshot>> RefreshCoreAsync(CancellationToken ct)
    {
        var merged = new Dictionary<string, DeviceSnapshot>(StringComparer.Ordinal);

        foreach (IDeviceProvider provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<DeviceSnapshot> batch;
            try
            {
                batch = await provider.ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个 provider 失败不得拖垮整轮刷新。
                AppServices.Log.Error("provider 探测失败：" + provider.ProviderId, ex);
                continue;
            }

            foreach (DeviceSnapshot snapshot in batch)
            {
                if (merged.TryGetValue(snapshot.StableId, out DeviceSnapshot? existing))
                {
                    merged[snapshot.StableId] = PreferBetter(existing, snapshot);
                    continue;
                }

                // 本轮没读到电量、但历史缓存里有：保留上次读数，供 UI 显示「上次已知 N%」。
                lock (_gate)
                {
                    if (_cache.TryGetValue(snapshot.StableId, out DeviceSnapshot? cached)
                        && cached.Battery is not null
                        && snapshot.Battery is null)
                    {
                        merged[snapshot.StableId] = snapshot with { Battery = cached.Battery };
                        continue;
                    }
                }

                merged[snapshot.StableId] = snapshot;
            }
        }

        lock (_gate)
        {
            // 保留本轮没出现、但曾经见过的设备（离线也要显示上次已知电量）。
            foreach (KeyValuePair<string, DeviceSnapshot> pair in _cache)
            {
                if (!merged.ContainsKey(pair.Key))
                {
                    merged[pair.Key] = pair.Value with
                    {
                        Connection = ConnectionStatus.Disconnected,
                        IsCharging = false,
                    };
                }
            }

            _cache.Clear();
            foreach (KeyValuePair<string, DeviceSnapshot> pair in merged)
            {
                _cache[pair.Key] = pair.Value;
            }

            var snapshotForPersist = _cache.Values.ToArray();

            // 时间戳必须在「探测完成」处落，且只有显式刷新路径才读它；
            // 放错位置会让后续显式刷新被误判为重复而直接返回旧缓存。
            _lastFullRefresh = DateTimeOffset.Now;
            PersistBatteries(snapshotForPersist);
            var result = _cache.Values.ToArray();
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }
    }

    /// <summary>
    /// 把本轮真实读到的电量写回配置文件。只改 LastKnownBatteries，不动别名等用户设置；
    /// 写盘失败绝不能影响刷新（例如磁盘满/权限问题）。
    /// </summary>
    private void PersistBatteries(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        try
        {
            bool dirty = false;
            foreach (DeviceSnapshot snapshot in snapshots)
            {
                if (snapshot.Battery is null)
                {
                    continue;
                }

                _config.LastKnownBatteries[snapshot.StableId] = new PersistedBattery
                {
                    Percent = snapshot.Battery.Percent,
                    ObservedAt = snapshot.Battery.ObservedAt,
                };
                dirty = true;
            }

            if (dirty)
            {
                AppServices.Config.Save(_config);
            }
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("持久化上次已知电量失败：" + ex.Message);
        }
    }

    /// <summary>同 StableId 出现多次时，优先保留真的读到了电量的那条。</summary>
    private static DeviceSnapshot PreferBetter(DeviceSnapshot a, DeviceSnapshot b)
    {
        bool aHas = a.Availability == BatteryAvailability.Available && a.Battery is not null;
        bool bHas = b.Availability == BatteryAvailability.Available && b.Battery is not null;
        if (bHas && !aHas)
        {
            return b;
        }

        return a;
    }

    public async Task<bool?> RefreshBatteryAsync(string stableId, CancellationToken ct = default)
    {
        foreach (IDeviceProvider provider in _providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                BatteryReading? reading = await provider.ReadBatteryAsync(stableId, ct).ConfigureAwait(false);
                if (reading is null)
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_cache.TryGetValue(stableId, out DeviceSnapshot? existing))
                    {
                        _cache[stableId] = existing with
                        {
                            Battery = reading,
                            Availability = BatteryAvailability.Available,
                        };
                        DevicesChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppServices.Log.Warn("单设备复核失败：" + stableId + " :: " + ex.Message);
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts.Dispose();
    }
}

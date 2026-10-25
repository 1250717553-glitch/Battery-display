using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WirelessBatteryTray.Core;
using WinForms = System.Windows.Forms;

namespace WirelessBatteryTray.App;

/// <summary>
/// 托盘常驻：图标 = 最低电量设备的分档；tooltip = 关键 2-3 个设备；
/// 右键菜单 = 只读简报 + 设备列表 + 立即刷新 + 开机自启 + 退出。
/// 刷新只由低频 DispatcherTimer（配置的间隔）与 DevicesChanged 驱动，空闲时 CPU 接近 0。
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private const int MaxTooltipChars = 63;

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _autoStartItem;
    private readonly DispatcherTimer _timer;
    private readonly Action _openDeviceList;
    private readonly Action<PhysicalRect> _showPopup;
    /// <summary>电池图标缓存：key = "percent|alert|extra"，避免每次刷新重画位图。</summary>
    private readonly Dictionary<string, Icon> _iconCache = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LowBatteryMonitor _lowBattery = new();

    private AppConfig _config;
    private IReadOnlyList<DeviceSnapshot> _devices = Array.Empty<DeviceSnapshot>();
    private int _refreshing;
    private bool _disposed;

    public TrayIconController(AppConfig config, Action openDeviceList, Action<PhysicalRect> showPopup)
    {
        _config = config;
        _openDeviceList = openDeviceList;
        _showPopup = showPopup;

        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += (_, _) => BuildMenuItems();

        _autoStartItem = new WinForms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = false,
            Checked = AutoStart.IsEnabled(),
        };
        _autoStartItem.Click += (_, _) => ToggleAutoStart();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = GetBatteryIcon(null, false, 0),
            Text = "无线设备电量",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Left)
            {
                return;
            }

            // 左键：贴着图标上方弹出快捷面板。
            // 用鼠标当前物理坐标作为锚点（托盘图标本身没有独立窗口句柄可查）。
            var (x, y) = TaskbarHelper.GetCursorPosition();
            _showPopup(new PhysicalRect(x, y, x, y));
        };

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_config.RefreshIntervalSeconds, 15, 3600)),
        };
        _timer.Tick += async (_, _) => await RefreshAsync(false).ConfigureAwait(true);
        _timer.Start();
    }

    public IReadOnlyList<DeviceSnapshot> Devices => _devices;

    public event EventHandler? DevicesUpdated;

    public AppConfig Config => _config;

    public void ReloadConfig(AppConfig config) => _config = config;

    public void Start()
    {
        if (AppServices.Device is { } device)
        {
            device.DevicesChanged += OnDevicesChanged;
        }

        _ = RefreshAsync(true);
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        _ = RefreshAsync(false);

    /// <summary>
    /// 刷新一次快照并更新托盘显示。readBatteries=false 表示只读缓存。
    /// 本方法可能由后台线程触发（provider 的 DevicesChanged），因此快照取出后
    /// 必须显式编组回托盘所在的 Dispatcher 才能碰 NotifyIcon 与 DevicesUpdated 订阅者；
    /// 依赖 ConfigureAwait(true) 是不够的，它只会回到「当时的」上下文（可能是线程池）。
    /// </summary>
    public async Task RefreshAsync(bool readBatteries)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        var device = AppServices.Device;
        if (device is null)
        {
            Interlocked.Exchange(ref _refreshing, 0);
            return;
        }

        try
        {
            var snapshot = await device.GetSnapshotAsync(readBatteries, _cts.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => ApplySnapshot(snapshot)).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("刷新设备快照失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>本设备是否处于「已报警且仍低电量」状态，供列表/面板渲染红色。</summary>
    public bool IsLowBatteryAlerted(string stableId) => _lowBattery.IsAlerted(stableId);

    private void ApplySnapshot(IReadOnlyList<DeviceSnapshot> snapshot)
    {
        try
        {
            _devices = snapshot;
            RaiseLowBatteryAlerts(snapshot);
            UpdateTrayVisuals();

            // 每轮刷新后同步给鲸鱼娘（文件桥）。内部已全量吞掉异常，不影响托盘。
            WhaleBridge.Write(
                snapshot,
                _config,
                id => _lowBattery.IsAlerted(id),
                d => BatteryDisplay.DisplayName(d, _config));

            DevicesUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 订阅者（窗口）抛错不得冒泡到 Dispatcher 变成未观察异常。
            AppServices.Log.Error("应用设备快照失败", ex);
        }
    }

    /// <summary>
    /// 评估低电量并报警。每台设备在跌破阈值时只响一次，
    /// 回升到重置阈值或重新连接后才会再次响（滞回防打扰）。
    /// </summary>
    private void RaiseLowBatteryAlerts(IReadOnlyList<DeviceSnapshot> snapshot)
    {
        IReadOnlyList<LowBatteryAlert> fired;
        try
        {
            fired = _lowBattery.Evaluate(snapshot, _config,
                d => BatteryDisplay.DisplayName(d, _config));
        }
        catch (Exception ex)
        {
            AppServices.Log.Error("低电量评估失败", ex);
            return;
        }

        if (fired.Count == 0)
        {
            return;
        }

        foreach (LowBatteryAlert alert in fired)
        {
            AppServices.Log.Info("低电量报警：" + alert.DisplayName + " " + alert.Percent + "%");
        }

        // 提示音：多设备同轮触发也只响一次，避免连续刺耳。
        // 用户可在「报警设置」里关闭声音（关闭后仍会变红，只是不响）。
        if (!_config.LowBatterySoundEnabled)
        {
            return;
        }

        try
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("播放提示音失败：" + ex.Message);
        }
    }

    private void UpdateTrayVisuals()
    {
        var lowest = _devices
            .Where(BatteryDisplay.HasPercent)
            .OrderBy(d => d.Battery!.Percent)
            .FirstOrDefault();

        // 托盘只显示「最需要关注的一台」：报警设备优先，其次电量最低。
        var focus = PickFocusDevice();

        // 角标 = 除主设备外还有几台在线设备（0 表示不画角标）
        int onlineCount = _devices.Count(d => d.Connection == ConnectionStatus.Connected);
        int extra = Math.Max(0, onlineCount - (focus is null ? 0 : 1));

        bool alert = focus is not null && _lowBattery.IsAlerted(focus.StableId);
        int? percent = focus is not null && BatteryDisplay.HasPercent(focus)
            ? focus.Battery!.Percent
            : (int?)null;

        _notifyIcon.Icon = GetBatteryIcon(percent, alert, extra);

        bool anyAlert = _devices.Any(d =>
            d.Connection == ConnectionStatus.Connected && _lowBattery.IsAlerted(d.StableId));

        var text = BuildTooltip(lowest, anyAlert);
        _notifyIcon.Text = text.Length > MaxTooltipChars ? text.Substring(0, MaxTooltipChars - 1) + "…" : text;
    }

    private string BuildTooltip(DeviceSnapshot? lowest, bool anyAlert = false)
    {
        if (_devices.Count == 0)
        {
            return "无线设备电量 — 暂无设备";
        }

        var builder = new StringBuilder();
        if (anyAlert)
        {
            // 红点前缀：即便图标在小尺寸托盘里不显眼，tooltip 也能立刻看出异常。
            builder.Append("🔴 低电量  ");
        }

        if (lowest is not null)
        {
            builder.Append(BatteryDisplay.DisplayName(lowest, _config))
                .Append(' ')
                .Append(lowest.Battery!.Percent)
                .Append('%');
        }
        else
        {
            builder.Append(_devices.Count).Append(" 个设备（电量未知）");
        }

        foreach (var other in _devices.Where(d => !ReferenceEquals(d, lowest)).Take(2))
        {
            builder.Append(Environment.NewLine)
                .Append(BatteryDisplay.DisplayName(other, _config))
                .Append(' ')
                .Append(BatteryDisplay.PercentText(other));
        }

        return builder.ToString();
    }

    private void BuildMenuItems()
    {
        _menu.Items.Clear();

        if (_devices.Count == 0)
        {
            _menu.Items.Add(new WinForms.ToolStripMenuItem("（暂无设备）") { Enabled = false });
        }
        else
        {
            var now = DateTimeOffset.Now;
            var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _config.StaleAfterMinutes));
            foreach (var device in OrderForBrief(_devices))
            {
                var line = BatteryDisplay.DisplayName(device, _config)
                    + " — "
                    + BatteryDisplay.PercentText(device)
                    + " / "
                    + BatteryDisplay.StatusText(device)
                    + (device.IsStale(now, staleAfter) ? " / 过期" : string.Empty);
                _menu.Items.Add(new WinForms.ToolStripMenuItem(line) { Enabled = false });
            }
        }

        _menu.Items.Add(new WinForms.ToolStripSeparator());

        var listItem = new WinForms.ToolStripMenuItem("设备列表…");
        listItem.Click += (_, _) => _openDeviceList();
        _menu.Items.Add(listItem);

        var refreshItem = new WinForms.ToolStripMenuItem("立即刷新");
        refreshItem.Click += async (_, _) =>
        {
            AppServices.Log.Info("托盘菜单：立即刷新");
            await RefreshAsync(true).ConfigureAwait(true);
        };
        _menu.Items.Add(refreshItem);

        _autoStartItem.Checked = AutoStart.IsEnabled();
        _menu.Items.Add(_autoStartItem);

        _menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        _menu.Items.Add(exitItem);
    }

    private static IEnumerable<DeviceSnapshot> OrderForBrief(IReadOnlyList<DeviceSnapshot> devices) =>
        devices
            .OrderBy(d => BatteryDisplay.HasPercent(d) ? d.Battery!.Percent : int.MaxValue)
            .ThenBy(d => d.DisplayName, StringComparer.CurrentCulture);

    private void ToggleAutoStart()
    {
        var target = !AutoStart.IsEnabled();
        var ok = AutoStart.SetEnabled(target);
        _autoStartItem.Checked = AutoStart.IsEnabled();
        if (ok)
        {
            AppServices.Log.Info("开机自启已" + (target ? "启用" : "禁用"));
        }
        else
        {
            AppServices.Log.Warn("写注册表失败，开机自启保持：" + (AutoStart.IsEnabled() ? "启用" : "禁用"));
        }
    }

    /// <summary>
    /// 选出托盘要展示的那一台：报警设备优先，其次电量最低的在线设备。
    /// 返回 null 表示没有可展示的设备（图标画成无读数状态）。
    /// </summary>
    private DeviceSnapshot? PickFocusDevice()
    {
        var online = _devices
            .Where(d => d.Connection == ConnectionStatus.Connected)
            .ToList();

        // 1) 报警中最紧急的一台
        var alerted = online
            .Where(d => _lowBattery.IsAlerted(d.StableId))
            .OrderBy(d => d.Battery?.Percent ?? int.MaxValue)
            .FirstOrDefault();
        if (alerted is not null)
        {
            return alerted;
        }

        // 2) 否则电量最低且确实有读数的一台
        var lowest = online
            .Where(BatteryDisplay.HasPercent)
            .OrderBy(d => d.Battery!.Percent)
            .FirstOrDefault();

        // 3) 都没有读数时，退回任意在线设备（图标会画成无读数）
        return lowest ?? online.FirstOrDefault();
    }

    /// <summary>取（或生成）电池图标。extra 为角标数字，0 表示不画。</summary>
    private Icon GetBatteryIcon(int? percent, bool alert, int extra)
    {
        string key = (percent?.ToString() ?? "-") + "|" + (alert ? "a" : "n") + "|" + extra;
        if (_iconCache.TryGetValue(key, out Icon? cached))
        {
            return cached;
        }

        Icon icon = CreateBatteryIcon(percent, alert, extra);
        _iconCache[key] = icon;
        return icon;
    }

    /// <summary>
    /// 绘制 Win11 风格竖条电池图标（32×32）：
    ///  · 填充高度 = 电量百分比（不足时保底 4.5px，避免看起来像没电）
    ///  · 数字黑色 + 白色细描边，压在任意填充色上都清晰
    ///  · 字号自适应：3 位数缩小、1 位数放大
    ///  · 报警时边框/填充/数字全部转红
    ///  · extra &gt; 0 时右下角画红色角标（表示还有几台在线设备）
    /// </summary>
    private static Icon CreateBatteryIcon(int? percent, bool alert, int extra)
    {
        const int Size = 32;

        // 电池几何：几乎填满 32×32 画布，与系统托盘图标（如「中」）等大。
        // 旧值 BodyX=6/BodyW=18 会在四周留下大片空白，视觉上明显偏小。
        const float BodyX = 2.5f, BodyY = 5f, BodyW = 25f, BodyH = 25f;
        const float InnerX = 5f, InnerW = 20f;
        const float InnerBottom = 27.5f, InnerH = 20f;

        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            bool unknown = percent is null;
            int pct = percent ?? 0;

            // 颜色规则
            Color strokeColor = alert ? Color.FromArgb(229, 72, 77)
                : unknown ? Color.FromArgb(184, 184, 192)
                : Color.FromArgb(43, 43, 48);

            Color fillColor;
            if (unknown)
            {
                fillColor = Color.FromArgb(244, 244, 246);
            }
            else if (alert || pct <= 10)
            {
                fillColor = Color.FromArgb(229, 72, 77);
            }
            else if (pct <= 20)
            {
                fillColor = Color.FromArgb(245, 166, 35);
            }
            else
            {
                fillColor = Color.FromArgb(62, 207, 106);
            }

            // 正极凸点
            using (var capBrush = new SolidBrush(strokeColor))
            {
                g.FillRectangle(capBrush, 11f, 1.2f, 10f, 4f);
            }

            // 电池外壳
            using (var bodyBrush = new SolidBrush(unknown
                ? Color.FromArgb(244, 244, 246) : Color.White))
            using (var bodyPen = new Pen(strokeColor, 2.6f))
            {
                var bodyRect = new RectangleF(BodyX, BodyY, BodyW, BodyH);
                using var path = RoundedRect(bodyRect, 5f);
                g.FillPath(bodyBrush, path);
                g.DrawPath(bodyPen, path);
            }

            // 填充条：高度按比例，最低保底 4.5px
            if (!unknown && pct > 0)
            {
                float fillH = Math.Max(4.5f, InnerH * pct / 100f);
                fillH = Math.Min(fillH, InnerH);
                float fillY = InnerBottom - fillH;

                using var fillBrush = new SolidBrush(fillColor);
                using var fillPath = RoundedRect(
                    new RectangleF(InnerX, fillY, InnerW, fillH), 1.5f);
                g.FillPath(fillBrush, fillPath);
            }

            // 数字 / 问号
            string text = unknown ? "?" : pct.ToString();
            float fontSize = unknown ? 16f
                : text.Length >= 3 ? 12f
                : text.Length == 2 ? 15f
                : 17f;

            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

            // 用测量结果精确定位：DrawString 的矩形对齐会带行高 padding，
            // 直接用 SizeF 算左上角，才能让数字真正居中在电池内。
            SizeF textSize = g.MeasureString(text, font);
            float textX = (BodyX + BodyW / 2f) - textSize.Width / 2f;
            float textY = (BodyY + BodyH / 2f) - textSize.Height / 2f;

            if (unknown)
            {
                using var unknownBrush = new SolidBrush(strokeColor);
                g.DrawString(text, font, unknownBrush, textX, textY);
            }
            else
            {
                // 白色描边：8 方向偏移，保证压在深色填充上也清晰
                using var outlineBrush = new SolidBrush(Color.FromArgb(255, 255, 255));
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        g.DrawString(text, font, outlineBrush, textX + dx, textY + dy);
                    }
                }

                using var textBrush = new SolidBrush(Color.FromArgb(26, 26, 26));
                g.DrawString(text, font, textBrush, textX, textY);
            }

            // 角标：右下角红圆 + 数字
            if (extra > 0)
            {
                // 角标贴在电池右下角外侧：避开数字（居中）与填充条（底部居中）
                const float BadgeR = 5.5f;
                float bx = 26f, by = 27.5f;

                using var badgeBrush = new SolidBrush(Color.FromArgb(229, 72, 77));
                using var badgePen = new Pen(Color.White, 1.4f);
                g.FillEllipse(badgeBrush, bx - BadgeR, by - BadgeR, BadgeR * 2, BadgeR * 2);
                g.DrawEllipse(badgePen, bx - BadgeR, by - BadgeR, BadgeR * 2, BadgeR * 2);

                string badgeText = extra > 9 ? "9+" : extra.ToString();
                using var badgeFont = new Font("Segoe UI", extra > 9 ? 5.5f : 7.5f,
                    FontStyle.Bold, GraphicsUnit.Pixel);
                using var badgeWhite = new SolidBrush(Color.White);
                SizeF badgeSize = g.MeasureString(badgeText, badgeFont);
                g.DrawString(badgeText, badgeFont, badgeWhite,
                    bx - badgeSize.Width / 2f, by - badgeSize.Height / 2f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var fromHandle = Icon.FromHandle(handle);
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>构造圆角矩形路径。</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float d = radius * 2;

        if (d <= 0 || r.Width < d || r.Height < d)
        {
            path.AddRectangle(r);
            path.CloseFigure();
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _cts.Cancel();
        _cts.Dispose();

        if (AppServices.Device is { } device)
        {
            device.DevicesChanged -= OnDevicesChanged;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (var icon in _iconCache.Values)
        {
            icon.Dispose();
        }

        _iconCache.Clear();
    }
}

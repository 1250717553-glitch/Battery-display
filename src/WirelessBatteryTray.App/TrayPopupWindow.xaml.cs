using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WirelessBatteryTray.Core;

namespace WirelessBatteryTray.App;

/// <summary>
/// 托盘快捷面板：贴在托盘图标上方弹出，展示在线设备。
/// 无边框、透明、不抢焦点（WS_EX_NOACTIVATE），点击面板外自动隐藏。
/// </summary>
public partial class TrayPopupWindow : Window
{
    private readonly DispatcherTimer _outsideTimer;
    private Action? _openMain;

    /// <summary>查询「该设备是否处于低电量报警状态」，由 App 注入。</summary>
    private Func<string, bool>? _alertSource;

    public void SetAlertSource(Func<string, bool> source) => _alertSource = source;

    public TrayPopupWindow()
    {
        InitializeComponent();

        // 点击别处自动隐藏：用低频轮询判定鼠标是否离开面板。
        // 不用全局鼠标钩子（会被安全软件盯上，也可能干扰游戏）。
        _outsideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _outsideTimer.Tick += (_, _) =>
        {
            if (!IsMouseOverWindowOrRecent())
            {
                Hide();
            }
        };

        Deactivated += (_, _) => Hide();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        // 关键：不抢焦点的样式必须在显示前设置。
        WindowStyles.MakeNonActivatingToolWindow(handle);
    }

    /// <summary>
    /// 以托盘图标位置为锚点弹出面板。
    /// anchorX/anchorY 为物理像素（鼠标点击点）。
    /// </summary>
    public void ShowAtTray(PhysicalRect anchorScreen, Action openMain, bool keepOpenForCapture = false)
    {
        _openMain = openMain;

        var device = AppServices.Device;

        // 只读缓存会在「缓存尚未填充」时显示为空（实测：进程刚启动就弹面板会误报无设备）。
        // 因此缓存为空时退回一次真实探测；非空时仍只读缓存，避免每次弹面板都重建蓝牙连接。
        var snapshot = device?.GetSnapshotAsync(refreshBatteries: false).GetAwaiter().GetResult()
                       ?? Array.Empty<DeviceSnapshot>();

        if (snapshot.Count == 0 && device is not null)
        {
            AppServices.Log.Info("快捷面板：缓存为空，退回一次真实探测");
            snapshot = device.GetSnapshotAsync(refreshBatteries: true).GetAwaiter().GetResult();
        }

        Bind(snapshot);

        // 先测量，再定位：SizeToContent 下 ActualWidth 在 Show 之前不可靠，
        // 因此先 Show（透明、无焦点），再摆位置。
        Show();
        UpdateLayout();

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        int popupWidth = (int)Math.Ceiling(ActualWidth * scale);
        int popupHeight = (int)Math.Ceiling(ActualHeight * scale);

        PhysicalRect placed = TaskbarHelper.PlaceAbove(
            anchorScreen.Left + (anchorScreen.Width / 2),
            anchorScreen.Top,
            popupWidth,
            popupHeight,
            gap: 6);

        Left = placed.Left / scale;
        Top = placed.Top / scale;

        WindowStyles.AssertTopmost(new WindowInteropHelper(this).Handle);

        // 自动隐藏在 1.5 秒后才启动：给首次渲染与验收截图留出窗口，
        // 也避免「刚弹出就因鼠标在别处而立刻消失」的观感问题。
        _outsideTimer.Stop();
        var delay = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        if (keepOpenForCapture)
        {
            // 验收/截图用：不启动自动隐藏，面板保持可见。
            return;
        }

        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (IsVisible)
            {
                _outsideTimer.Start();
            }
        };
        delay.Start();

        AppServices.Log.Info("快捷面板已弹出：" + placed.Width + "x" + placed.Height
            + " at " + placed.Left + "," + placed.Top);
    }

    private void Bind(IReadOnlyList<DeviceSnapshot> snapshot)
    {
        var config = AppServices.Config.Load();
        var now = DateTimeOffset.Now;

        // 只显示「在线」的设备；离线的不进快捷面板。
        var rows = snapshot
            .Where(d => d.Connection == ConnectionStatus.Connected)
            .OrderBy(d => BatteryDisplay.HasPercent(d) ? d.Battery!.Percent : int.MaxValue)
            .Select(d =>
            {
                var row = new DeviceRow(d, config, now);
                row.IsLowBatteryAlerted = _alertSource?.Invoke(d.StableId) ?? false;
                return row;
            })
            .ToList();

        AppServices.Log.Info("快捷面板：在线设备 " + rows.Count + " / 共 " + snapshot.Count + " 条");
        TileHost.ItemsSource = rows;

        bool empty = rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = empty ? "当前无在线设备" : "在线设备 · " + rows.Count;
    }

    /// <summary>鼠标是否仍停留在本窗口（含少量容差，避免边缘抖动导致闪烁）。</summary>
    private bool IsMouseOverWindowOrRecent()
    {
        if (!IsVisible)
        {
            return false;
        }

        if (IsMouseOver)
        {
            return true;
        }

        // 用窗口矩形 + 8px 容差判断，比 IsMouseOver 更稳（相邻控件切换时会短暂为 false）。
        var (x, y) = TaskbarHelper.GetCursorPosition();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        int left = (int)(Left * scale) - 8;
        int top = (int)(Top * scale) - 8;
        int right = (int)((Left + ActualWidth) * scale) + 8;
        int bottom = (int)((Top + ActualHeight) * scale) + 8;

        return x >= left && x <= right && y >= top && y <= bottom;
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        Hide();
        _openMain?.Invoke();
    }

    private void OnOpenMainClick(object sender, RoutedEventArgs e)
    {
        Hide();
        _openMain?.Invoke();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 面板常驻复用，关闭只是隐藏。
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    public void StopAutoHide() => _outsideTimer.Stop();
}

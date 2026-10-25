using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WirelessBatteryTray.Core;


namespace WirelessBatteryTray.App;

/// <summary>
/// 应用启动：单实例 -> 主题 -> 装配设备服务 -> 托盘常驻。
/// 默认路径走 AppServices（正式装配），只有显式 --demo 才启用本地假数据。
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private TrayIconController? _tray;
    private DeviceListWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        AppServices.Log.Info("启动参数：" + string.Join(' ', e.Args));

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstance.MutexName, out var isFirst);
        if (!isFirst)
        {
            AppServices.Log.Info("已有实例在运行，激活后退出。");
            SingleInstance.SignalExisting();
            // 本实例并不持有该互斥体，绝不能走 OnExit 里的 ReleaseMutex（会抛
            // "Object synchronization method was called from an unsynchronized block"）。
            _instanceMutex.Dispose();
            _instanceMutex = null;
            // 关键：用 Environment.Exit(0) 而不是 Shutdown()。
            // 在 ShutdownMode=OnExplicitShutdown 下，Shutdown() 会走完整个 Application 的
            // 关闭流程（含 OnExit、Dispatcher 关闭），而本进程与首个实例共用同一个
            // 单实例命名对象；实测这会把正在运行的主实例一并带下去。
            // 这里本进程还没建立任何窗口/托盘，直接终结自身最干净。
            Environment.Exit(0);
            return;
        }

        ThemeManager.ApplyFromSystem();
        SystemEvents_Subscribe();

        var options = CommandLineOptions.Parse(e.Args);
        AppServices.Device = options.Demo
            ? new DemoDeviceService(options.DemoSerial, options.DemoReplug)
            : ServiceBootstrap.TryAttach(AppServices.Log);

        if (AppServices.Device is null)
        {
            AppServices.Log.Warn("未发现可用的 IDeviceService 实现，界面将以空列表运行（不伪造数据）。");
        }
        else
        {
            AppServices.Device.Start();
        }

        var config = AppServices.Config.Load();

        _tray = new TrayIconController(config, ShowDeviceList, ShowTrayPopup);
        _tray.Start();
        _tray.DevicesUpdated += (_, _) => _window?.UpdateConfig(_tray.Config);

        SingleInstance.ListenForActivation(() => Dispatcher.BeginInvoke(ShowDeviceList));

        // 验收辅助：--exit-after N 启动 N 秒后走正常退出路径（验证鲸鱼娘桥接的离线标记）。
        if (e.Args.FirstOrDefault(a => a.StartsWith("--exit-after", StringComparison.OrdinalIgnoreCase)) is { } exitArg)
        {
            int seconds = 5;
            string? value = exitArg.Contains('=') ? exitArg.Split('=')[^1] : null;
            if (value is not null && int.TryParse(value, out int parsed) && parsed > 0)
            {
                seconds = parsed;
            }

            var exitTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(seconds),
            };
            exitTimer.Tick += (_, _) =>
            {
                exitTimer.Stop();
                AppServices.Log.Info("--exit-after：触发正常退出");
                Shutdown();
            };
            exitTimer.Start();
        }

        // 验收辅助：--probe-popup 直接把快捷面板弹在屏幕右下角（无需点托盘）。
        if (e.Args.Any(a => a.Equals("--probe-popup", StringComparison.OrdinalIgnoreCase)))
        {
            // 延迟到首轮真实探测完成后再弹，模拟真实使用场景（用户是事后点托盘，缓存已填充）。
            var probeDelay = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(8),
            };
            probeDelay.Tick += (_, _) =>
            {
                probeDelay.Stop();
                TaskbarInfo tb = TaskbarHelper.Probe();
                PhysicalRect area = tb.Monitor.Bounds;
                // 模拟托盘图标位置：屏幕右下角、任务栏上方 30px 处。
                int anchorX = area.Right - 180;
                int anchorY = area.Bottom - tb.Thickness - 30;
                if (_popup is null)
                {
                    _popup = new TrayPopupWindow();
                    _popup.SetAlertSource(id => _tray?.IsLowBatteryAlerted(id) ?? false);
                    _popup.Closed += (_, _) => _popup = null;
                }

                try
                {
                    _popup.ShowAtTray(new PhysicalRect(anchorX, anchorY, anchorX, anchorY), ShowDeviceList,
                        keepOpenForCapture: true);
                }
                catch (Exception ex)
                {
                    AppServices.Log.Error("--probe-popup 弹出失败", ex);
                }
            };
            probeDelay.Start();
        }

        AppServices.Log.Info("托盘已就绪。");
    }

    private void SystemEvents_Subscribe()
    {
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn("订阅系统主题变更失败：" + ex.Message);
        }
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(new Action(ThemeManager.ApplyFromSystem), DispatcherPriority.Background);
        }
    }

    private TrayPopupWindow? _popup;

    /// <summary>托盘左键：贴着图标弹出快捷面板。</summary>
    private void ShowTrayPopup(PhysicalRect anchor)
    {
        if (_popup is null)
        {
            _popup = new TrayPopupWindow();
            _popup.SetAlertSource(id => _tray?.IsLowBatteryAlerted(id) ?? false);
            _popup.Closed += (_, _) => _popup = null;
        }

        // 已显示则先收起（左键点一下开、再点一下关）。
        if (_popup.IsVisible)
        {
            _popup.Hide();
            return;
        }

        _popup.ShowAtTray(anchor, ShowDeviceList);
    }

    private void ShowDeviceList()
    {
        if (_tray is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new DeviceListWindow(_tray.Config) { ShowInTaskbar = true };
            _window.SetAlertSource(id => _tray?.IsLowBatteryAlerted(id) ?? false);

            // 设置保存后让托盘立即重算（换新阈值、换静音开关都要马上生效）
            _window.RefreshTray = () => _ = _tray?.RefreshAsync(true);
            _window.Closed += (_, _) => _window = null;
        }
        else
        {
            _window.UpdateConfig(_tray.Config);
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _ = _window.ReloadAsync(readBatteries: true);
    }

    /// <summary>只在真正持有互斥体的实例里释放；释放失败按物资清理处理，绝不因退出路径崩溃。</summary>
    private void ReleaseInstance()
    {
        var mutex = _instanceMutex;
        _instanceMutex = null;
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException ex)
        {
            AppServices.Log.Warn("释放单实例互斥体失败（非属主线程）：" + ex.Message);
        }
        finally
        {
            mutex.Dispose();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch (Exception)
        {
            // 退出路径上的清理失败不影响进程结束。
        }

        AppServices.Device?.Stop();

        // 先让鲸鱼娘知道托盘要走了，再清理托盘资源：
        // 前端据此显示「托盘未运行」而不是把旧数据当实时值。
        WhaleBridge.MarkOffline();

        _tray?.Dispose();
        ReleaseInstance();
        AppServices.Log.Info("退出。");
        base.OnExit(e);
    }
}

using System;
using System.Threading;

namespace WirelessBatteryTray.App;

/// <summary>单实例：Mutex 判定 + 命名事件把「再次启动」变成「激活已有窗口」。</summary>
public static class SingleInstance
{
    public const string MutexName = @"Local\WirelessBatteryTray.SingleInstance.Mutex";
    private const string EventName = @"Local\WirelessBatteryTray.SingleInstance.Activate";

    public static void ListenForActivation(Action onActivate)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!handle.WaitOne())
                    {
                        return;
                    }

                    onActivate();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    // 激活失败不能杀掉监听线程。
                }
            }
        })
        {
            IsBackground = true,
            Name = "wbt-activate-listener",
        };
        listener.Start();
    }

    /// <summary>
    /// 通知已有实例激活窗口。事件可能尚未建好（竞态），因此额外做一次
    /// 按进程名唤醒：即使事件信号丢失，用户也能看到窗口。
    /// </summary>
    public static void SignalExisting()
    {
        bool signalled = false;
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                    signalled = true;
                }
            }
        }
        catch (Exception)
        {
            // 事件通道不可用时走下面的进程唤醒兜底。
        }

        if (!signalled)
        {
            WakeExistingProcess();
        }
    }

    private static void WakeExistingProcess()
    {
        foreach (System.Diagnostics.Process other in
            System.Diagnostics.Process.GetProcessesByName("WirelessBatteryTray.App"))
        {
            if (other.Id != Environment.ProcessId && other.MainWindowHandle != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(other.MainWindowHandle, 9);
                NativeMethods.SetForegroundWindow(other.MainWindowHandle);
            }
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}

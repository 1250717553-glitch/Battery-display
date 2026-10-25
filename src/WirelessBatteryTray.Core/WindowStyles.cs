using System;
using System.Runtime.InteropServices;

namespace WirelessBatteryTray.Core;

/// <summary>
/// 悬浮层窗口的 Win32 扩展样式设置（纯 P/Invoke，不依赖 WPF，
/// 因此 Core 可被 BatteryProbe 直接引用来做验证）。
///
/// 三个样式各自解决的问题（缺一不可）：
///   WS_EX_NOACTIVATE  -> 窗口显示或鼠标点击时都不激活自己，不会把前台程序/全屏游戏切走。
///                        注意：WPF 的 ShowActivated="False" 只管「首次显示」，鼠标点击仍会抢焦点，
///                        所以必须再加这个扩展样式。
///   WS_EX_TOOLWINDOW  -> 不出现在 Alt+Tab 列表里，同时也不产生任务栏按钮。
///   WS_EX_TOPMOST     -> 置顶。WPF 的 Topmost="True" 已能设置，这里提供 Win32 兜底与重新断言能力。
///
/// 设置时机很关键：必须在窗口<b>创建后、首次显示前</b>完成（WPF 的 SourceInitialized 阶段）。
/// 先 Show() 再改样式，窗口可能已经抢过一次焦点。
/// </summary>
public static class WindowStyles
{
    private const int GWL_EXSTYLE = -20;

    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>给窗口加 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW + WS_EX_TOPMOST。幂等。</summary>
    public static bool MakeNonActivatingToolWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        long current = GetExtendedStyle(handle);
        long updated = current | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        return SetExtendedStyle(handle, updated);
    }

    /// <summary>把窗口重新提到最顶层（不激活、不改尺寸与位置）。</summary>
    public static bool AssertTopmost(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static long GetExtendedStyle(IntPtr handle) =>
        handle == IntPtr.Zero ? 0 : GetWindowLongPtrSafe(handle, GWL_EXSTYLE).ToInt64();

    public static bool SetExtendedStyle(IntPtr handle, long exStyle) =>
        handle != IntPtr.Zero && SetWindowLongPtrSafe(handle, GWL_EXSTYLE, new IntPtr(exStyle)) != IntPtr.Zero;

    public static bool HasNoActivate(long exStyle) => (exStyle & WS_EX_NOACTIVATE) != 0;

    public static bool HasToolWindow(long exStyle) => (exStyle & WS_EX_TOOLWINDOW) != 0;

    public static bool HasTopmost(long exStyle) => (exStyle & WS_EX_TOPMOST) != 0;

    /// <summary>当前前台窗口句柄，用于「弹出前后焦点是否变化」的验证。</summary>
    public static IntPtr GetForegroundWindow() => NativeGetForegroundWindow();

    // GetWindowLongPtr 只在 64 位存在；32 位系统上必须调用 GetWindowLong。
    private static IntPtr GetWindowLongPtrSafe(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr NativeGetForegroundWindow();
}

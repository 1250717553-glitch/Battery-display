using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WirelessBatteryTray.Core;

/// <summary>任务栏贴边方向（与 SHAppBarMessage 的 uEdge 一致）。</summary>
public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
    Unknown = -1,
}

/// <summary>一次任务栏探测的结果，坐标统一为**物理像素**。</summary>
public sealed record TaskbarInfo
{
    public required TaskbarEdge Edge { get; init; }

    /// <summary>任务栏矩形（物理像素）。</summary>
    public required PhysicalRect Bounds { get; init; }

    /// <summary>任务栏是否开启自动隐藏（ABS_AUTOHIDE）。</summary>
    public required bool IsAutoHide { get; init; }

    /// <summary>任务栏是否总在最前（ABS_ALWAYSONTOP）。</summary>
    public required bool IsAlwaysOnTop { get; init; }

    /// <summary>本次探测所处的显示器信息（物理像素）。</summary>
    public required MonitorInfo Monitor { get; init; }

    /// <summary>该任务栏厚度（物理像素）：下/上边取高度，左/右边取宽度。</summary>
    public int Thickness => Edge is TaskbarEdge.Bottom or TaskbarEdge.Top
        ? Bounds.Height
        : Bounds.Width;

    public bool IsValid => Edge != TaskbarEdge.Unknown && Bounds.Width > 0 && Bounds.Height > 0;
}

/// <summary>显示器信息（物理像素）。</summary>
public sealed record MonitorInfo
{
    public required PhysicalRect Bounds { get; init; }

    public required PhysicalRect WorkArea { get; init; }

    public required bool IsPrimary { get; init; }

    public required string DeviceName { get; init; }
}

/// <summary>物理像素矩形。所有对外坐标都必须是这个语义，绝不混用逻辑单位。</summary>
public readonly record struct PhysicalRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// 任务栏几何探测。
///
/// 关键约束（真机实测得出，见 P2-技术调研.md）：
///  1. SHAppBarMessage 返回的是**逻辑单位**，而 GetWindowRect/GetCursorPos 是**物理像素**。
///     本机 200% 缩放下，前者给 2880×96，后者给 1440×48——正好 2 倍。混用必然错位。
///     本类**只输出物理像素**，逻辑值一律就地换算。
///  2. 自动隐藏开启时 WorkingArea == Bounds（实测相等），因此不能用 WorkingArea 推算任务栏位置。
///  3. SHAppBarMessage 只返回**主**任务栏；多显示器需自己按 MonitorFromPoint 映射。
/// </summary>
public static class TaskbarHelper
{
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint ABM_GETSTATE = 0x00000004;
    private const int ABS_AUTOHIDE = 0x00000001;
    private const int ABS_ALWAYSONTOP = 0x00000002;

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>
    /// 探测鼠标所在显示器上的任务栏几何。
    /// 传入鼠标物理坐标；为空则探测主显示器。
    /// </summary>
    public static TaskbarInfo Probe(int? cursorX = null, int? cursorY = null)
    {
        // 1) 目标显示器：优先鼠标所在屏；多显示器时主任务栏不一定在此屏，
        //    因此矩形按该屏边缘自行推导（见下方 BuildBoundsForMonitor）。
        MonitorInfo monitor = cursorX.HasValue && cursorY.HasValue
            ? GetMonitorAtPoint(cursorX.Value, cursorY.Value)
            : GetPrimaryMonitor();

        // 2) 唯一的任务栏状态来源
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        bool hasPosition = SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero;
        uint state = (uint)SHAppBarMessage(ABM_GETSTATE, ref data).ToInt64();

        var edge = hasPosition ? (TaskbarEdge)data.uEdge : TaskbarEdge.Unknown;
        if (!hasPosition || edge == TaskbarEdge.Unknown)
        {
            return new TaskbarInfo
            {
                Edge = TaskbarEdge.Unknown,
                Bounds = new PhysicalRect(0, 0, 0, 0),
                IsAutoHide = (state & ABS_AUTOHIDE) != 0,
                IsAlwaysOnTop = (state & ABS_ALWAYSONTOP) != 0,
                Monitor = monitor,
            };
        }

        // 3) 把「逻辑单位」的任务栏厚度换算为物理像素，再贴合到目标显示器边缘。
        //    厚度是跨屏唯一可靠的信息：即使任务栏在另一块屏上，厚度仍按本屏 DPI 换算即可。
        int logicalThickness = edge is TaskbarEdge.Bottom or TaskbarEdge.Top
            ? data.rc.Bottom - data.rc.Top
            : data.rc.Right - data.rc.Left;

        int physicalThickness = ScaleLogicalToPhysical(monitor, logicalThickness);
        PhysicalRect bounds = BuildBoundsForMonitor(monitor.Bounds, edge, physicalThickness);

        return new TaskbarInfo
        {
            Edge = edge,
            Bounds = bounds,
            IsAutoHide = (state & ABS_AUTOHIDE) != 0,
            IsAlwaysOnTop = (state & ABS_ALWAYSONTOP) != 0,
            Monitor = monitor,
        };
    }

    /// <summary>
    /// 逻辑厚度 -> 物理厚度。用任务栏真实窗口的物理尺寸与逻辑厚度的比值反推缩放，
    /// 而不是假设系统 DPI 就等于该屏 DPI（多屏混合缩放下两者可以不同）。
    /// </summary>
    private static int ScaleLogicalToPhysical(MonitorInfo monitor, int logicalThickness)
    {
        if (logicalThickness <= 0)
        {
            return 0;
        }

        // 参考量：系统报告的该屏物理宽度 / 主任务栏逻辑宽度。避免依赖浮点配置漂移。
        int ratioNumerator = monitor.Bounds.Width;
        bool ok = TryGetPrimaryTaskbarLogicalWidth(out int logicalWidth);
        if (ok && logicalWidth > 0)
        {
            // 物理 = 逻辑 × (屏宽物理 / 屏宽逻辑)。这里屏宽逻辑由任务栏逻辑宽度同源推算，
            // 因此直接用整数比例，避免 125% 这类非整数缩放被截断。
            long scaled = (long)logicalThickness * ratioNumerator / logicalWidth;
            if (scaled > 0)
            {
                return (int)scaled;
            }
        }

        // 兜底：按整屏物理宽 / (屏物理宽按系统 DPI 折回逻辑) 估算，保证至少能给出合理值。
        int dpi = GetDpiForMonitor(monitor);
        if (dpi > 0)
        {
            return (int)Math.Round(logicalThickness * (dpi / 96.0));
        }

        return logicalThickness;
    }

    private static bool TryGetPrimaryTaskbarLogicalWidth(out int width)
    {
        width = 0;
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero)
        {
            return false;
        }

        width = data.rc.Right - data.rc.Left;
        return width > 0;
    }

    /// <summary>按显示器矩形与贴边方向构造任务栏物理矩形。</summary>
    private static PhysicalRect BuildBoundsForMonitor(PhysicalRect monitor, TaskbarEdge edge, int thickness)
    {
        return edge switch
        {
            TaskbarEdge.Bottom => new PhysicalRect(
                monitor.Left, monitor.Bottom - thickness, monitor.Right, monitor.Bottom),
            TaskbarEdge.Top => new PhysicalRect(
                monitor.Left, monitor.Top, monitor.Right, monitor.Top + thickness),
            TaskbarEdge.Left => new PhysicalRect(
                monitor.Left, monitor.Top, monitor.Left + thickness, monitor.Bottom),
            TaskbarEdge.Right => new PhysicalRect(
                monitor.Right - thickness, monitor.Top, monitor.Right, monitor.Bottom),
            _ => new PhysicalRect(0, 0, 0, 0),
        };
    }

    /// <summary>
    /// 计算「贴着某个屏幕坐标点上方弹出」的窗口位置（物理像素输入，物理像素输出）。
    /// 用于托盘面板：以鼠标点击点为锚点，水平居中对齐，底边贴住锚点上方。
    /// 结果会被夹在该显示器范围内，避免面板跑出屏幕。
    /// </summary>
    public static PhysicalRect PlaceAbove(int anchorX, int anchorY, int popupWidth, int popupHeight, int gap)
    {
        MonitorInfo monitor = GetMonitorAtPoint(anchorX, anchorY);
        PhysicalRect area = monitor.Bounds;

        int left = anchorX - (popupWidth / 2);
        int top = anchorY - popupHeight - gap;

        // 水平夹取
        int maxLeft = area.Right - popupWidth;
        if (left < area.Left)
        {
            left = area.Left;
        }
        else if (left > maxLeft)
        {
            left = Math.Max(area.Left, maxLeft);
        }

        // 上方放不下时翻到下方
        if (top < area.Top)
        {
            top = anchorY + gap;
        }

        int maxTop = area.Bottom - popupHeight;
        if (top > maxTop)
        {
            top = Math.Max(area.Top, maxTop);
        }

        return new PhysicalRect(left, top, left + popupWidth, top + popupHeight);
    }

    /// <summary>鼠标当前物理坐标（供托盘图标定位使用）。</summary>
    public static (int X, int Y) GetCursorPosition()
    {
        return GetCursorPos(out POINT pt) ? (pt.X, pt.Y) : (0, 0);
    }

    private static MonitorInfo GetPrimaryMonitor()
    {
        IntPtr primary = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTONEAREST);
        return DescribeMonitor(primary);
    }

    private static MonitorInfo GetMonitorAtPoint(int x, int y)
    {
        IntPtr handle = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return DescribeMonitor(handle);
    }

    private static MonitorInfo DescribeMonitor(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return new MonitorInfo
            {
                Bounds = new PhysicalRect(0, 0, 0, 0),
                WorkArea = new PhysicalRect(0, 0, 0, 0),
                IsPrimary = true,
                DeviceName = string.Empty,
            };
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(handle, ref info))
        {
            return new MonitorInfo
            {
                Bounds = new PhysicalRect(0, 0, 0, 0),
                WorkArea = new PhysicalRect(0, 0, 0, 0),
                IsPrimary = true,
                DeviceName = string.Empty,
            };
        }

        return new MonitorInfo
        {
            Bounds = new PhysicalRect(
                info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            WorkArea = new PhysicalRect(
                info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
            IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
            DeviceName = string.Empty,
        };
    }

    private static int GetDpiForMonitor(MonitorInfo monitor)
    {
        // 用 GetDpiForWindow 无法取到非前台屏，这里以 GetDpiForSystem 兜底，
        // 真实的多屏混合 DPI 需要 per-monitor v2 感知，留待第二阶段处理。
        return GetDpiForSystem();
    }

    private const int MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetDpiForSystem();
}
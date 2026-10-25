using System;
using System.Runtime.InteropServices;
using WirelessBatteryTray.Core;

namespace BatteryProbe;

/// <summary>
/// TaskbarHelper 验证入口：把「原始 API 返回值」与「Helper 换算后的物理像素」并列打印，
/// 逻辑单位与物理像素的 2 倍关系是否被正确处理，一眼可判。
/// </summary>
internal static class TaskbarCheck
{
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const uint ABM_GETTASKBARPOS = 0x00000005;

    public static int Run(int? x, int? y)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== TaskbarHelper 验证 ===");
        Console.WriteLine("传入鼠标坐标: " + (x.HasValue ? "(" + x + "," + y + ")" : "(未传，使用主显示器)"));
        Console.WriteLine();

        // ---------- 对照组 A：原始 API（逻辑单位） ----------
        var raw = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        IntPtr ok = SHAppBarMessage(ABM_GETTASKBARPOS, ref raw);
        Console.WriteLine("[原始A] SHAppBarMessage 返回=" + ok
            + " edge=" + raw.uEdge
            + " rc=(" + raw.rc.Left + "," + raw.rc.Top + ")-(" + raw.rc.Right + "," + raw.rc.Bottom + ")"
            + " 宽=" + (raw.rc.Right - raw.rc.Left)
            + " 高=" + (raw.rc.Bottom - raw.rc.Top)
            + "   <-- 逻辑单位");

        // ---------- 对照组 B：任务栏窗口（物理像素） ----------
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT rp))
        {
            Console.WriteLine("[原始B] GetWindowRect(Shell_TrayWnd)=( " + rp.Left + "," + rp.Top + ")-("
                + rp.Right + "," + rp.Bottom + ")"
                + " 宽=" + (rp.Right - rp.Left) + " 高=" + (rp.Bottom - rp.Top)
                + "   <-- 物理像素");
        }

        Console.WriteLine();

        // ---------- 被测对象 ----------
        TaskbarInfo info = TaskbarHelper.Probe(x, y);
        Console.WriteLine("[Helper] 边缘     = " + info.Edge + " (" + (int)info.Edge + ")");
        Console.WriteLine("[Helper] 矩形     = (" + info.Bounds.Left + "," + info.Bounds.Top + ")-("
            + info.Bounds.Right + "," + info.Bounds.Bottom + ")"
            + " 宽=" + info.Bounds.Width + " 高=" + info.Bounds.Height);
        Console.WriteLine("[Helper] 厚度     = " + info.Thickness);
        Console.WriteLine("[Helper] 自动隐藏 = " + info.IsAutoHide);
        Console.WriteLine("[Helper] 总在前   = " + info.IsAlwaysOnTop);
        Console.WriteLine("[Helper] 有效     = " + info.IsValid);
        Console.WriteLine("[Helper] 显示器   = (" + info.Monitor.Bounds.Left + "," + info.Monitor.Bounds.Top
            + ")-(" + info.Monitor.Bounds.Right + "," + info.Monitor.Bounds.Bottom + ")"
            + " 主屏=" + info.Monitor.IsPrimary);

        Console.WriteLine();
        Console.WriteLine("=== 自动判定 ===");
        int pass = 0;
        int fail = 0;

        // 1) Helper 必须返回有效几何
        Check(info.IsValid, "矩形非空、边缘已知", ref pass, ref fail);

        // 2) 关键：Helper 的厚度必须等于「物理像素」量级，而不是逻辑单位。
        //    判据：与 GetWindowRect 的任务栏物理厚度一致（误差 <=1px）。
        if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT rp2))
        {
            int physicalThickness = info.Edge is TaskbarEdge.Bottom or TaskbarEdge.Top
                ? rp2.Bottom - rp2.Top
                : rp2.Right - rp2.Left;
            bool close = Math.Abs(info.Thickness - physicalThickness) <= 1;
            Console.WriteLine((close ? "  [PASS] " : "  [FAIL] ")
                + "厚度物理量级: Helper=" + info.Thickness + " vs GetWindowRect=" + physicalThickness);
            if (close) pass++; else fail++;
        }

        // 3) 任务栏矩形必须贴合显示器对应边缘
        bool onEdge = info.Edge switch
        {
            TaskbarEdge.Bottom => info.Bounds.Bottom == info.Monitor.Bounds.Bottom,
            TaskbarEdge.Top => info.Bounds.Top == info.Monitor.Bounds.Top,
            TaskbarEdge.Left => info.Bounds.Left == info.Monitor.Bounds.Left,
            TaskbarEdge.Right => info.Bounds.Right == info.Monitor.Bounds.Right,
            _ => false,
        };
        Check(onEdge, "贴合显示器对应边缘", ref pass, ref fail);

        // 4) 任务栏矩形不得超出显示器
        bool inside = info.Bounds.Left >= info.Monitor.Bounds.Left
            && info.Bounds.Top >= info.Monitor.Bounds.Top
            && info.Bounds.Right <= info.Monitor.Bounds.Right
            && info.Bounds.Bottom <= info.Monitor.Bounds.Bottom;
        Check(inside, "不超出显示器范围", ref pass, ref fail);

        Console.WriteLine();
        Console.WriteLine("结果: " + pass + " 通过 / " + fail + " 失败");
        return fail == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string label, ref int pass, ref int fail)
    {
        Console.WriteLine((condition ? "  [PASS] " : "  [FAIL] ") + label);
        if (condition) pass++; else fail++;
    }
}

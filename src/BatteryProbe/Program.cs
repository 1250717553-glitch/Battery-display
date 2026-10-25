using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using WirelessBatteryTray.Core;
using WirelessBatteryTray.Core.Providers;

namespace BatteryProbe;

/// <summary>
/// 只读 HID 探测工具：列出全部 HID 接口能力，对目标接收器采样输入报文并做逐字节统计，
/// 用于推断 2.4G 接收器的电量报文格式。绝不向设备写入任何数据。
/// </summary>
internal static class Program
{
    private const ushort TargetVid = 0x249A;
    private const ushort TargetPid = 0x5C2F;

    private static int _durationSeconds = 10;
    private static int _intervalMs = 0;

    private static int Main(string[] args)
    {
        if (!ParseArgs(args))
        {
            return 1;
        }

        if (args.Any(a => a.Equals("--check-offline", StringComparison.OrdinalIgnoreCase)))
        {
            return OfflineCheck.RunAsync().GetAwaiter().GetResult();
        }

        if (args.Any(a => a.Equals("--check-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            return TaskbarCheck.Run(null, null);
        }

        if (args.Any(a => a.Equals("--check-alert", StringComparison.OrdinalIgnoreCase)))
        {
            return AlertCheck.Run();
        }

        // 汇总自检：一次跑完全部不依赖设备的回归项。
        if (args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== 回归自检合集 (--diagnose) ===");
            Console.WriteLine();

            int offline = OfflineCheck.RunAsync().GetAwaiter().GetResult();
            Console.WriteLine();
            int alert = AlertCheck.Run();
            Console.WriteLine();
            int taskbar = TaskbarCheck.Run(null, null);

            Console.WriteLine();
            Console.WriteLine("=== 汇总 ===");
            Console.WriteLine("离线降级: " + (offline == 0 ? "通过" : "失败"));
            Console.WriteLine("低电量报警: " + (alert == 0 ? "通过" : "失败"));
            Console.WriteLine("任务栏几何: " + (taskbar == 0 ? "通过" : "失败"));

            bool allOk = offline == 0 && alert == 0 && taskbar == 0;
            Console.WriteLine(allOk ? "全部通过" : "存在失败项");
            return allOk ? 0 : 1;
        }

        AppServices.Log = new FileLog();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("=== HID 只读探测 (BatteryProbe) ===");
        Console.WriteLine($"采样时长 {_durationSeconds}s" + (_intervalMs > 0 ? $", 读取间隔 {_intervalMs}ms" : string.Empty));
        Console.WriteLine();

        List<HidInterfaceInfo> all;
        try
        {
            all = HidEnumerator.Enumerate();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[致命] HID 枚举失败: " + ex);
            return 2;
        }

        Console.WriteLine($"=== 全部 HID 接口 ({all.Count}) ===");
        Console.WriteLine("VID:PID     MI  COL  UsagePage Usage  InLen FeatLen  Serial          Product / Path");
        foreach (HidInterfaceInfo info in all)
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:x4}:{1:x4}  {2:x2}  {3:x2}   {4:x4}      {5:x4}   {6,3}   {7,3}    {8,-15} {9}",
                info.VendorId,
                info.ProductId,
                info.InterfaceNumber,
                info.CollectionNumber,
                info.UsagePage,
                info.Usage,
                info.InputReportByteLength,
                info.FeatureReportByteLength,
                string.IsNullOrEmpty(info.SerialNumber) ? "<none>" : info.SerialNumber,
                info.ProductString));
            Console.WriteLine("    path=" + info.InterfacePath);
            Console.WriteLine("    stableId=" + info.StableId + "  readable=" + info.Readable
                + (info.OpenError != 0 ? "  openError=" + info.OpenError : string.Empty));
        }

        List<HidInterfaceInfo> targets = all.Where(i => i.IsTargetDevice).ToList();
        Console.WriteLine();
        Console.WriteLine($"=== 目标设备 VID_{TargetVid:x4}&PID_{TargetPid:x4} ({targets.Count} 个接口) ===");
        if (targets.Count == 0)
        {
            Console.WriteLine("[未找到] 请确认接收器已插好。这属于真实探测结果，不是程序错误。");
            return 3;
        }

        int sampled = 0;
        foreach (HidInterfaceInfo info in targets)
        {
            Console.WriteLine();
            Console.WriteLine("--- 接口 " + info.InterfaceSummary + " InLen=" + info.InputReportByteLength + " ---");
            if (info.InputReportByteLength <= 0)
            {
                Console.WriteLine("    该接口无输入报文，跳过。");
                continue;
            }

            if (SampleInput(info))
            {
                sampled++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== 完成：{sampled}/{targets.Count} 个接口采到报文 ===");
        return 0;
    }

    private static bool ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].Trim();
            if (arg is "--help" or "-h" or "/?")
            {
                Console.WriteLine("用法: BatteryProbe [--duration N] [--interval N]");
                Console.WriteLine("  --duration  采样总时长（秒），默认 10");
                Console.WriteLine("  --interval  两次读取之间的主动等待（毫秒），默认 0");
                return false;
            }

            if (ValueOf(args, ref i, "--duration", out string? d)
                && int.TryParse(d, out int seconds) && seconds > 0)
            {
                _durationSeconds = Math.Min(seconds, 300);
                continue;
            }

            if (ValueOf(args, ref i, "--interval", out string? iv)
                && int.TryParse(iv, out int interval) && interval >= 0)
            {
                _intervalMs = Math.Min(interval, 5000);
            }
        }

        return true;
    }

    /// <summary>同时接受 "--name=value" 与 "--name value"，前者不推进索引。</summary>
    private static bool ValueOf(string[] args, ref int index, string name, out string? value)
    {
        value = null;
        string arg = args[index];
        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg.Substring(name.Length + 1);
            return true;
        }

        if (!arg.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= args.Length)
        {
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static bool SampleInput(HidInterfaceInfo info)
    {
        using SafeFileHandle handle = HidNative.CreateFileW(
            info.InterfacePath,
            HidNative.GenericRead,
            HidNative.FileShareRead | HidNative.FileShareWrite,
            IntPtr.Zero,
            HidNative.OpenExisting,
            HidNative.FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            Console.WriteLine("    [打不开] Win32 错误码 " + Marshal.GetLastWin32Error()
                + "（可能被占用或权限不足，属真实结果）");
            return false;
        }

        int length = Math.Max(info.InputReportByteLength, 1);
        IntPtr buffer = Marshal.AllocHGlobal(length);
        var overlapped = new NativeOverlapped();
        IntPtr evt = CreateEvent(IntPtr.Zero, true, false, null);
        overlapped.EventHandle = evt;

        var byteValues = new SortedDictionary<int, SortedSet<int>>();
        int reports = 0;
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(_durationSeconds);
            while (DateTime.UtcNow < deadline)
            {
                overlapped.OffsetLow = 0;
                overlapped.OffsetHigh = 0;
                if (!HidNative.ReadFile(handle, buffer, length, IntPtr.Zero, ref overlapped))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != HidNative.ErrorIoPending)
                    {
                        Console.WriteLine("    [读取失败] Win32 错误码 " + err);
                        break;
                    }

                    uint wait = HidNative.WaitForSingleObject(evt, 500);
                    if (wait == 0x102)
                    {
                        // 超时：该接口当前没有主动上报，继续等（静默接口是重要结论）。
                        HidNative.CancelIoEx(handle, ref overlapped);
                        if (_intervalMs > 0)
                        {
                            Thread.Sleep(_intervalMs);
                        }

                        continue;
                    }

                    if (!HidNative.GetOverlappedResult(handle, ref overlapped, out int read, false) || read <= 0)
                    {
                        continue;
                    }

                    var bytes = new byte[read];
                    Marshal.Copy(buffer, bytes, 0, read);
                    Report(bytes, byteValues);
                    reports++;
                }
                else
                {
                    var bytes = new byte[length];
                    Marshal.Copy(buffer, bytes, 0, length);
                    Report(bytes, byteValues);
                    reports++;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (evt != IntPtr.Zero)
            {
                CloseHandle(evt);
            }
        }

        Console.WriteLine();
        Console.WriteLine("    共收到 " + reports + " 条报文。");
        if (reports == 0)
        {
            Console.WriteLine("    [静默接口] 采样期内设备未主动上报任何数据 —— 这是判断「是否需要主动查询指令」的关键证据。");
            return false;
        }

        Console.WriteLine("    逐字节取值统计（位置: 出现过的不同取值）:");
        foreach (KeyValuePair<int, SortedSet<int>> pair in byteValues)
        {
            string values = string.Join(",", pair.Value.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            if (values.Length > 120)
            {
                values = values.Substring(0, 117) + "...";
            }

            Console.WriteLine($"      byte[{pair.Key,2}] ({pair.Value.Count} 种): {values}");
        }

        Console.WriteLine("    启发式候选（0..100 区间内且出现过变化）:");
        bool any = false;
        foreach (KeyValuePair<int, SortedSet<int>> pair in byteValues)
        {
            if (pair.Value.Count > 1 && pair.Value.All(v => v is >= 0 and <= 100))
            {
                Console.WriteLine($"      -> byte[{pair.Key}] 很可能是电量类字段（量纲未知，需对照插拔充电线观察）");
                any = true;
            }
        }

        if (!any)
        {
            Console.WriteLine("      （无满足条件的字节）");
        }

        return true;
    }

    private static void Report(byte[] bytes, SortedDictionary<int, SortedSet<int>> stats)
    {
        Console.WriteLine("    report[" + bytes.Length + "] " +
            string.Join(" ", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))));
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!stats.TryGetValue(i, out SortedSet<int>? set))
            {
                set = new SortedSet<int>();
                stats[i] = set;
            }

            set.Add(bytes[i]);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

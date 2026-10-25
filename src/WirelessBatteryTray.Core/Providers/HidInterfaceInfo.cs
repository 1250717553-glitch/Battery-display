using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WirelessBatteryTray.Core.Providers;

/// <summary>一个 HID 设备接口（一个顶层集合）的描述。</summary>
internal sealed class HidInterfaceInfo
{
    private static readonly Regex MiPattern = new Regex("mi_([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ColPattern = new Regex("col([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public required string InterfacePath { get; init; }

    public ushort VendorId { get; init; }

    public ushort ProductId { get; init; }

    public byte InterfaceNumber { get; init; }

    public byte CollectionNumber { get; init; }

    public ushort UsagePage { get; init; }

    public ushort Usage { get; init; }

    public int InputReportByteLength { get; init; }

    public int OutputReportByteLength { get; init; }

    public int FeatureReportByteLength { get; init; }

    public string ProductString { get; init; } = string.Empty;

    /// <summary>HidD_GetSerialNumberString 的真实返回值；拿不到时为空字符串。</summary>
    public string SerialNumber { get; init; } = string.Empty;

    public bool Readable { get; init; }

    public int OpenError { get; init; }

    /// <summary>同一 (VID,PID,MI,COL) 下的顶层集合数量，由枚举器收齐后回填。</summary>
    public int TopLevelCollectionCount { get; set; } = 1;

    public bool IsTargetDevice => VendorId == 0x249A && ProductId == 0x5C2F;

    public string InterfaceSummary => "MI_" + InterfaceNumber.ToString("x2") + "&COL" + CollectionNumber.ToString("x2");

    /// <summary>设备实例 ID（仅用于展示/诊断，不作为 StableId）。</summary>
    public string DeviceInstanceId
    {
        get
        {
            string trimmed = InterfacePath;
            int guidIndex = trimmed.IndexOf("#{", StringComparison.Ordinal);
            if (guidIndex > 0)
            {
                trimmed = trimmed.Substring(0, guidIndex);
            }

            int hidIndex = trimmed.IndexOf("HID#", StringComparison.OrdinalIgnoreCase);
            if (hidIndex >= 0)
            {
                trimmed = trimmed.Substring(hidIndex);
            }

            return trimmed.Replace('#', (char)92);
        }
    }

    /// <summary>
    /// 稳定设备标识。绝不包含接口路径里的实例段/端口段/容器 GUID——
    /// 那些由 USB 端口位置派生，换口或重插就会变，会让用户改名丢失。
    /// </summary>
    public string StableId
    {
        get
        {
            if (!string.IsNullOrEmpty(SerialNumber))
            {
                return "hid:" + VendorId.ToString("x4") + ":" + ProductId.ToString("x4") + ":"
                    + InterfaceNumber.ToString("x2") + ":" + CollectionNumber.ToString("x2") + ":" + SerialNumber;
            }

            return "hid:" + VendorId.ToString("x4") + ":" + ProductId.ToString("x4") + ":"
                + InterfaceNumber.ToString("x2") + ":" + CollectionNumber.ToString("x2") + ":"
                + UsagePage.ToString("x4") + ":" + Usage.ToString("x4") + ":" + TopLevelCollectionCount.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static (byte Mi, byte Col) ParseMiCol(string path)
    {
        string trimmed = path;
        int guidIndex = trimmed.IndexOf("#{", StringComparison.Ordinal);
        if (guidIndex > 0)
        {
            trimmed = trimmed.Substring(0, guidIndex);
        }

        byte mi = 0;
        byte col = 0;

        Match miMatch = MiPattern.Match(trimmed);
        if (miMatch.Success &&
            byte.TryParse(miMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte miValue))
        {
            mi = miValue;
        }

        Match colMatch = ColPattern.Match(trimmed);
        if (colMatch.Success &&
            byte.TryParse(colMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte colValue))
        {
            col = colValue;
        }

        return (mi, col);
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WirelessBatteryTray.Core.Providers;

/// <summary>SetupAPI 只读枚举：列出所有 HID 设备接口及其能力。</summary>
internal static class HidEnumerator
{
    public static List<HidInterfaceInfo> Enumerate()
    {
        var result = new List<HidInterfaceInfo>();
        Guid guid = HidNative.GuidDeviceInterfaceHid;
        IntPtr set = HidNative.SetupDiGetClassDevsW(
            ref guid,
            IntPtr.Zero,
            IntPtr.Zero,
            HidNative.DigcfPresent | HidNative.DigcfDeviceInterface);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return result;
        }

        try
        {
            int index = 0;
            while (true)
            {
                var data = new HidNative.SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<HidNative.SpDeviceInterfaceData>(),
                };

                if (!HidNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    break;
                }

                index++;
                string? path = GetInterfacePath(set, ref data);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                result.Add(Describe(path!));
            }
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(set);
        }

        AssignCollectionCounts(result);
        return result;
    }

    public static List<HidInterfaceInfo> EnumerateTarget(ushort vendorId, ushort productId)
    {
        var matched = new List<HidInterfaceInfo>();
        foreach (HidInterfaceInfo info in Enumerate())
        {
            if (info.VendorId == vendorId && info.ProductId == productId)
            {
                matched.Add(info);
            }
        }

        return matched;
    }

    /// <summary>回填同一 (VID,PID,MI,COL) 下的顶层集合数量，供 StableId 兜底串消歧。</summary>
    private static void AssignCollectionCounts(List<HidInterfaceInfo> list)
    {
        var counts = new Dictionary<(ushort, ushort, byte, byte), int>();
        foreach (HidInterfaceInfo info in list)
        {
            var key = (info.VendorId, info.ProductId, info.InterfaceNumber, info.CollectionNumber);
            counts[key] = counts.TryGetValue(key, out int existing) ? existing + 1 : 1;
        }

        foreach (HidInterfaceInfo info in list)
        {
            info.TopLevelCollectionCount = counts[(info.VendorId, info.ProductId, info.InterfaceNumber, info.CollectionNumber)];
        }
    }

    private static string? GetInterfacePath(IntPtr set, ref HidNative.SpDeviceInterfaceData data)
    {
        HidNative.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!HidNative.SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidInterfaceInfo Describe(string path)
    {
        ushort vendorId = 0;
        ushort productId = 0;
        ushort usagePage = 0;
        ushort usage = 0;
        int inputLength = 0;
        int outputLength = 0;
        int featureLength = 0;
        string product = string.Empty;
        string serial = string.Empty;
        bool readable = false;
        int openError = 0;

        (byte mi, byte col) = HidInterfaceInfo.ParseMiCol(path);

        using (SafeFileHandle handle = TryOpen(path, HidNative.GenericRead))
        {
            if (!handle.IsInvalid)
            {
                readable = true;
                ReadBasicInfo(handle, ref vendorId, ref productId, ref usagePage, ref usage, ref inputLength, ref outputLength, ref featureLength, ref product, ref serial);
            }
            else
            {
                openError = Marshal.GetLastWin32Error();
                using SafeFileHandle attributesOnly = TryOpen(path, 0);
                if (!attributesOnly.IsInvalid)
                {
                    ReadBasicInfo(attributesOnly, ref vendorId, ref productId, ref usagePage, ref usage, ref inputLength, ref outputLength, ref featureLength, ref product, ref serial);
                }
            }
        }

        return new HidInterfaceInfo
        {
            InterfacePath = path,
            VendorId = vendorId,
            ProductId = productId,
            InterfaceNumber = mi,
            CollectionNumber = col,
            UsagePage = usagePage,
            Usage = usage,
            InputReportByteLength = inputLength,
            OutputReportByteLength = outputLength,
            FeatureReportByteLength = featureLength,
            ProductString = product,
            SerialNumber = serial,
            Readable = readable,
            OpenError = openError,
        };
    }

    private static void ReadBasicInfo(
        SafeFileHandle handle,
        ref ushort vendorId,
        ref ushort productId,
        ref ushort usagePage,
        ref ushort usage,
        ref int inputLength,
        ref int outputLength,
        ref int featureLength,
        ref string product,
        ref string serial)
    {
        var attributes = new HidNative.HidAttributes { Size = Marshal.SizeOf<HidNative.HidAttributes>() };
        if (HidNative.HidD_GetAttributes(handle, ref attributes))
        {
            vendorId = attributes.VendorId;
            productId = attributes.ProductId;
        }

        product = ReadString(handle, isSerial: false);
        serial = ReadString(handle, isSerial: true);

        if (HidNative.HidD_GetPreparsedData(handle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                HidNative.HidpCaps caps = HidNative.HidpCaps.Create();
                if (HidNative.HidP_GetCaps(preparsed, ref caps) == HidNative.HidpStatusSuccess)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inputLength = caps.InputReportByteLength;
                    outputLength = caps.OutputReportByteLength;
                    featureLength = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                HidNative.HidD_FreePreparsedData(preparsed);
            }
        }
    }

    private static string ReadString(SafeFileHandle handle, bool isSerial)
    {
        const int BufferBytes = 512;
        IntPtr buffer = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            for (int i = 0; i < BufferBytes; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            bool ok = isSerial
                ? HidNative.HidD_GetSerialNumberString(handle, buffer, BufferBytes)
                : HidNative.HidD_GetProductString(handle, buffer, BufferBytes);

            if (!ok)
            {
                return string.Empty;
            }

            string text = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            if (!isSerial)
            {
                return text;
            }

            return IsUsableSerial(text) ? text.Trim() : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsUsableSerial(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (char ch in text)
        {
            if (ch != '0' && ch != (char)0 && !char.IsWhiteSpace(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static SafeFileHandle TryOpen(string path, uint access)
    {
        return HidNative.CreateFileW(
            path,
            access,
            HidNative.FileShareRead | HidNative.FileShareWrite,
            IntPtr.Zero,
            HidNative.OpenExisting,
            HidNative.FileFlagOverlapped,
            IntPtr.Zero);
    }
}

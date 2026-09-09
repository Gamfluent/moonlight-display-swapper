using System.Runtime.InteropServices;

namespace DisplaySwitcher.Interop;

// Layouts mirror wingdi.h. Sizes are asserted at startup by CcdNative.AssertLayouts().

[StructLayout(LayoutKind.Sequential)]
public struct LUID : IEquatable<LUID>
{
    public uint LowPart;
    public int HighPart;

    public bool Equals(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
    public override bool Equals(object? obj) => obj is LUID other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(LowPart, HighPart);
    public override string ToString() => $"{HighPart:X8}-{LowPart:X8}";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINTL
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECTL
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;

    public double AsHz() => Denominator == 0 ? 0d : (double)Numerator / Denominator;
    public override string ToString() => Denominator == 0 ? "0Hz" : $"{AsHz():0.###}Hz";
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_2DREGION
{
    public uint cx;
    public uint cy;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
{
    public ulong pixelRate;
    public DISPLAYCONFIG_RATIONAL hSyncFreq;
    public DISPLAYCONFIG_RATIONAL vSyncFreq;
    public DISPLAYCONFIG_2DREGION activeSize;
    public DISPLAYCONFIG_2DREGION totalSize;

    // Union of AdditionalSignalInfo bitfield and videoStandard; both are 32 bits wide.
    public uint videoStandard;
    public uint scanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SOURCE_MODE
{
    public uint width;
    public uint height;
    public uint pixelFormat;
    public POINTL position;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_TARGET_MODE
{
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
{
    public POINTL PathSourceSize;
    public RECTL DesktopImageRegion;
    public RECTL DesktopImageClip;
}

[StructLayout(LayoutKind.Explicit)]
public struct DISPLAYCONFIG_MODE_INFO_UNION
{
    [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
    [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_MODE_INFO
{
    public uint infoType;
    public uint id;
    public LUID adapterId;
    public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;

    // Union: plain modeInfoIdx, or (cloneGroupId:16 | sourceModeInfoIdx:16) when virtual-mode aware.
    // We never query virtual-mode aware, so this is always the plain index.
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;

    // Union: plain modeInfoIdx, or (desktopModeInfoIdx:16 | targetModeInfoIdx:16) when virtual-mode aware.
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string monitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string viewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct DISPLAYCONFIG_ADAPTER_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string adapterDevicePath;
}

public static class CcdNative
{
    // QueryDisplayConfig flags
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;
    public const uint QDC_VIRTUAL_MODE_AWARE = 0x00000010;
    public const uint QDC_INCLUDE_HMD = 0x00000020;

    // SetDisplayConfig flags
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    public const uint SDC_VALIDATE = 0x00000040;
    public const uint SDC_APPLY = 0x00000080;
    public const uint SDC_NO_OPTIMIZATION = 0x00000100;
    public const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    public const uint SDC_ALLOW_CHANGES = 0x00000400;
    public const uint SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800;
    public const uint SDC_FORCE_MODE_ENUMERATION = 0x00001000;
    public const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;

    // Path flags
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_PREFERRED_UNSCALED = 0x00000004;
    public const uint DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE = 0x00000008;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

    // Status flags
    public const uint DISPLAYCONFIG_SOURCE_IN_USE = 0x00000001;
    public const uint DISPLAYCONFIG_TARGET_IN_USE = 0x00000001;

    // Mode info types
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE = 3;

    // Device info request types
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 3;

    // Scaling / rotation defaults
    public const uint DISPLAYCONFIG_ROTATION_IDENTITY = 1;
    public const uint DISPLAYCONFIG_SCALING_IDENTITY = 1;
    public const uint DISPLAYCONFIG_SCALING_PREFERRED = 128;

    // Win32 error codes
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_BAD_CONFIGURATION = 1610;

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME adapterName);

    /// <summary>
    /// Translates a Win32 error code from the CCD APIs into a readable symbolic name.
    /// The raw numbers alone have caused real diagnostic confusion, so logs always carry the name.
    /// </summary>
    public static string DescribeError(int code) => code switch
    {
        ERROR_SUCCESS => "ERROR_SUCCESS (0)",
        ERROR_ACCESS_DENIED => "ERROR_ACCESS_DENIED (5)",
        ERROR_GEN_FAILURE => "ERROR_GEN_FAILURE (31) - the driver refused the configuration",
        ERROR_NOT_SUPPORTED => "ERROR_NOT_SUPPORTED (50)",
        ERROR_INVALID_PARAMETER => "ERROR_INVALID_PARAMETER (87) - malformed path/mode arrays",
        ERROR_INSUFFICIENT_BUFFER => "ERROR_INSUFFICIENT_BUFFER (122) - topology changed mid-query",
        ERROR_BAD_CONFIGURATION => "ERROR_BAD_CONFIGURATION (1610) - the requested topology is not achievable",
        _ => $"ERROR_{code} (0x{code:X8})"
    };

    /// <summary>
    /// Guards against silent struct-layout drift, which would corrupt every call rather than fail loudly.
    /// </summary>
    public static void AssertLayouts()
    {
        Expect<LUID>(8);
        Expect<DISPLAYCONFIG_RATIONAL>(8);
        Expect<DISPLAYCONFIG_2DREGION>(8);
        Expect<DISPLAYCONFIG_VIDEO_SIGNAL_INFO>(48);
        Expect<DISPLAYCONFIG_SOURCE_MODE>(20);
        Expect<DISPLAYCONFIG_TARGET_MODE>(48);
        Expect<DISPLAYCONFIG_DESKTOP_IMAGE_INFO>(40);
        Expect<DISPLAYCONFIG_MODE_INFO>(64);
        Expect<DISPLAYCONFIG_PATH_SOURCE_INFO>(20);
        Expect<DISPLAYCONFIG_PATH_TARGET_INFO>(48);
        Expect<DISPLAYCONFIG_PATH_INFO>(72);
        Expect<DISPLAYCONFIG_DEVICE_INFO_HEADER>(20);
        Expect<DISPLAYCONFIG_TARGET_DEVICE_NAME>(420);
        Expect<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(84);

        static void Expect<T>(int expected) where T : struct
        {
            var actual = Marshal.SizeOf<T>();
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"Interop layout mismatch: {typeof(T).Name} marshals to {actual} bytes, expected {expected}.");
            }
        }
    }
}

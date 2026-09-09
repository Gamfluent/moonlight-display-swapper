using System.Runtime.InteropServices;
using DisplaySwitcher.Interop;

namespace DisplaySwitcher.Display;

public sealed record DisplayMode(uint Width, uint Height, uint RefreshHz)
{
    public string ResolutionLabel => $"{Width} x {Height}";
    public string RefreshLabel => $"{RefreshHz} Hz";
    public override string ToString() => $"{Width}x{Height} @ {RefreshHz}Hz";
}

/// <summary>
/// Lists the resolutions and refresh rates a monitor advertises.
///
/// This uses the old GDI enumeration rather than the CCD API on purpose: EnumDisplaySettingsEx
/// returns the full mode list for a monitor that is connected but currently switched off, which
/// is exactly the case that matters here (configuring a VDD-only streaming profile while the VDD
/// is inactive). Unused GDI display slots report zero modes, which is how phantom entries are
/// filtered out.
/// </summary>
public static class DisplayModeEnumerator
{
    private const uint RequiredBitsPerPixel = 32;

    /// <summary>Enumerates every mode advertised for a GDI device name such as <c>\\.\DISPLAY1</c>.</summary>
    public static IReadOnlyList<DisplayMode> ForGdiDevice(string gdiDeviceName)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
        {
            return Array.Empty<DisplayMode>();
        }

        var seen = new HashSet<DisplayMode>();
        var devModeSize = (ushort)Marshal.SizeOf<DEVMODE>();

        for (var index = 0; index < 20000; index++)
        {
            var devMode = new DEVMODE
            {
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
                dmSize = devModeSize
            };

            if (!GdiNative.EnumDisplaySettingsEx(gdiDeviceName, index, ref devMode, 0))
            {
                break;
            }

            if (devMode.dmBitsPerPel != RequiredBitsPerPixel ||
                devMode.dmPelsWidth == 0 ||
                devMode.dmPelsHeight == 0)
            {
                continue;
            }

            seen.Add(new DisplayMode(devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency));
        }

        return seen
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Width)
            .ThenByDescending(m => m.RefreshHz)
            .ToList();
    }

    /// <summary>Distinct resolutions, largest first.</summary>
    public static IReadOnlyList<(uint Width, uint Height)> Resolutions(IEnumerable<DisplayMode> modes) =>
        modes
            .Select(m => (m.Width, m.Height))
            .Distinct()
            .OrderByDescending(r => (long)r.Width * r.Height)
            .ThenByDescending(r => r.Width)
            .ToList();

    /// <summary>Refresh rates available at a given resolution, highest first.</summary>
    public static IReadOnlyList<uint> RefreshRates(IEnumerable<DisplayMode> modes, uint width, uint height) =>
        modes
            .Where(m => m.Width == width && m.Height == height)
            .Select(m => m.RefreshHz)
            .Distinct()
            .OrderByDescending(hz => hz)
            .ToList();

    /// <summary>The mode Windows currently has registered for this device, if any.</summary>
    public static DisplayMode? Current(string gdiDeviceName)
    {
        if (string.IsNullOrWhiteSpace(gdiDeviceName))
        {
            return null;
        }

        var devMode = new DEVMODE
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DEVMODE>()
        };

        if (!GdiNative.EnumDisplaySettingsEx(gdiDeviceName, GdiNative.ENUM_CURRENT_SETTINGS, ref devMode, 0) ||
            devMode.dmPelsWidth == 0)
        {
            return null;
        }

        return new DisplayMode(devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency);
    }
}

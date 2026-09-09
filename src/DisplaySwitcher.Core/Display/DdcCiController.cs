using System.Runtime.InteropServices;
using DisplaySwitcher.Config;
using DisplaySwitcher.Interop;
using DisplaySwitcher.Logging;

namespace DisplaySwitcher.Display;

/// <summary>
/// Last-resort escalation: drives VCP 0xD6 (DPMS power mode) over DDC/CI to force a monitor
/// through a real firmware power cycle, which is what triggers a clean EDID re-handshake.
/// This sits outside the Windows display-path API entirely.
/// </summary>
public sealed class DdcCiController
{
    private readonly AppSettings _settings;
    private readonly RunLogger _log;

    public DdcCiController(AppSettings settings, RunLogger log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Power-cycles the monitor behind <paramref name="devicePath"/>. Returns false when no DDC/CI
    /// handle could be obtained, which is expected for a monitor whose display path is inactive.
    /// </summary>
    public bool TryPowerCycle(string devicePath, LiveTopology topology)
    {
        var gdiName = ResolveGdiDeviceName(devicePath, topology);

        if (gdiName is null)
        {
            _log.Warn($"    DDC/CI: no GDI device name for {devicePath} - the path is not active, " +
                      "so Windows exposes no monitor handle to talk to. Skipping escalation for this monitor.");
            return false;
        }

        var hMonitor = FindHMonitor(gdiName);
        if (hMonitor == IntPtr.Zero)
        {
            _log.Warn($"    DDC/CI: no HMONITOR found for {gdiName} ({devicePath}). Skipping escalation.");
            return false;
        }

        if (!Dxva2Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
        {
            _log.Warn($"    DDC/CI: GetNumberOfPhysicalMonitorsFromHMONITOR failed for {gdiName} " +
                      $"(win32 {Marshal.GetLastWin32Error()}). Skipping escalation.");
            return false;
        }

        var monitors = new PHYSICAL_MONITOR[count];
        if (!Dxva2Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
        {
            _log.Warn($"    DDC/CI: GetPhysicalMonitorsFromHMONITOR failed for {gdiName} " +
                      $"(win32 {Marshal.GetLastWin32Error()}). Skipping escalation.");
            return false;
        }

        try
        {
            var handle = monitors[0].hPhysicalMonitor;
            var description = monitors[0].szPhysicalMonitorDescription;
            _log.Info($"    DDC/CI: acquired handle for {gdiName} ('{description}').");

            if (Dxva2Native.GetVCPFeatureAndVCPFeatureReply(handle, Dxva2Native.VcpPowerMode, IntPtr.Zero,
                    out var current, out var maximum))
            {
                _log.Info($"    DDC/CI: current VCP 0xD6 power mode is {current} (max {maximum}).");
            }
            else
            {
                _log.Warn($"    DDC/CI: monitor did not answer a VCP 0xD6 read (win32 {Marshal.GetLastWin32Error()}); " +
                          "it may not support DDC/CI. Attempting the power cycle anyway.");
            }

            if (!TrySetPower(handle, Dxva2Native.PowerStandby, "standby (0xD6=4)") &&
                !TrySetPower(handle, Dxva2Native.PowerOff, "off (0xD6=5)"))
            {
                _log.Warn("    DDC/CI: monitor rejected both standby and off commands; escalation had no effect.");
                return false;
            }

            _log.Info($"    DDC/CI: holding powered down for {_settings.DdcCiPowerCycleSeconds}s.");
            Thread.Sleep(TimeSpan.FromSeconds(_settings.DdcCiPowerCycleSeconds));

            if (!TrySetPower(handle, Dxva2Native.PowerOn, "on (0xD6=1)"))
            {
                _log.Error("    DDC/CI: failed to send power-on. The monitor may need a manual power button press.");
                return false;
            }

            _log.Info($"    DDC/CI: waiting {_settings.DdcCiSettleSeconds}s for the EDID handshake to settle.");
            Thread.Sleep(TimeSpan.FromSeconds(_settings.DdcCiSettleSeconds));
            return true;
        }
        finally
        {
            Dxva2Native.DestroyPhysicalMonitors(count, monitors);
        }
    }

    private bool TrySetPower(IntPtr handle, uint value, string description)
    {
        if (Dxva2Native.SetVCPFeature(handle, Dxva2Native.VcpPowerMode, value))
        {
            _log.Info($"    DDC/CI: sent power {description}.");
            return true;
        }

        _log.Warn($"    DDC/CI: power {description} was rejected (win32 {Marshal.GetLastWin32Error()}).");
        return false;
    }

    /// <summary>
    /// DDC/CI is addressed by HMONITOR, which only exists for monitors currently painting a desktop.
    /// The bridge from our stable device path to that handle is the GDI device name on the active path.
    /// </summary>
    private static string? ResolveGdiDeviceName(string devicePath, LiveTopology topology)
    {
        var match = topology.Paths
            .Where(p => p.IsActive && p.GdiDeviceName.Length > 0)
            .FirstOrDefault(p => MonitorIdentity.SameMonitor(p.DevicePath, devicePath));

        return match?.GdiDeviceName;
    }

    private static IntPtr FindHMonitor(string gdiDeviceName)
    {
        var found = IntPtr.Zero;

        Dxva2Native.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty
            };

            if (Dxva2Native.GetMonitorInfo(hMonitor, ref info) &&
                string.Equals(info.szDevice?.Trim(), gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = hMonitor;
                return false;
            }

            return true;
        };

        Dxva2Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }
}

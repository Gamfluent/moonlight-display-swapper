using DisplaySwitcher.Interop;
using DisplaySwitcher.Logging;
using DisplaySwitcher.Models;

namespace DisplaySwitcher.Display;

/// <summary>A connected monitor plus the modes its driver advertises.</summary>
public sealed record MonitorInventoryItem(MonitorSnapshot Current, IReadOnlyList<DisplayMode> Modes)
{
    public string DevicePath => Current.DevicePath;
    public string FriendlyName => Current.FriendlyName;
    public bool IsActive => Current.Active;
}

/// <summary>
/// Combines the CCD topology (stable identity, exact timings) with the GDI mode list
/// (what resolutions the panel will accept). The GUI needs both to offer a real editor.
/// </summary>
public static class MonitorInventory
{
    public static IReadOnlyList<MonitorInventoryItem> Read(RunLogger log)
    {
        var reader = new TopologyReader(log);
        var topology = reader.Read(CcdNative.QDC_ALL_PATHS);
        var snapshot = TopologyReader.ToSnapshot("live", topology);

        return snapshot.Monitors
            .Select(monitor => new MonitorInventoryItem(
                monitor,
                DisplayModeEnumerator.ForGdiDevice(monitor.GdiDeviceName)))
            .ToList();
    }
}

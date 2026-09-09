using System.Text.Json.Serialization;

namespace DisplaySwitcher.Models;

/// <summary>
/// A saved display topology. Monitors are keyed by PnP device instance path rather than adapter LUID,
/// because LUIDs are reassigned across reboots and driver reloads.
/// </summary>
public sealed class DisplaySnapshot
{
    public string Name { get; set; } = string.Empty;
    public DateTime CapturedUtc { get; set; }
    public List<MonitorSnapshot> Monitors { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<MonitorSnapshot> ExpectedActive => Monitors.Where(m => m.Active);

    [JsonIgnore]
    public IEnumerable<MonitorSnapshot> ExpectedInactive => Monitors.Where(m => !m.Active);
}

public sealed class MonitorSnapshot
{
    /// <summary>Stable key, e.g. <c>DISPLAY\MTT1337\1&amp;33320f0&amp;0&amp;UID256</c>.</summary>
    public string DevicePath { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public bool Active { get; set; }

    /// <summary>True when the source mode sits at the desktop origin.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>GDI source (view) index on its adapter. Stable per adapter; re-paired with a live LUID on apply.</summary>
    public uint SourceId { get; set; }

    /// <summary>Target id recorded at capture time. Recorded for diagnostics; always re-resolved before apply.</summary>
    public uint TargetId { get; set; }

    /// <summary>Adapter LUID at capture time, as text. Diagnostics only - never used to build a call.</summary>
    public string CapturedAdapterId { get; set; } = string.Empty;

    /// <summary>GDI device name such as <c>\\.\DISPLAY1</c> at capture time. Diagnostics only.</summary>
    public string GdiDeviceName { get; set; } = string.Empty;

    public uint OutputTechnology { get; set; }
    public uint Rotation { get; set; }
    public uint Scaling { get; set; }
    public uint RefreshNumerator { get; set; }
    public uint RefreshDenominator { get; set; }
    public uint ScanLineOrdering { get; set; }
    public bool TargetAvailable { get; set; }
    public uint PathFlags { get; set; }

    public SourceModeSnapshot? SourceMode { get; set; }
    public VideoSignalSnapshot? TargetMode { get; set; }

    [JsonIgnore]
    public double RefreshHz => RefreshDenominator == 0 ? 0d : (double)RefreshNumerator / RefreshDenominator;

    [JsonIgnore]
    public string Label =>
        string.IsNullOrWhiteSpace(FriendlyName) ? DevicePath : $"{FriendlyName} [{DevicePath}]";

    public string DescribeMode()
    {
        if (!Active || SourceMode is null)
        {
            return "inactive";
        }

        return $"{SourceMode.Width}x{SourceMode.Height} @ {RefreshHz:0.###}Hz " +
               $"at ({SourceMode.PositionX},{SourceMode.PositionY}){(IsPrimary ? " [primary]" : string.Empty)}";
    }
}

public sealed class SourceModeSnapshot
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint PixelFormat { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
}

/// <summary>
/// Verbatim DISPLAYCONFIG_VIDEO_SIGNAL_INFO, so a restore can request the exact original timing
/// instead of letting Windows pick a nearest match.
/// </summary>
public sealed class VideoSignalSnapshot
{
    public ulong PixelRate { get; set; }
    public uint HSyncNumerator { get; set; }
    public uint HSyncDenominator { get; set; }
    public uint VSyncNumerator { get; set; }
    public uint VSyncDenominator { get; set; }
    public uint ActiveWidth { get; set; }
    public uint ActiveHeight { get; set; }
    public uint TotalWidth { get; set; }
    public uint TotalHeight { get; set; }
    public uint VideoStandard { get; set; }
    public uint ScanLineOrdering { get; set; }
}

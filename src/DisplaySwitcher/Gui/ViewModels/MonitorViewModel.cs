using System.Collections.ObjectModel;
using DisplaySwitcher.Display;
using DisplaySwitcher.Models;

namespace DisplaySwitcher.Gui.ViewModels;

public sealed record ResolutionOption(uint Width, uint Height)
{
    public string Label => $"{Width} \u00d7 {Height}";
}

/// <summary>
/// One monitor as presented in the arrangement editor. Holds both the user's chosen layout and
/// the exact timing captured from the live topology, so an unchanged display can be re-applied
/// with its original DISPLAYCONFIG_VIDEO_SIGNAL_INFO rather than a driver approximation.
/// </summary>
public sealed class MonitorViewModel : ObservableBase
{
    private bool _isActive;
    private bool _isPrimary;
    private int _positionX;
    private int _positionY;
    private ResolutionOption? _selectedResolution;
    private uint _selectedRefreshHz;
    private bool _isSelected;
    private bool _suppressCascade;

    public required string DevicePath { get; init; }
    public required string FriendlyName { get; init; }
    public required string GdiDeviceName { get; init; }

    /// <summary>False when the profile references a monitor that is not plugged in right now.</summary>
    public required bool IsConnected { get; init; }

    /// <summary>The 1-based badge shown on the arrangement canvas, matching the Windows convention.</summary>
    public int Number { get; set; }

    public IReadOnlyList<DisplayMode> AvailableModes { get; init; } = Array.Empty<DisplayMode>();
    public ObservableCollection<ResolutionOption> Resolutions { get; } = new();
    public ObservableCollection<uint> RefreshRates { get; } = new();

    // Everything below is carried through from the captured topology so the applier can rebuild
    // an identical path. None of it is user-editable.
    public uint SourceId { get; set; }
    public uint OutputTechnology { get; set; }
    public uint Rotation { get; set; } = 1;
    public uint Scaling { get; set; } = 1;
    public uint ScanLineOrdering { get; set; } = 1;
    public uint PixelFormat { get; set; } = 4;
    public uint PathFlags { get; set; }
    public uint CapturedRefreshNumerator { get; set; }
    public uint CapturedRefreshDenominator { get; set; }
    public VideoSignalSnapshot? CapturedTargetMode { get; set; }
    public uint CapturedWidth { get; set; }
    public uint CapturedHeight { get; set; }

    public string Label => string.IsNullOrWhiteSpace(FriendlyName) ? DevicePath : FriendlyName;

    public string DetailLine => IsConnected
        ? $"{DevicePath}"
        : $"{DevicePath}  (not connected)";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value))
            {
                Raise(nameof(StatusText));
            }
        }
    }

    public bool IsPrimary
    {
        get => _isPrimary;
        set => Set(ref _isPrimary, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public int PositionX
    {
        get => _positionX;
        set => Set(ref _positionX, value);
    }

    public int PositionY
    {
        get => _positionY;
        set => Set(ref _positionY, value);
    }

    public ResolutionOption? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (!Set(ref _selectedResolution, value) || _suppressCascade)
            {
                return;
            }

            RebuildRefreshRates();
            Raise(nameof(Width));
            Raise(nameof(Height));
            Raise(nameof(StatusText));
        }
    }

    public uint SelectedRefreshHz
    {
        get => _selectedRefreshHz;
        set
        {
            if (Set(ref _selectedRefreshHz, value))
            {
                Raise(nameof(StatusText));
            }
        }
    }

    public uint Width => SelectedResolution?.Width ?? CapturedWidth;
    public uint Height => SelectedResolution?.Height ?? CapturedHeight;

    public string StatusText => IsActive
        ? $"{Width} \u00d7 {Height} at {SelectedRefreshHz} Hz"
        : "Not in use";

    public void LoadModes()
    {
        Resolutions.Clear();

        foreach (var resolution in DisplayModeEnumerator.Resolutions(AvailableModes))
        {
            Resolutions.Add(new ResolutionOption(resolution.Width, resolution.Height));
        }
    }

    /// <summary>Picks the resolution/refresh entries matching the given mode, without cascading edits.</summary>
    public void SelectMode(uint width, uint height, uint refreshHz)
    {
        _suppressCascade = true;

        var match = Resolutions.FirstOrDefault(r => r.Width == width && r.Height == height);

        if (match is null && width > 0 && height > 0)
        {
            // The saved profile references a mode the driver no longer advertises. Keep it visible
            // rather than silently snapping the user to something else.
            match = new ResolutionOption(width, height);
            Resolutions.Insert(0, match);
        }

        _selectedResolution = match ?? Resolutions.FirstOrDefault();
        Raise(nameof(SelectedResolution));
        _suppressCascade = false;

        RebuildRefreshRates(preferred: refreshHz);
        Raise(nameof(Width));
        Raise(nameof(Height));
        Raise(nameof(StatusText));
    }

    private void RebuildRefreshRates(uint preferred = 0)
    {
        var wanted = preferred != 0 ? preferred : SelectedRefreshHz;

        RefreshRates.Clear();

        if (SelectedResolution is { } resolution)
        {
            foreach (var hz in DisplayModeEnumerator.RefreshRates(AvailableModes, resolution.Width, resolution.Height))
            {
                RefreshRates.Add(hz);
            }
        }

        if (wanted != 0 && !RefreshRates.Contains(wanted))
        {
            RefreshRates.Insert(0, wanted);
        }

        SelectedRefreshHz = RefreshRates.Contains(wanted) && wanted != 0
            ? wanted
            : RefreshRates.FirstOrDefault();
    }

    /// <summary>
    /// True when the chosen mode is identical to what was captured, meaning the exact original
    /// pixel timing can be replayed instead of letting the driver derive one.
    /// </summary>
    public bool MatchesCapturedMode()
    {
        if (CapturedTargetMode is null || CapturedRefreshDenominator == 0)
        {
            return false;
        }

        if (Width != CapturedWidth || Height != CapturedHeight)
        {
            return false;
        }

        var capturedHz = (double)CapturedRefreshNumerator / CapturedRefreshDenominator;
        return Math.Abs(capturedHz - SelectedRefreshHz) < 1.0;
    }

    public MonitorSnapshot ToSnapshot()
    {
        var reuseExactTiming = IsActive && MatchesCapturedMode();

        return new MonitorSnapshot
        {
            DevicePath = DevicePath,
            FriendlyName = FriendlyName,
            Active = IsActive,
            IsPrimary = IsActive && IsPrimary,
            SourceId = SourceId,
            GdiDeviceName = GdiDeviceName,
            OutputTechnology = OutputTechnology,
            Rotation = Rotation == 0 ? 1 : Rotation,
            Scaling = Scaling == 0 ? 1 : Scaling,
            ScanLineOrdering = ScanLineOrdering,
            TargetAvailable = true,
            PathFlags = IsActive ? 1u : 0u,

            // A driver-derived timing is requested by leaving the target mode null and giving the
            // refresh rate as a whole number; this is what makes picking a brand new resolution work.
            RefreshNumerator = reuseExactTiming ? CapturedRefreshNumerator : (IsActive ? SelectedRefreshHz : 0),
            RefreshDenominator = reuseExactTiming ? CapturedRefreshDenominator : (IsActive ? 1u : 0u),
            TargetMode = reuseExactTiming ? CapturedTargetMode : null,

            SourceMode = IsActive
                ? new SourceModeSnapshot
                {
                    Width = Width,
                    Height = Height,
                    PixelFormat = PixelFormat == 0 ? 4 : PixelFormat,
                    PositionX = PositionX,
                    PositionY = PositionY
                }
                : null
        };
    }
}

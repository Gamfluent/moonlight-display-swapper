using System.Collections.ObjectModel;
using DisplaySwitcher.Config;
using DisplaySwitcher.Display;
using DisplaySwitcher.Logging;
using DisplaySwitcher.Models;
using DisplaySwitcher.Profiles;

namespace DisplaySwitcher.Gui.ViewModels;

public sealed class ProfileOption
{
    public required string Key { get; init; }
    public required string Display { get; init; }
    public override string ToString() => Display;
}

public sealed class ProfileEditorViewModel : ObservableBase
{
    private readonly RunLogger _log;
    private MonitorViewModel? _selectedMonitor;
    private ProfileOption _currentProfile;
    private string _statusMessage = string.Empty;
    private string _statusSeverity = "info";
    private bool _isBusy;

    public ProfileEditorViewModel(RunLogger log)
    {
        _log = log;

        Profiles = new ObservableCollection<ProfileOption>
        {
            new() { Key = ProfileStore.Streaming, Display = ProfileStore.FriendlyName(ProfileStore.Streaming) },
            new() { Key = ProfileStore.Desktop, Display = ProfileStore.FriendlyName(ProfileStore.Desktop) }
        };

        _currentProfile = Profiles[0];
    }

    public ObservableCollection<ProfileOption> Profiles { get; }
    public ObservableCollection<MonitorViewModel> Monitors { get; } = new();

    public ObservableCollection<MonitorViewModel> ActiveMonitors { get; } = new();
    public ObservableCollection<MonitorViewModel> InactiveMonitors { get; } = new();

    /// <summary>
    /// Which profile the editor is showing. Setting this does not reload on its own; the view
    /// calls <see cref="LoadProfile"/> so the first load and every later switch take one path.
    /// </summary>
    public ProfileOption CurrentProfile
    {
        get => _currentProfile;
        set => Set(ref _currentProfile, value);
    }

    public MonitorViewModel? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (_selectedMonitor is not null)
            {
                _selectedMonitor.IsSelected = false;
            }

            if (Set(ref _selectedMonitor, value) && value is not null)
            {
                value.IsSelected = true;
            }

            Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => SelectedMonitor is not null;

    public bool HasInactive => InactiveMonitors.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string StatusSeverity
    {
        get => _statusSeverity;
        private set => Set(ref _statusSeverity, value);
    }

    public event EventHandler? LayoutChanged;

    public void SetStatus(string message, string severity = "info")
    {
        StatusMessage = message;
        StatusSeverity = severity;
    }

    /// <summary>
    /// Rebuilds the editor from the live hardware, then overlays whatever the saved profile says.
    /// Monitors named in the profile but currently unplugged are kept visible and disabled.
    /// </summary>
    public void LoadProfile(string profileKey)
    {
        var inventory = MonitorInventory.Read(_log);
        var saved = ProfileStore.TryLoad(profileKey, out _);

        Monitors.Clear();

        foreach (var item in inventory)
        {
            var current = item.Current;
            var savedEntry = saved?.Monitors.FirstOrDefault(m => MonitorIdentity.SameMonitor(m.DevicePath, current.DevicePath));

            var vm = new MonitorViewModel
            {
                DevicePath = current.DevicePath,
                FriendlyName = current.FriendlyName,
                GdiDeviceName = current.GdiDeviceName,
                IsConnected = true,
                AvailableModes = item.Modes,
                SourceId = savedEntry?.SourceId ?? current.SourceId,
                OutputTechnology = current.OutputTechnology,
                Rotation = current.Rotation,
                Scaling = current.Scaling,
                ScanLineOrdering = current.ScanLineOrdering,
                PathFlags = current.PathFlags,
                PixelFormat = current.SourceMode?.PixelFormat ?? 4,
                CapturedWidth = current.SourceMode?.Width ?? 0,
                CapturedHeight = current.SourceMode?.Height ?? 0,
                CapturedRefreshNumerator = current.RefreshNumerator,
                CapturedRefreshDenominator = current.RefreshDenominator,
                CapturedTargetMode = current.TargetMode
            };

            vm.LoadModes();
            ApplySavedState(vm, savedEntry, current, item.Modes);
            Monitors.Add(vm);
        }

        // Anything the profile expects but that is not plugged in right now.
        if (saved is not null)
        {
            foreach (var orphan in saved.Monitors)
            {
                if (Monitors.Any(m => MonitorIdentity.SameMonitor(m.DevicePath, orphan.DevicePath)))
                {
                    continue;
                }

                var vm = new MonitorViewModel
                {
                    DevicePath = orphan.DevicePath,
                    FriendlyName = orphan.FriendlyName,
                    GdiDeviceName = orphan.GdiDeviceName,
                    IsConnected = false,
                    AvailableModes = Array.Empty<DisplayMode>(),
                    SourceId = orphan.SourceId,
                    OutputTechnology = orphan.OutputTechnology,
                    Rotation = orphan.Rotation,
                    Scaling = orphan.Scaling,
                    ScanLineOrdering = orphan.ScanLineOrdering,
                    PathFlags = orphan.PathFlags,
                    CapturedWidth = orphan.SourceMode?.Width ?? 0,
                    CapturedHeight = orphan.SourceMode?.Height ?? 0,
                    CapturedRefreshNumerator = orphan.RefreshNumerator,
                    CapturedRefreshDenominator = orphan.RefreshDenominator,
                    CapturedTargetMode = orphan.TargetMode
                };

                vm.LoadModes();
                vm.IsActive = false;
                vm.SelectMode(orphan.SourceMode?.Width ?? 0, orphan.SourceMode?.Height ?? 0, (uint)Math.Round(orphan.RefreshHz));
                Monitors.Add(vm);
            }
        }

        Renumber();
        RefreshBuckets();

        SelectedMonitor = Monitors.FirstOrDefault(m => m.IsActive) ?? Monitors.FirstOrDefault();

        SetStatus(saved is null
            ? $"No '{profileKey}' profile saved yet. Arrange the displays you want and press Save."
            : $"Loaded the {ProfileStore.FriendlyName(profileKey).ToLowerInvariant()} profile.");

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplySavedState(
        MonitorViewModel vm,
        MonitorSnapshot? savedEntry,
        MonitorSnapshot current,
        IReadOnlyList<DisplayMode> modes)
    {
        var source = savedEntry ?? current;

        vm.IsActive = savedEntry?.Active ?? current.Active;
        vm.IsPrimary = source.IsPrimary;
        vm.PositionX = source.SourceMode?.PositionX ?? 0;
        vm.PositionY = source.SourceMode?.PositionY ?? 0;

        var width = source.SourceMode?.Width ?? current.SourceMode?.Width ?? 0;
        var height = source.SourceMode?.Height ?? current.SourceMode?.Height ?? 0;
        var hz = (uint)Math.Round(source.RefreshHz > 0 ? source.RefreshHz : current.RefreshHz);

        // Fall back to the best advertised mode when nothing usable was recorded, which is what
        // happens for a display that has never been switched on in this profile.
        if ((width == 0 || height == 0) && modes.Count > 0)
        {
            width = modes[0].Width;
            height = modes[0].Height;
            hz = modes[0].RefreshHz;
        }

        vm.SelectMode(width, height, hz);
    }

    /// <summary>Replaces the editor contents with exactly what is on screen right now.</summary>
    public void ImportLive()
    {
        var inventory = MonitorInventory.Read(_log);

        foreach (var vm in Monitors)
        {
            var live = inventory.FirstOrDefault(i => MonitorIdentity.SameMonitor(i.DevicePath, vm.DevicePath));

            if (live is null)
            {
                vm.IsActive = false;
                continue;
            }

            vm.IsActive = live.Current.Active;
            vm.IsPrimary = live.Current.IsPrimary;
            vm.PositionX = live.Current.SourceMode?.PositionX ?? 0;
            vm.PositionY = live.Current.SourceMode?.PositionY ?? 0;
            vm.CapturedWidth = live.Current.SourceMode?.Width ?? 0;
            vm.CapturedHeight = live.Current.SourceMode?.Height ?? 0;
            vm.CapturedRefreshNumerator = live.Current.RefreshNumerator;
            vm.CapturedRefreshDenominator = live.Current.RefreshDenominator;
            vm.CapturedTargetMode = live.Current.TargetMode;

            if (live.Current.SourceMode is { } mode)
            {
                vm.SelectMode(mode.Width, mode.Height, (uint)Math.Round(live.Current.RefreshHz));
            }
        }

        Renumber();
        RefreshBuckets();
        SetStatus("Imported the layout that is on screen right now. Press Save to keep it.");
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPrimary(MonitorViewModel monitor)
    {
        foreach (var m in Monitors)
        {
            m.IsPrimary = ReferenceEquals(m, monitor);
        }
    }

    public void ToggleActive(MonitorViewModel monitor, bool active)
    {
        if (active && !monitor.IsConnected)
        {
            SetStatus($"{monitor.Label} is not connected, so it cannot be switched on.", "error");
            return;
        }

        monitor.IsActive = active;

        if (!active && monitor.IsPrimary)
        {
            monitor.IsPrimary = false;
            var replacement = Monitors.FirstOrDefault(m => m.IsActive);
            if (replacement is not null)
            {
                replacement.IsPrimary = true;
            }
        }

        if (active && !Monitors.Any(m => m.IsActive && m.IsPrimary))
        {
            monitor.IsPrimary = true;
        }

        if (active)
        {
            PlaceBesideExisting(monitor);
        }

        Renumber();
        RefreshBuckets();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops a newly enabled display to the right of everything else so it is never stranded.</summary>
    private void PlaceBesideExisting(MonitorViewModel monitor)
    {
        var others = Monitors.Where(m => m.IsActive && !ReferenceEquals(m, monitor)).ToList();

        if (others.Count == 0)
        {
            monitor.PositionX = 0;
            monitor.PositionY = 0;
            return;
        }

        monitor.PositionX = others.Max(m => m.PositionX + (int)m.Width);
        monitor.PositionY = others.Min(m => m.PositionY);
    }

    public void Renumber()
    {
        var index = 1;

        foreach (var monitor in Monitors.Where(m => m.IsActive).OrderBy(m => m.PositionX).ThenBy(m => m.PositionY))
        {
            monitor.Number = index++;
        }

        foreach (var monitor in Monitors.Where(m => !m.IsActive))
        {
            monitor.Number = index++;
        }
    }

    public void RefreshBuckets()
    {
        ActiveMonitors.Clear();
        InactiveMonitors.Clear();

        foreach (var monitor in Monitors.Where(m => m.IsActive))
        {
            ActiveMonitors.Add(monitor);
        }

        foreach (var monitor in Monitors.Where(m => !m.IsActive))
        {
            InactiveMonitors.Add(monitor);
        }

        Raise(nameof(HasInactive));
    }

    public DisplaySnapshot BuildSnapshot()
    {
        var snapshot = new DisplaySnapshot
        {
            Name = CurrentProfile.Key,
            CapturedUtc = DateTime.UtcNow,
            Monitors = Monitors.Select(m => m.ToSnapshot()).ToList()
        };

        LayoutValidator.Normalize(snapshot);
        return snapshot;
    }

    public IReadOnlyList<LayoutIssue> Validate() => LayoutValidator.Validate(BuildSnapshot());

    public bool Save()
    {
        var snapshot = BuildSnapshot();
        var issues = LayoutValidator.Validate(snapshot);
        var errors = issues.Where(i => i.IsError).ToList();

        if (errors.Count > 0)
        {
            SetStatus(errors[0].Message, "error");
            return false;
        }

        ProfileStore.Save(snapshot);

        // Normalization may have shifted everything; mirror that back into the editor.
        foreach (var monitor in Monitors)
        {
            var saved = snapshot.Monitors.FirstOrDefault(m => MonitorIdentity.SameMonitor(m.DevicePath, monitor.DevicePath));
            if (saved?.SourceMode is { } mode)
            {
                monitor.PositionX = mode.PositionX;
                monitor.PositionY = mode.PositionY;
            }

            monitor.IsPrimary = saved?.IsPrimary ?? false;
        }

        Renumber();
        LayoutChanged?.Invoke(this, EventArgs.Empty);

        var warning = issues.FirstOrDefault(i => !i.IsError);
        SetStatus(warning is null
                ? $"Saved to {AppPaths.ProfileFile(snapshot.Name)}"
                : $"Saved, but note: {warning.Message}",
            warning is null ? "success" : "warning");

        return true;
    }

    /// <summary>Applies the profile for real so the user can see it, using the full verify/retry path.</summary>
    public int Preview(AppSettings settings)
    {
        var snapshot = BuildSnapshot();
        var errors = LayoutValidator.Validate(snapshot).Where(i => i.IsError).ToList();

        if (errors.Count > 0)
        {
            SetStatus(errors[0].Message, "error");
            return ExitCodes.BadArguments;
        }

        var applier = new TopologyApplier(settings, _log);
        return applier.Apply(snapshot);
    }
}

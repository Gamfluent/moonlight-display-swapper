using DisplaySwitcher.Config;
using DisplaySwitcher.Interop;
using DisplaySwitcher.Logging;
using DisplaySwitcher.Models;

namespace DisplaySwitcher.Display;

public sealed class TopologyApplier
{
    private const uint ApplyFlags =
        CcdNative.SDC_APPLY |
        CcdNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
        CcdNative.SDC_ALLOW_CHANGES |
        CcdNative.SDC_SAVE_TO_DATABASE;

    private const uint ValidateFlags =
        CcdNative.SDC_VALIDATE |
        CcdNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
        CcdNative.SDC_ALLOW_CHANGES;

    private readonly AppSettings _settings;
    private readonly RunLogger _log;
    private readonly TopologyReader _reader;

    public TopologyApplier(AppSettings settings, RunLogger log)
    {
        _settings = settings;
        _log = log;
        _reader = new TopologyReader(log);
    }

    public int Apply(DisplaySnapshot snapshot)
    {
        _log.Info($"Applying snapshot '{snapshot.Name}' captured {snapshot.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC.");
        LogIntent(snapshot);

        for (var attempt = 1; attempt <= _settings.MaxRetries; attempt++)
        {
            _log.Info($"--- Attempt {attempt} of {_settings.MaxRetries} ---");

            if (!TryAttempt(snapshot, attempt, out var failures, out var fatal))
            {
                if (fatal)
                {
                    return ExitCodeFor(failures);
                }
            }
            else
            {
                _log.Info($"SUCCESS: snapshot '{snapshot.Name}' is live and verified.");
                return ExitCodes.Success;
            }

            if (attempt < _settings.MaxRetries)
            {
                _log.Warn($"Verification failed; waiting {_settings.RetryDelaySeconds}s before retrying.");
                Thread.Sleep(TimeSpan.FromSeconds(_settings.RetryDelaySeconds));
            }
        }

        _log.Warn($"All {_settings.MaxRetries} attempts failed verification.");

        if (Escalate(snapshot))
        {
            _log.Info($"SUCCESS: snapshot '{snapshot.Name}' verified after DDC/CI escalation.");
            return ExitCodes.Success;
        }

        _log.Error($"FAILURE: snapshot '{snapshot.Name}' could not be verified. Reporting failure rather than a false success.");
        return ExitCodes.VerificationFailed;
    }

    private void LogIntent(DisplaySnapshot snapshot)
    {
        _log.Info("Expected ACTIVE monitors:");
        foreach (var monitor in snapshot.ExpectedActive)
        {
            _log.Info($"  + {monitor.Label} -> {monitor.DescribeMode()}");
        }

        var inactive = snapshot.ExpectedInactive.ToList();
        if (inactive.Count == 0)
        {
            _log.Info("Expected INACTIVE monitors: (none)");
            return;
        }

        _log.Info("Expected INACTIVE monitors (sent as explicit inactive paths, not omitted):");
        foreach (var monitor in inactive)
        {
            _log.Info($"  - {monitor.Label}");
        }
    }

    /// <summary>
    /// One resolve/build/apply/verify cycle. <paramref name="fatal"/> marks conditions that retrying
    /// cannot fix, such as a monitor that is physically absent from the live topology.
    /// </summary>
    private bool TryAttempt(DisplaySnapshot snapshot, int attempt, out List<string> failedDevicePaths, out bool fatal)
    {
        failedDevicePaths = new List<string>();
        fatal = false;

        var live = _reader.Read(CcdNative.QDC_ALL_PATHS);

        if (!TryResolve(snapshot, live, out var resolved, out var missing))
        {
            foreach (var name in missing)
            {
                _log.Error($"  Monitor not present in the live topology: {name}");
            }

            _log.Error("Cannot build a display configuration while a display that should be ON is missing. " +
                       "Check the cable/power, or re-save the profile if the hardware changed.");
            failedDevicePaths = missing;
            fatal = true;
            return false;
        }

        var (paths, modes) = BuildRequest(resolved);

        _log.Info($"  Built request: {paths.Length} paths ({paths.Count(p => (p.flags & CcdNative.DISPLAYCONFIG_PATH_ACTIVE) != 0)} active), {modes.Length} modes.");

        var validation = CcdNative.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, ValidateFlags);
        _log.Info($"  SDC_VALIDATE returned {CcdNative.DescribeError(validation)}");

        if (validation != CcdNative.ERROR_SUCCESS)
        {
            _log.Warn("  Validation rejected the configuration; applying anyway so the driver's own error is recorded.");
        }

        var applied = CcdNative.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, ApplyFlags);
        _log.Info($"  SDC_APPLY returned {CcdNative.DescribeError(applied)}");

        if (applied != CcdNative.ERROR_SUCCESS)
        {
            _log.Warn("  SetDisplayConfig reported failure. Verifying the live topology regardless, " +
                      "because the API return code alone is not trustworthy in either direction.");
        }

        _log.Info($"  Waiting {_settings.VerifyDelaySeconds}s before verification.");
        Thread.Sleep(TimeSpan.FromSeconds(_settings.VerifyDelaySeconds));

        var verified = Verify(snapshot, attempt, out failedDevicePaths);
        return verified;
    }

    private bool TryResolve(
        DisplaySnapshot snapshot,
        LiveTopology live,
        out List<ResolvedMonitor> resolved,
        out List<string> missing)
    {
        resolved = new List<ResolvedMonitor>();
        missing = new List<string>();

        foreach (var monitor in snapshot.Monitors)
        {
            var match = live.FindMonitor(monitor.DevicePath);

            if (match is null)
            {
                // A display that should be ON but is absent is unrecoverable. A display that should
                // be OFF and is absent is already in the desired state, so it is simply dropped from
                // the request. Without this distinction, unplugging any unused monitor (an undocked
                // laptop, a powered-off TV) would break every switch.
                if (monitor.Active)
                {
                    missing.Add(monitor.Label);
                }
                else
                {
                    _log.Warn($"  {monitor.Label} should be OFF and is not connected right now; " +
                              "omitting it from the request.");
                }

                continue;
            }

            // Prefer the live path that already pairs this target with the source recorded at capture time.
            var template = live.Paths.FirstOrDefault(p =>
                               p.TargetId == match.TargetId &&
                               p.AdapterId.Equals(match.AdapterId) &&
                               p.SourceId == monitor.SourceId)
                           ?? match;

            _log.Debug($"  Resolved {monitor.Label}: adapter {match.AdapterId} (captured as {monitor.CapturedAdapterId}), " +
                       $"target {match.TargetId} (captured {monitor.TargetId}), source {template.SourceId}.");

            resolved.Add(new ResolvedMonitor(monitor, match.AdapterId, match.TargetId, template));
        }

        return missing.Count == 0;
    }

    /// <summary>
    /// Rebuilds path and mode arrays from scratch against live adapter LUIDs. Monitors that must be
    /// off are included as explicit inactive paths so Windows cannot leave them in a stale on state.
    /// </summary>
    private (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) BuildRequest(
        IReadOnlyList<ResolvedMonitor> resolved)
    {
        var paths = new List<DISPLAYCONFIG_PATH_INFO>(resolved.Count);
        var modes = new List<DISPLAYCONFIG_MODE_INFO>(resolved.Count * 2);
        var sourceModeIndexes = new Dictionary<(LUID Adapter, uint SourceId), uint>();

        foreach (var item in resolved)
        {
            var snapshot = item.Snapshot;

            var path = new DISPLAYCONFIG_PATH_INFO
            {
                sourceInfo = new DISPLAYCONFIG_PATH_SOURCE_INFO
                {
                    adapterId = item.AdapterId,
                    id = snapshot.SourceId,
                    modeInfoIdx = CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID,
                    statusFlags = 0
                },
                targetInfo = new DISPLAYCONFIG_PATH_TARGET_INFO
                {
                    adapterId = item.AdapterId,
                    id = item.TargetId,
                    modeInfoIdx = CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID,
                    outputTechnology = snapshot.OutputTechnology,
                    rotation = snapshot.Rotation == 0 ? CcdNative.DISPLAYCONFIG_ROTATION_IDENTITY : snapshot.Rotation,
                    scaling = snapshot.Scaling == 0 ? CcdNative.DISPLAYCONFIG_SCALING_IDENTITY : snapshot.Scaling,
                    refreshRate = new DISPLAYCONFIG_RATIONAL
                    {
                        Numerator = snapshot.RefreshNumerator,
                        Denominator = snapshot.RefreshDenominator
                    },
                    scanLineOrdering = snapshot.ScanLineOrdering,
                    targetAvailable = 1,
                    statusFlags = 0
                },
                // Rebuild the flag word deterministically: keep captured hints, force the active bit,
                // and drop virtual-mode support so the mode indices stay plain 32-bit values.
                flags = (snapshot.PathFlags &
                         ~(CcdNative.DISPLAYCONFIG_PATH_ACTIVE | CcdNative.DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE)) |
                        (snapshot.Active ? CcdNative.DISPLAYCONFIG_PATH_ACTIVE : 0u)
            };

            if (snapshot.Active && snapshot.SourceMode is { } sourceMode)
            {
                var key = (item.AdapterId, snapshot.SourceId);

                if (!sourceModeIndexes.TryGetValue(key, out var sourceIdx))
                {
                    sourceIdx = (uint)modes.Count;
                    sourceModeIndexes[key] = sourceIdx;

                    modes.Add(new DISPLAYCONFIG_MODE_INFO
                    {
                        infoType = CcdNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
                        id = snapshot.SourceId,
                        adapterId = item.AdapterId,
                        modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
                        {
                            sourceMode = new DISPLAYCONFIG_SOURCE_MODE
                            {
                                width = sourceMode.Width,
                                height = sourceMode.Height,
                                pixelFormat = sourceMode.PixelFormat,
                                position = new POINTL { x = sourceMode.PositionX, y = sourceMode.PositionY }
                            }
                        }
                    });
                }

                path.sourceInfo.modeInfoIdx = sourceIdx;

                if (snapshot.TargetMode is { } targetMode)
                {
                    path.targetInfo.modeInfoIdx = (uint)modes.Count;

                    modes.Add(new DISPLAYCONFIG_MODE_INFO
                    {
                        infoType = CcdNative.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET,
                        id = item.TargetId,
                        adapterId = item.AdapterId,
                        modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
                        {
                            targetMode = new DISPLAYCONFIG_TARGET_MODE
                            {
                                targetVideoSignalInfo = new DISPLAYCONFIG_VIDEO_SIGNAL_INFO
                                {
                                    pixelRate = targetMode.PixelRate,
                                    hSyncFreq = new DISPLAYCONFIG_RATIONAL
                                    {
                                        Numerator = targetMode.HSyncNumerator,
                                        Denominator = targetMode.HSyncDenominator
                                    },
                                    vSyncFreq = new DISPLAYCONFIG_RATIONAL
                                    {
                                        Numerator = targetMode.VSyncNumerator,
                                        Denominator = targetMode.VSyncDenominator
                                    },
                                    activeSize = new DISPLAYCONFIG_2DREGION
                                    {
                                        cx = targetMode.ActiveWidth,
                                        cy = targetMode.ActiveHeight
                                    },
                                    totalSize = new DISPLAYCONFIG_2DREGION
                                    {
                                        cx = targetMode.TotalWidth,
                                        cy = targetMode.TotalHeight
                                    },
                                    videoStandard = targetMode.VideoStandard,
                                    scanLineOrdering = targetMode.ScanLineOrdering
                                }
                            }
                        }
                    });
                }
                else
                {
                    _log.Debug($"  {snapshot.Label} has no captured target mode; " +
                               "letting the driver derive timing from the refresh rate.");
                }
            }

            paths.Add(path);
        }

        return (paths.ToArray(), modes.ToArray());
    }

    /// <summary>
    /// Checks the live topology against what was requested. This is the whole point of the tool:
    /// a successful SetDisplayConfig return code does not mean a monitor actually lit up.
    /// </summary>
    private bool Verify(DisplaySnapshot snapshot, int attempt, out List<string> failedDevicePaths)
    {
        failedDevicePaths = new List<string>();

        LiveTopology live;
        try
        {
            live = _reader.Read(CcdNative.QDC_ONLY_ACTIVE_PATHS);
        }
        catch (DisplayConfigException ex)
        {
            _log.Error($"  Verification pass {attempt} could not read the topology: {ex.Message}");
            failedDevicePaths.AddRange(snapshot.ExpectedActive.Select(m => m.DevicePath));
            return false;
        }

        var activeMonitors = live.Monitors.Where(m => m.IsActive).ToList();
        _log.Info($"  Verification pass {attempt}: {activeMonitors.Count} monitors are currently active.");

        var ok = true;

        foreach (var expected in snapshot.ExpectedActive)
        {
            var match = activeMonitors.FirstOrDefault(m => MonitorIdentity.SameMonitor(m.DevicePath, expected.DevicePath));

            if (match is null)
            {
                _log.Error($"    FAIL {expected.Label}: expected ACTIVE but it has no active path.");
                failedDevicePaths.Add(expected.DevicePath);
                ok = false;
                continue;
            }

            if (match.TargetId == 0)
            {
                _log.Error($"    FAIL {expected.Label}: active path has a zero target id.");
                failedDevicePaths.Add(expected.DevicePath);
                ok = false;
                continue;
            }

            if (match.SourceMode is not { } mode)
            {
                _log.Error($"    FAIL {expected.Label}: active path carries no source mode.");
                failedDevicePaths.Add(expected.DevicePath);
                ok = false;
                continue;
            }

            if (expected.SourceMode is { } wanted && (mode.width != wanted.Width || mode.height != wanted.Height))
            {
                _log.Error($"    FAIL {expected.Label}: expected {wanted.Width}x{wanted.Height} " +
                           $"but the live mode is {mode.width}x{mode.height}.");
                failedDevicePaths.Add(expected.DevicePath);
                ok = false;
                continue;
            }

            var liveHz = match.Path.targetInfo.refreshRate.AsHz();
            if (expected.RefreshHz > 0 && Math.Abs(liveHz - expected.RefreshHz) > 1.0)
            {
                _log.Warn($"    OK(*) {expected.Label}: active at the right resolution, but refresh is " +
                          $"{liveHz:0.###}Hz instead of {expected.RefreshHz:0.###}Hz.");
            }
            else
            {
                _log.Info($"    OK   {expected.Label}: active, target {match.TargetId}, {match.DescribeMode()}");
            }
        }

        foreach (var expected in snapshot.ExpectedInactive)
        {
            var stillOn = activeMonitors.FirstOrDefault(m => MonitorIdentity.SameMonitor(m.DevicePath, expected.DevicePath));

            if (stillOn is not null)
            {
                _log.Error($"    FAIL {expected.Label}: expected INACTIVE but it is still active ({stillOn.DescribeMode()}).");
                ok = false;
            }
            else
            {
                _log.Info($"    OK   {expected.Label}: inactive as expected.");
            }
        }

        return ok;
    }

    /// <summary>
    /// Last resort. Power-cycles the monitors that never came back over DDC/CI, then re-applies
    /// and verifies one final time.
    /// </summary>
    private bool Escalate(DisplaySnapshot snapshot)
    {
        if (!_settings.EnableDdcCiEscalation)
        {
            _log.Warn("DDC/CI escalation is disabled in settings.json; skipping.");
            return false;
        }

        List<string> stubborn;
        try
        {
            var live = _reader.Read(CcdNative.QDC_ONLY_ACTIVE_PATHS);
            var activeMonitors = live.Monitors.Where(m => m.IsActive).ToList();

            stubborn = snapshot.ExpectedActive
                .Where(expected => !activeMonitors.Any(m => MonitorIdentity.SameMonitor(m.DevicePath, expected.DevicePath)))
                .Select(m => m.DevicePath)
                .ToList();

            if (stubborn.Count == 0)
            {
                stubborn = snapshot.ExpectedActive.Select(m => m.DevicePath).ToList();
                _log.Info("Every expected monitor has an active path, so the mismatch is a mode problem rather than " +
                          "a missing signal. Power-cycling all expected-active monitors.");
            }
        }
        catch (DisplayConfigException ex)
        {
            _log.Error($"Could not read topology before escalation: {ex.Message}");
            stubborn = snapshot.ExpectedActive.Select(m => m.DevicePath).ToList();
        }

        _log.Warn($"--- DDC/CI escalation for {stubborn.Count} monitor(s) ---");

        var controller = new DdcCiController(_settings, _log);
        var nudged = false;

        foreach (var devicePath in stubborn)
        {
            var label = snapshot.Monitors.FirstOrDefault(m => MonitorIdentity.SameMonitor(m.DevicePath, devicePath))?.Label
                        ?? devicePath;
            _log.Warn($"  Power-cycling {label}");

            LiveTopology current;
            try
            {
                current = _reader.Read(CcdNative.QDC_ALL_PATHS);
            }
            catch (DisplayConfigException ex)
            {
                _log.Error($"    Could not read topology for DDC/CI correlation: {ex.Message}");
                continue;
            }

            nudged |= controller.TryPowerCycle(devicePath, current);
        }

        if (!nudged)
        {
            _log.Warn("No monitor accepted a DDC/CI power cycle; the final re-apply is unlikely to help, " +
                      "but it will be attempted for completeness.");
        }

        _log.Info("--- Final attempt after escalation ---");
        return TryAttempt(snapshot, _settings.MaxRetries + 1, out _, out _);
    }

    private static int ExitCodeFor(List<string> failures) =>
        failures.Count > 0 ? ExitCodes.MonitorMissing : ExitCodes.VerificationFailed;

    private sealed record ResolvedMonitor(
        MonitorSnapshot Snapshot,
        LUID AdapterId,
        uint TargetId,
        LiveDisplayPath Template);
}

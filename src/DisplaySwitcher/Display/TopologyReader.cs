using System.Runtime.InteropServices;
using DisplaySwitcher.Interop;
using DisplaySwitcher.Logging;
using DisplaySwitcher.Models;

namespace DisplaySwitcher.Display;

/// <summary>A single path returned by QueryDisplayConfig, enriched with monitor identity and modes.</summary>
public sealed class LiveDisplayPath
{
    public required int Index { get; init; }
    public required DISPLAYCONFIG_PATH_INFO Path { get; init; }
    public required string DevicePath { get; init; }
    public required string FriendlyName { get; init; }
    public required string GdiDeviceName { get; init; }
    public DISPLAYCONFIG_SOURCE_MODE? SourceMode { get; init; }
    public DISPLAYCONFIG_VIDEO_SIGNAL_INFO? TargetMode { get; init; }

    public bool IsActive => (Path.flags & CcdNative.DISPLAYCONFIG_PATH_ACTIVE) != 0;
    public LUID AdapterId => Path.targetInfo.adapterId;
    public uint TargetId => Path.targetInfo.id;
    public uint SourceId => Path.sourceInfo.id;
    public bool IsConnectedMonitor => DevicePath.Length > 0;

    public string Label => string.IsNullOrWhiteSpace(FriendlyName) ? DevicePath : $"{FriendlyName} [{DevicePath}]";

    public string DescribeMode()
    {
        if (!IsActive || SourceMode is null)
        {
            return "inactive";
        }

        var mode = SourceMode.Value;
        var primary = mode.position is { x: 0, y: 0 } ? " [primary]" : string.Empty;
        return $"{mode.width}x{mode.height} @ {Path.targetInfo.refreshRate} at ({mode.position.x},{mode.position.y}){primary}";
    }
}

public sealed class LiveTopology
{
    public required DISPLAYCONFIG_PATH_INFO[] RawPaths { get; init; }
    public required DISPLAYCONFIG_MODE_INFO[] RawModes { get; init; }
    public required IReadOnlyList<LiveDisplayPath> Paths { get; init; }

    public IEnumerable<LiveDisplayPath> Monitors => Paths.Where(p => p.IsConnectedMonitor);

    /// <summary>
    /// One entry per physical monitor, preferring its active path when one exists.
    /// QDC_ALL_PATHS returns every source-to-target permutation, so deduplication is required.
    /// </summary>
    public IReadOnlyList<LiveDisplayPath> DistinctMonitors()
    {
        var byDevice = new Dictionary<string, LiveDisplayPath>(MonitorIdentity.Comparer);

        foreach (var path in Monitors)
        {
            if (!byDevice.TryGetValue(path.DevicePath, out var existing))
            {
                byDevice[path.DevicePath] = path;
                continue;
            }

            if (path.IsActive && !existing.IsActive)
            {
                byDevice[path.DevicePath] = path;
            }
        }

        return byDevice.Values
            .OrderByDescending(p => p.IsActive)
            .ThenBy(p => p.SourceId)
            .ToList();
    }

    public LiveDisplayPath? FindMonitor(string devicePath) =>
        DistinctMonitors().FirstOrDefault(p => MonitorIdentity.SameMonitor(p.DevicePath, devicePath));
}

public sealed class TopologyReader
{
    private readonly RunLogger _log;

    public TopologyReader(RunLogger log) => _log = log;

    /// <summary>
    /// Reads the current topology. The buffer size and the query are two separate calls, so a
    /// display change in between yields ERROR_INSUFFICIENT_BUFFER; that case is simply retried.
    /// </summary>
    public LiveTopology Read(uint queryFlags)
    {
        const int maxAttempts = 5;
        var lastError = CcdNative.ERROR_SUCCESS;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sizeResult = CcdNative.GetDisplayConfigBufferSizes(queryFlags, out var pathCount, out var modeCount);
            if (sizeResult != CcdNative.ERROR_SUCCESS)
            {
                throw new DisplayConfigException(
                    $"GetDisplayConfigBufferSizes failed: {CcdNative.DescribeError(sizeResult)}", sizeResult);
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            var queryResult = CcdNative.QueryDisplayConfig(
                queryFlags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);

            if (queryResult == CcdNative.ERROR_SUCCESS)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return Build(paths, modes);
            }

            lastError = queryResult;

            if (queryResult != CcdNative.ERROR_INSUFFICIENT_BUFFER)
            {
                break;
            }

            _log.Debug($"QueryDisplayConfig buffer race on attempt {attempt}; retrying.");
            Thread.Sleep(200);
        }

        throw new DisplayConfigException(
            $"QueryDisplayConfig failed: {CcdNative.DescribeError(lastError)}", lastError);
    }

    private LiveTopology Build(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var enriched = new List<LiveDisplayPath>(paths.Length);

        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            var (devicePath, friendlyName) = QueryTargetName(path.targetInfo.adapterId, path.targetInfo.id);

            enriched.Add(new LiveDisplayPath
            {
                Index = i,
                Path = path,
                DevicePath = devicePath,
                FriendlyName = friendlyName,
                GdiDeviceName = QuerySourceName(path.sourceInfo.adapterId, path.sourceInfo.id),
                SourceMode = ExtractSourceMode(path, modes),
                TargetMode = ExtractTargetMode(path, modes)
            });
        }

        return new LiveTopology { RawPaths = paths, RawModes = modes, Paths = enriched };
    }

    private static DISPLAYCONFIG_SOURCE_MODE? ExtractSourceMode(
        DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var idx = path.sourceInfo.modeInfoIdx;
        if (idx == CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || idx >= modes.Length)
        {
            return null;
        }

        var mode = modes[idx];
        return mode.infoType == CcdNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE ? mode.modeInfo.sourceMode : null;
    }

    private static DISPLAYCONFIG_VIDEO_SIGNAL_INFO? ExtractTargetMode(
        DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var idx = path.targetInfo.modeInfoIdx;
        if (idx == CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || idx >= modes.Length)
        {
            return null;
        }

        var mode = modes[idx];
        return mode.infoType == CcdNative.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET
            ? mode.modeInfo.targetMode.targetVideoSignalInfo
            : null;
    }

    private (string DevicePath, string FriendlyName) QueryTargetName(LUID adapterId, uint targetId)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = CcdNative.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };

        var result = CcdNative.DisplayConfigGetDeviceInfo(ref request);
        if (result != CcdNative.ERROR_SUCCESS)
        {
            _log.Debug($"GET_TARGET_NAME failed for adapter {adapterId} target {targetId}: {CcdNative.DescribeError(result)}");
            return (string.Empty, string.Empty);
        }

        return (MonitorIdentity.Normalize(request.monitorDevicePath), request.monitorFriendlyDeviceName?.Trim() ?? string.Empty);
    }

    private string QuerySourceName(LUID adapterId, uint sourceId)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = CcdNative.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId
            },
            viewGdiDeviceName = string.Empty
        };

        return CcdNative.DisplayConfigGetDeviceInfo(ref request) == CcdNative.ERROR_SUCCESS
            ? request.viewGdiDeviceName?.Trim() ?? string.Empty
            : string.Empty;
    }

    /// <summary>Builds a persistable snapshot from the current topology, including explicit inactive monitors.</summary>
    public static DisplaySnapshot ToSnapshot(string name, LiveTopology topology)
    {
        var snapshot = new DisplaySnapshot { Name = name, CapturedUtc = DateTime.UtcNow };

        foreach (var path in topology.DistinctMonitors())
        {
            var monitor = new MonitorSnapshot
            {
                DevicePath = path.DevicePath,
                FriendlyName = path.FriendlyName,
                Active = path.IsActive,
                SourceId = path.SourceId,
                TargetId = path.TargetId,
                CapturedAdapterId = path.AdapterId.ToString(),
                GdiDeviceName = path.GdiDeviceName,
                OutputTechnology = path.Path.targetInfo.outputTechnology,
                Rotation = path.Path.targetInfo.rotation,
                Scaling = path.Path.targetInfo.scaling,
                RefreshNumerator = path.Path.targetInfo.refreshRate.Numerator,
                RefreshDenominator = path.Path.targetInfo.refreshRate.Denominator,
                ScanLineOrdering = path.Path.targetInfo.scanLineOrdering,
                TargetAvailable = path.Path.targetInfo.targetAvailable != 0,
                PathFlags = path.Path.flags
            };

            if (path.SourceMode is { } source)
            {
                monitor.SourceMode = new SourceModeSnapshot
                {
                    Width = source.width,
                    Height = source.height,
                    PixelFormat = source.pixelFormat,
                    PositionX = source.position.x,
                    PositionY = source.position.y
                };
                monitor.IsPrimary = source.position is { x: 0, y: 0 };
            }

            if (path.TargetMode is { } target)
            {
                monitor.TargetMode = new VideoSignalSnapshot
                {
                    PixelRate = target.pixelRate,
                    HSyncNumerator = target.hSyncFreq.Numerator,
                    HSyncDenominator = target.hSyncFreq.Denominator,
                    VSyncNumerator = target.vSyncFreq.Numerator,
                    VSyncDenominator = target.vSyncFreq.Denominator,
                    ActiveWidth = target.activeSize.cx,
                    ActiveHeight = target.activeSize.cy,
                    TotalWidth = target.totalSize.cx,
                    TotalHeight = target.totalSize.cy,
                    VideoStandard = target.videoStandard,
                    ScanLineOrdering = target.scanLineOrdering
                };
            }

            snapshot.Monitors.Add(monitor);
        }

        snapshot.Monitors = snapshot.Monitors
            .OrderByDescending(m => m.Active)
            .ThenBy(m => m.SourceId)
            .ToList();

        return snapshot;
    }
}

public sealed class DisplayConfigException : Exception
{
    public DisplayConfigException(string message, int errorCode) : base(message) => ErrorCode = errorCode;
    public int ErrorCode { get; }
}

using System.Text.Json;
using DisplaySwitcher.Config;
using DisplaySwitcher.Display;
using DisplaySwitcher.Interop;
using DisplaySwitcher.Logging;
using DisplaySwitcher.Models;

namespace DisplaySwitcher;

public static class Program
{
    public const int ExitSuccess = 0;
    public const int ExitVerificationFailed = 1;
    public const int ExitMonitorMissing = 2;
    public const int ExitBadArguments = 3;
    public const int ExitUnexpectedError = 4;

    public static int Main(string[] args)
    {
        var appDirectory = ResolveAppDirectory();
        var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;

        if (mode is "" or "-h" or "--help" or "/?" or "help")
        {
            PrintUsage();
            return mode is "" ? ExitBadArguments : ExitSuccess;
        }

        var settings = AppSettings.Load(Path.Combine(appDirectory, "settings.json"), out var settingsWarning);

        using var log = new RunLogger(Path.Combine(appDirectory, "logs"), mode, settings.LogRetentionDays);

        try
        {
            if (settingsWarning is not null)
            {
                log.Warn(settingsWarning);
            }

            log.Info($"App directory: {appDirectory}");
            log.Info($"Settings: {settings.Describe()}");

            CcdNative.AssertLayouts();

            return mode switch
            {
                "status" => RunStatus(log),
                "capture" => RunCapture(args, appDirectory, log),
                "activate" => RunApply("streaming", appDirectory, settings, log),
                "restore" => RunApply("normal", appDirectory, settings, log),
                _ => UnknownMode(mode, log)
            };
        }
        catch (DisplayConfigException ex)
        {
            log.Error($"Display configuration API failure: {ex.Message}");
            return ExitUnexpectedError;
        }
        catch (Exception ex)
        {
            log.Error("Unhandled failure", ex);
            return ExitUnexpectedError;
        }
    }

    private static int UnknownMode(string mode, RunLogger log)
    {
        log.Error($"Unknown mode '{mode}'.");
        PrintUsage();
        return ExitBadArguments;
    }

    private static int RunStatus(RunLogger log)
    {
        var reader = new TopologyReader(log);
        var topology = reader.Read(CcdNative.QDC_ALL_PATHS);
        var monitors = topology.DistinctMonitors();

        log.Info($"Live topology: {topology.RawPaths.Length} raw paths, {topology.RawModes.Length} modes, " +
                 $"{monitors.Count} distinct connected monitors.");
        log.Info(string.Empty);

        foreach (var monitor in monitors)
        {
            log.Info($"  {(monitor.IsActive ? "ACTIVE  " : "inactive")}  {monitor.Label}");
            log.Info($"            friendlyName : {(monitor.FriendlyName.Length > 0 ? monitor.FriendlyName : "(none)")}");
            log.Info($"            devicePath   : {monitor.DevicePath}");
            log.Info($"            adapterLuid  : {monitor.AdapterId}  (changes across reboots - not used as a key)");
            log.Info($"            sourceId     : {monitor.SourceId}   targetId: {monitor.TargetId}");
            log.Info($"            gdiName      : {(monitor.GdiDeviceName.Length > 0 ? monitor.GdiDeviceName : "(none)")}");
            log.Info($"            mode         : {monitor.DescribeMode()}");
            log.Info($"            outputTech   : {monitor.Path.targetInfo.outputTechnology}   pathFlags: 0x{monitor.Path.flags:X8}");
            log.Info(string.Empty);
        }

        var active = monitors.Count(m => m.IsActive);
        log.Info($"Summary: {active} active, {monitors.Count - active} inactive.");
        return ExitSuccess;
    }

    private static int RunCapture(string[] args, string appDirectory, RunLogger log)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            log.Error("capture requires a snapshot name, e.g. 'DisplaySwitcher.exe capture normal'.");
            return ExitBadArguments;
        }

        var name = args[1].Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            log.Error($"Snapshot name '{name}' contains characters that are not valid in a file name.");
            return ExitBadArguments;
        }

        var reader = new TopologyReader(log);
        var topology = reader.Read(CcdNative.QDC_ALL_PATHS);
        var snapshot = TopologyReader.ToSnapshot(name, topology);

        if (snapshot.Monitors.Count == 0)
        {
            log.Error("No connected monitors were found; refusing to write an empty snapshot.");
            return ExitMonitorMissing;
        }

        if (!snapshot.ExpectedActive.Any())
        {
            log.Error("No active monitors were found; refusing to write a snapshot that turns everything off.");
            return ExitMonitorMissing;
        }

        var target = Path.Combine(appDirectory, $"{name}.json");
        File.WriteAllText(target, JsonSerializer.Serialize(snapshot, AppSettings.JsonOptions));

        log.Info($"Captured snapshot '{name}' to {target}");
        foreach (var monitor in snapshot.Monitors)
        {
            log.Info($"  {(monitor.Active ? "ACTIVE  " : "inactive")}  {monitor.Label} -> {monitor.DescribeMode()}");
        }

        return ExitSuccess;
    }

    private static int RunApply(string snapshotName, string appDirectory, AppSettings settings, RunLogger log)
    {
        var path = Path.Combine(appDirectory, $"{snapshotName}.json");

        if (!File.Exists(path))
        {
            log.Error($"Snapshot '{snapshotName}.json' not found at {path}. " +
                      $"Run 'DisplaySwitcher.exe capture {snapshotName}' while that layout is active.");
            return ExitBadArguments;
        }

        DisplaySnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<DisplaySnapshot>(File.ReadAllText(path), AppSettings.JsonOptions);
        }
        catch (JsonException ex)
        {
            log.Error($"Snapshot {path} is not valid JSON: {ex.Message}");
            return ExitBadArguments;
        }

        if (snapshot is null || snapshot.Monitors.Count == 0)
        {
            log.Error($"Snapshot {path} contains no monitors.");
            return ExitBadArguments;
        }

        var applier = new TopologyApplier(settings, log);
        return applier.Apply(snapshot);
    }

    /// <summary>
    /// Resolves the folder holding settings.json, the snapshots and logs. Environment.ProcessPath is
    /// used because AppContext.BaseDirectory points at the extraction folder for single-file builds.
    /// </summary>
    private static string ResolveAppDirectory()
    {
        var processPath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(directory) &&
                !directory.Contains(Path.Combine("Program Files", "dotnet"), StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            DisplaySwitcher - verified display topology switching for Sunshine.

              DisplaySwitcher.exe activate        Switch to the VDD-only layout (streaming.json)
              DisplaySwitcher.exe restore         Switch back to the desktop layout (normal.json)
              DisplaySwitcher.exe capture <name>  Save the current topology as <name>.json
              DisplaySwitcher.exe status          Print the live topology and device instance paths

            Exit codes: 0 success, 1 verification failed, 2 monitor missing,
                        3 bad arguments or missing snapshot, 4 unexpected error.
            """);
    }
}

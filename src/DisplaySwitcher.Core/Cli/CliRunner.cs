using DisplaySwitcher.Config;
using DisplaySwitcher.Display;
using DisplaySwitcher.Interop;
using DisplaySwitcher.Logging;
using DisplaySwitcher.Profiles;
using DisplaySwitcher.Sunshine;

namespace DisplaySwitcher.Cli;

/// <summary>
/// The headless side of DisplaySwitcher.exe, used by Sunshine's prep commands and by anyone
/// scripting the tool. The GUI calls the same Core services directly.
/// </summary>
public static class CliRunner
{
    public static int Run(string[] args)
    {
        var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;

        if (mode is "-h" or "--help" or "/?" or "help")
        {
            PrintUsage();
            return ExitCodes.Success;
        }

        var settings = AppSettings.Load(AppPaths.SettingsFile, out var settingsWarning);

        using var log = new RunLogger(AppPaths.LogDirectory, mode, settings.LogRetentionDays);

        try
        {
            if (AppPaths.FallbackReason is { } fallback)
            {
                log.Warn(fallback);
            }

            if (settingsWarning is not null)
            {
                log.Warn(settingsWarning);
            }

            log.Info($"Data directory: {AppPaths.Root}");
            log.Info($"Settings: {settings.Describe()}");

            CcdNative.AssertLayouts();

            return mode switch
            {
                "status" => RunStatus(log),
                "capture" => RunCapture(args, log),
                "activate" => RunApply(ProfileStore.Streaming, settings, log),
                "restore" => RunApply(ProfileStore.Desktop, settings, log),
                "apply" => RunApplyNamed(args, settings, log),
                "install-hook" => RunInstallHook(args, log),
                "remove-hook" => RunRemoveHook(args, log),
                _ => UnknownMode(mode, log)
            };
        }
        catch (DisplayConfigException ex)
        {
            log.Error($"Display configuration API failure: {ex.Message}");
            return ExitCodes.UnexpectedError;
        }
        catch (Exception ex)
        {
            log.Error("Unhandled failure", ex);
            return ExitCodes.UnexpectedError;
        }
    }

    private static int UnknownMode(string mode, RunLogger log)
    {
        log.Error($"Unknown command '{mode}'.");
        PrintUsage();
        return ExitCodes.BadArguments;
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

            var modes = DisplayModeEnumerator.ForGdiDevice(monitor.GdiDeviceName);
            log.Info($"            modes        : {modes.Count} advertised" +
                     (modes.Count > 0 ? $", best {modes[0]}" : string.Empty));
            log.Info(string.Empty);
        }

        var active = monitors.Count(m => m.IsActive);
        log.Info($"Summary: {active} active, {monitors.Count - active} inactive.");
        return ExitCodes.Success;
    }

    private static int RunCapture(string[] args, RunLogger log)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            log.Error("capture needs a profile name, for example: DisplaySwitcher.exe capture normal");
            return ExitCodes.BadArguments;
        }

        var name = args[1].Trim();
        if (!ProfileStore.IsValidName(name))
        {
            log.Error($"Profile name '{name}' contains characters that are not valid in a file name.");
            return ExitCodes.BadArguments;
        }

        var reader = new TopologyReader(log);
        var topology = reader.Read(CcdNative.QDC_ALL_PATHS);
        var snapshot = TopologyReader.ToSnapshot(name, topology);

        if (snapshot.Monitors.Count == 0)
        {
            log.Error("No connected monitors were found; refusing to write an empty profile.");
            return ExitCodes.MonitorMissing;
        }

        if (!snapshot.ExpectedActive.Any())
        {
            log.Error("No active monitors were found; refusing to write a profile that turns everything off.");
            return ExitCodes.MonitorMissing;
        }

        LayoutValidator.Normalize(snapshot);
        ProfileStore.Save(snapshot);

        log.Info($"Captured profile '{name}' to {AppPaths.ProfileFile(name)}");
        foreach (var monitor in snapshot.Monitors)
        {
            log.Info($"  {(monitor.Active ? "ACTIVE  " : "inactive")}  {monitor.Label} -> {monitor.DescribeMode()}");
        }

        return ExitCodes.Success;
    }

    private static int RunApplyNamed(string[] args, AppSettings settings, RunLogger log)
    {
        var name = args.Length > 1 ? args[1].Trim() : string.Empty;

        if (!ProfileStore.IsValidName(name))
        {
            log.Error("apply needs a profile name, for example: DisplaySwitcher.exe apply streaming");
            return ExitCodes.BadArguments;
        }

        return RunApply(name, settings, log);
    }

    private static int RunApply(string profileName, AppSettings settings, RunLogger log)
    {
        var snapshot = ProfileStore.TryLoad(profileName, out var error);

        if (snapshot is null)
        {
            log.Error(error ?? $"Profile '{profileName}' could not be loaded.");
            log.Error("Open DisplaySwitcher.exe to create it, or run: " +
                      $"DisplaySwitcher.exe capture {profileName}");
            return ExitCodes.BadArguments;
        }

        foreach (var issue in LayoutValidator.Validate(snapshot).Where(i => i.IsError))
        {
            log.Warn($"Profile '{profileName}' looks questionable: {issue.Message}");
        }

        var applier = new TopologyApplier(settings, log);
        return applier.Apply(snapshot);
    }

    /// <summary>Finds sunshine.conf, unless an explicit path was given as the second argument.</summary>
    private static string? ResolveConfigPath(string[] args, RunLogger log)
    {
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            var explicitPath = args[1].Trim();

            if (!File.Exists(explicitPath))
            {
                log.Error($"No file at {explicitPath}.");
                return null;
            }

            log.Info($"Using the sunshine.conf given on the command line: {explicitPath}");
            return explicitPath;
        }

        var install = SunshineConfigService.Detect();

        if (install is null)
        {
            log.Error("Could not find sunshine.conf. Run Setup.exe and browse to it, " +
                      "or pass the path: DisplaySwitcher.exe install-hook \"C:\\path\\to\\sunshine.conf\"");
            return null;
        }

        log.Info($"Found sunshine.conf at {install.ConfigPath} (via {install.DiscoveredVia}).");
        return install.ConfigPath;
    }

    private static int RunInstallHook(string[] args, RunLogger log)
    {
        var configPath = ResolveConfigPath(args, log);

        if (configPath is null)
        {
            return ExitCodes.BadArguments;
        }

        foreach (var command in SunshineConfigService.ReadPrepCommands(configPath))
        {
            log.Info($"  existing prep command ({(command.IsOurs ? "ours, will be replaced" : "another tool, preserved")}): {command.Do}");
        }

        var result = SunshineConfigService.InstallHook(configPath, AppPaths.DisplaySwitcherExe);

        if (!result.Success)
        {
            log.Error(result.Message);
            return ExitCodes.UnexpectedError;
        }

        log.Info(result.Message);
        log.Info($"Backup written to {result.BackupPath}");
        log.Info("Restart Sunshine for the change to take effect.");
        return ExitCodes.Success;
    }

    private static int RunRemoveHook(string[] args, RunLogger log)
    {
        var configPath = ResolveConfigPath(args, log);

        if (configPath is null)
        {
            return ExitCodes.BadArguments;
        }

        var result = SunshineConfigService.RemoveHook(configPath);

        if (!result.Success)
        {
            log.Error(result.Message);
            return ExitCodes.UnexpectedError;
        }

        log.Info(result.Message);
        return ExitCodes.Success;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            DisplaySwitcher - verified display switching for Sunshine / Moonlight.

              DisplaySwitcher.exe                   Open the configuration window
              DisplaySwitcher.exe activate          Switch to the streaming profile
              DisplaySwitcher.exe restore           Switch back to the desktop profile
              DisplaySwitcher.exe apply <name>      Apply any saved profile by name
              DisplaySwitcher.exe capture <name>    Save the current layout as a profile
              DisplaySwitcher.exe status            Print the live topology and device paths
              DisplaySwitcher.exe install-hook      Wire this exe into Sunshine (needs admin)
              DisplaySwitcher.exe remove-hook       Remove it from Sunshine (needs admin)

            install-hook and remove-hook find sunshine.conf automatically, or take its path
            as a second argument.

            Exit codes: 0 success, 1 verification failed, 2 a required display is missing,
                        3 bad arguments or missing profile, 4 unexpected error.
            """);
    }
}

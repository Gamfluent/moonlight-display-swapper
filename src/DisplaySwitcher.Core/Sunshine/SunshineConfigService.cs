using System.Diagnostics;
using System.Security;
using System.ServiceProcess;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DisplaySwitcher.Sunshine;

public sealed record SunshineInstall(string ConfigPath, string? InstallDirectory, string DiscoveredVia);

public sealed record PrepCommandView(string Do, string Undo, bool Elevated, bool IsOurs);

public sealed record HookResult(bool Success, string Message, string? BackupPath = null);

/// <summary>
/// Finds Sunshine's configuration and edits its global_prep_cmd list.
///
/// The critical rule here is that plenty of Sunshine users chain several prep commands (HDR
/// toggles, resolution matchers, bitrate tweaks). Only entries that point at DisplaySwitcher.exe
/// are ever touched; everything else is preserved byte for byte by editing the parsed JSON nodes
/// rather than regenerating the array.
/// </summary>
public sealed class SunshineConfigService
{
    private const string ConfigKey = "global_prep_cmd";
    private const string ServiceName = "SunshineService";
    private const string OurExeName = "DisplaySwitcher.exe";

    /// <summary>
    /// The default encoder escapes a quote as \u0022, which is valid JSON but unreadable and
    /// unlike anything Sunshine writes itself. The relaxed encoder emits \" instead, so the
    /// line still looks hand-written after we touch it.
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Locates sunshine.conf. The uninstall registry entry is the most reliable source, then the
    /// registered service image path, then the conventional install locations.
    /// </summary>
    public static SunshineInstall? Detect()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate.ConfigPath))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<SunshineInstall> EnumerateCandidates()
    {
        foreach (var directory in RegistryInstallLocations())
        {
            yield return new SunshineInstall(
                Path.Combine(directory, "config", "sunshine.conf"), directory, "Windows uninstall registry entry");
        }

        var serviceDirectory = ServiceImageDirectory();
        if (serviceDirectory is not null)
        {
            yield return new SunshineInstall(
                Path.Combine(serviceDirectory, "config", "sunshine.conf"), serviceDirectory, $"{ServiceName} service image path");
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var directory = Path.Combine(root, "Sunshine");
            yield return new SunshineInstall(
                Path.Combine(directory, "config", "sunshine.conf"), directory, "default install location");
        }
    }

    private static IEnumerable<string> RegistryInstallLocations()
    {
        var roots = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var root in roots)
        {
            RegistryKey? key = null;
            try
            {
                key = Registry.LocalMachine.OpenSubKey(root);
            }
            catch (SecurityException)
            {
                // Ignore: fall through to the other discovery methods.
            }

            if (key is null)
            {
                continue;
            }

            using (key)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    string? location = null;

                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        var displayName = subKey?.GetValue("DisplayName") as string;

                        if (displayName is not null &&
                            displayName.Contains("Sunshine", StringComparison.OrdinalIgnoreCase))
                        {
                            location = (subKey?.GetValue("InstallLocation") as string)?.Trim().TrimEnd('\\');
                        }
                    }
                    catch
                    {
                        // Ignore unreadable keys.
                    }

                    if (!string.IsNullOrEmpty(location))
                    {
                        yield return location;
                    }
                }
            }
        }
    }

    private static string? ServiceImageDirectory()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            var imagePath = key?.GetValue("ImagePath") as string;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            var trimmed = imagePath.Trim().Trim('"');
            var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex > 0)
            {
                trimmed = trimmed[..(exeIndex + 4)].Trim('"');
            }

            var directory = Path.GetDirectoryName(trimmed);

            // The service binary lives in the install root or a tools subfolder.
            if (directory is not null &&
                Path.GetFileName(directory).Equals("tools", StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.GetDirectoryName(directory);
            }

            return directory;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lists the prep commands currently configured, flagging the ones we own.</summary>
    public static IReadOnlyList<PrepCommandView> ReadPrepCommands(string configPath)
    {
        var array = ParsePrepArray(ReadValue(configPath));
        var result = new List<PrepCommandView>();

        foreach (var node in array)
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var doCmd = entry["do"]?.ToString() ?? string.Empty;
            var undoCmd = entry["undo"]?.ToString() ?? string.Empty;
            var elevated = ReadElevated(entry);

            result.Add(new PrepCommandView(doCmd, undoCmd, elevated, ReferencesOurExe(doCmd) || ReferencesOurExe(undoCmd)));
        }

        return result;
    }

    public static bool IsHookInstalled(string configPath) => ReadPrepCommands(configPath).Any(c => c.IsOurs);

    /// <summary>
    /// Adds or updates our prep command, leaving every unrelated entry untouched and in order.
    /// </summary>
    public static HookResult InstallHook(string configPath, string displaySwitcherExePath)
    {
        return Edit(configPath, array =>
        {
            RemoveOurEntries(array);

            var quoted = Quote(displaySwitcherExePath);
            array.Add(new JsonObject
            {
                ["do"] = $"{quoted} activate",
                ["undo"] = $"{quoted} restore",
                ["elevated"] = false
            });

            return "DisplaySwitcher is now wired into Sunshine's global prep commands.";
        });
    }

    public static HookResult RemoveHook(string configPath)
    {
        return Edit(configPath, array =>
        {
            var removed = RemoveOurEntries(array);
            return removed == 0
                ? "No DisplaySwitcher prep command was present, so nothing changed."
                : $"Removed {removed} DisplaySwitcher prep command entr{(removed == 1 ? "y" : "ies")}.";
        });
    }

    private static HookResult Edit(string configPath, Func<JsonArray, string> mutate)
    {
        if (!File.Exists(configPath))
        {
            return new HookResult(false, $"sunshine.conf was not found at {configPath}.");
        }

        string backupPath;
        try
        {
            backupPath = $"{configPath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(configPath, backupPath, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            return new HookResult(false,
                $"Access denied writing next to {configPath}. Run this as administrator.");
        }
        catch (Exception ex)
        {
            return new HookResult(false, $"Could not back up sunshine.conf: {ex.Message}");
        }

        try
        {
            var array = ParsePrepArray(ReadValue(configPath));
            var message = mutate(array);
            WriteValue(configPath, array.ToJsonString(WriteOptions));
            return new HookResult(true, message, backupPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new HookResult(false,
                $"Access denied writing {configPath}. Run this as administrator.", backupPath);
        }
        catch (Exception ex)
        {
            return new HookResult(false, $"Failed to update sunshine.conf: {ex.Message}", backupPath);
        }
    }

    private static int RemoveOurEntries(JsonArray array)
    {
        var removed = 0;

        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is not JsonObject entry)
            {
                continue;
            }

            var doCmd = entry["do"]?.ToString() ?? string.Empty;
            var undoCmd = entry["undo"]?.ToString() ?? string.Empty;

            if (ReferencesOurExe(doCmd) || ReferencesOurExe(undoCmd))
            {
                array.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static bool ReferencesOurExe(string command) =>
        command.Contains(OurExeName, StringComparison.OrdinalIgnoreCase);

    private static bool ReadElevated(JsonObject entry)
    {
        var node = entry["elevated"];

        if (node is null)
        {
            return false;
        }

        // Sunshine has written this as both a JSON boolean and a quoted string over the years.
        var text = node.ToString();
        return bool.TryParse(text, out var value) && value;
    }

    /// <summary>Reads the raw value of the global_prep_cmd key, or null when the key is absent.</summary>
    private static string? ReadValue(string configPath)
    {
        foreach (var line in File.ReadAllLines(configPath))
        {
            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            if (line[..separator].Trim().Equals(ConfigKey, StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>Rewrites only the global_prep_cmd line, leaving every other setting alone.</summary>
    private static void WriteValue(string configPath, string value)
    {
        var lines = File.ReadAllLines(configPath).ToList();
        var newLine = $"{ConfigKey} = {value}";
        var replaced = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var separator = lines[i].IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            if (!lines[i][..separator].Trim().Equals(ConfigKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (replaced)
            {
                lines.RemoveAt(i--);
                continue;
            }

            lines[i] = newLine;
            replaced = true;
        }

        if (!replaced)
        {
            lines.Add(newLine);
        }

        // Sunshine's parser does not tolerate a UTF-8 byte order mark.
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(configPath, string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n", utf8NoBom);
    }

    private static JsonArray ParsePrepArray(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new JsonArray();
        }

        try
        {
            return JsonNode.Parse(rawValue) as JsonArray ?? new JsonArray();
        }
        catch (JsonException)
        {
            // A malformed value is preserved by starting fresh; the backup still holds the original.
            return new JsonArray();
        }
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    public static bool ServiceExists()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            _ = controller.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restarts Sunshine so it re-reads the config. Sunshine only parses it at startup.</summary>
    public static HookResult RestartSunshine()
    {
        if (!ServiceExists())
        {
            return new HookResult(false,
                $"The {ServiceName} service was not found. Restart Sunshine manually so it re-reads the config.");
        }

        try
        {
            using var controller = new ServiceController(ServiceName);
            var timeout = TimeSpan.FromSeconds(30);

            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return new HookResult(true, "Sunshine restarted and has re-read its configuration.");
        }
        catch (Exception ex)
        {
            return new HookResult(false,
                $"Could not restart the {ServiceName} service ({ex.Message}). Restart Sunshine manually.");
        }
    }

    public static bool IsRunningElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // Opening a folder is a convenience; failing is not worth surfacing.
        }
    }
}

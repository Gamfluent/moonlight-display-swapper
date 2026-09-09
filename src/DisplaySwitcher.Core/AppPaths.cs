namespace DisplaySwitcher;

/// <summary>
/// Works out where settings, profiles and logs live. The app is portable by default, so
/// everything sits beside the executable. If that folder is read-only (unzipped into Program
/// Files, or run from a network share) it falls back to ProgramData, which is shared across
/// users so the Sunshine hook and the GUI always agree on the same files.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> RootLazy = new(Resolve);

    /// <summary>Set when the portable location was not writable and ProgramData was used instead.</summary>
    public static string? FallbackReason { get; private set; }

    public static string Root => RootLazy.Value;

    public static string ExeDirectory
    {
        get
        {
            var processPath = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(processPath))
            {
                var directory = Path.GetDirectoryName(processPath);

                // Guard against 'dotnet run', where ProcessPath is the shared dotnet host.
                if (!string.IsNullOrEmpty(directory) &&
                    !directory.Contains(Path.Combine("Program Files", "dotnet"), StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }
            }

            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogDirectory => Path.Combine(Root, "logs");
    public static string ProfileDirectory => Root;

    public static string ProfileFile(string name) => Path.Combine(ProfileDirectory, $"{name}.json");

    /// <summary>
    /// Both places profiles could live. Setup.exe runs elevated, so its writability probe can
    /// succeed where the non-elevated app's failed; checking both keeps the two in agreement.
    /// </summary>
    public static IEnumerable<string> CandidateRoots()
    {
        yield return Root;

        var shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DisplaySwitcher");

        if (!string.Equals(shared, Root, StringComparison.OrdinalIgnoreCase))
        {
            yield return shared;
        }
    }

    /// <summary>Path to DisplaySwitcher.exe, used when writing the Sunshine prep commands.</summary>
    public static string DisplaySwitcherExe => Path.Combine(ExeDirectory, "DisplaySwitcher.exe");

    private static string Resolve()
    {
        var portable = ExeDirectory;

        if (IsWritable(portable))
        {
            return portable;
        }

        var shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DisplaySwitcher");

        FallbackReason =
            $"'{portable}' is not writable, so settings, profiles and logs are being read from '{shared}' instead.";

        Directory.CreateDirectory(shared);
        return shared;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

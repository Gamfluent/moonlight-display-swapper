using System.Text.Json;
using DisplaySwitcher.Config;
using DisplaySwitcher.Models;

namespace DisplaySwitcher.Profiles;

/// <summary>
/// Reads and writes the saved topology profiles. Two names are canonical because Sunshine only
/// offers a single do/undo pair, but any name is accepted so the CLI can drive extra profiles.
/// </summary>
public static class ProfileStore
{
    /// <summary>Applied by 'restore', i.e. when the Moonlight client disconnects.</summary>
    public const string Desktop = "normal";

    /// <summary>Applied by 'activate', i.e. when the Moonlight client connects.</summary>
    public const string Streaming = "streaming";

    public static string FriendlyName(string profile) => profile switch
    {
        Desktop => "Desktop (Moonlight disconnected)",
        Streaming => "Streaming (Moonlight connected)",
        _ => profile
    };

    public static bool Exists(string name) =>
        AppPaths.CandidateRoots().Any(root => File.Exists(Path.Combine(root, $"{name}.json")));

    public static DisplaySnapshot? TryLoad(string name, out string? error)
    {
        error = null;
        var path = AppPaths.ProfileFile(name);

        if (!File.Exists(path))
        {
            error = $"Profile '{name}' has not been saved yet ({path} does not exist).";
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<DisplaySnapshot>(
                File.ReadAllText(path), AppSettings.JsonOptions);

            if (snapshot is null || snapshot.Monitors.Count == 0)
            {
                error = $"Profile '{name}' contains no monitors ({path}).";
                return null;
            }

            return snapshot;
        }
        catch (JsonException ex)
        {
            error = $"Profile '{name}' is not valid JSON ({path}): {ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            error = $"Profile '{name}' could not be read ({path}): {ex.Message}";
            return null;
        }
    }

    public static void Save(DisplaySnapshot snapshot)
    {
        Directory.CreateDirectory(AppPaths.ProfileDirectory);
        var path = AppPaths.ProfileFile(snapshot.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, AppSettings.JsonOptions));
    }

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

namespace DisplaySwitcher.Display;

public static class MonitorIdentity
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Converts the CCD interface path into the PnP device instance path shown in Device Manager.
    /// <c>\\?\DISPLAY#MTT1337#1&amp;33320f0&amp;0&amp;UID256#{e6f07b5f-...}</c>
    /// becomes <c>DISPLAY\MTT1337\1&amp;33320f0&amp;0&amp;UID256</c>.
    /// </summary>
    public static string Normalize(string? monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath))
        {
            return string.Empty;
        }

        var value = monitorDevicePath.Trim();

        if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }
        else if (value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        var segments = value
            .Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.StartsWith('{'))
            .ToArray();

        // Already in device-instance form (no '#' separators) - hand it back untouched.
        return segments.Length == 0 ? value : string.Join('\\', segments);
    }

    /// <summary>
    /// True when two identifiers refer to the same physical monitor. Windows is inconsistent about
    /// casing between WMI and the CCD APIs, and some sources append a "_0" enumerator suffix.
    /// </summary>
    public static bool SameMonitor(string? a, string? b)
    {
        var left = Canonical(a);
        var right = Canonical(b);
        return left.Length > 0 && left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonical(string? value)
    {
        var normalized = Normalize(value);

        var suffix = normalized.LastIndexOf('_');
        if (suffix > 0 && suffix == normalized.Length - 2 && char.IsDigit(normalized[^1]))
        {
            normalized = normalized[..suffix];
        }

        return normalized;
    }
}

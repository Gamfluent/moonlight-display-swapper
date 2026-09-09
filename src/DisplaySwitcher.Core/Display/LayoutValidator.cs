using DisplaySwitcher.Models;

namespace DisplaySwitcher.Display;

public sealed record LayoutIssue(bool IsError, string Message);

/// <summary>
/// Enforces the rules Windows applies to a desktop layout before we hand it to SetDisplayConfig:
/// exactly one primary display, the primary anchored at the desktop origin, and no display
/// stranded away from the others. Catching these here produces a readable message instead of an
/// opaque ERROR_INVALID_PARAMETER or ERROR_BAD_CONFIGURATION from the driver.
/// </summary>
public static class LayoutValidator
{
    /// <summary>
    /// Guarantees a single primary and shifts every active display so the primary sits at (0,0).
    /// Windows defines the desktop origin as the primary display's top-left corner.
    /// </summary>
    public static void Normalize(DisplaySnapshot snapshot)
    {
        var active = snapshot.Monitors.Where(m => m.Active && m.SourceMode is not null).ToList();

        if (active.Count == 0)
        {
            return;
        }

        var primary = active.FirstOrDefault(m => m.IsPrimary)
                      ?? active.OrderBy(m => m.SourceMode!.PositionY)
                               .ThenBy(m => m.SourceMode!.PositionX)
                               .First();

        foreach (var monitor in snapshot.Monitors)
        {
            monitor.IsPrimary = ReferenceEquals(monitor, primary);
        }

        var offsetX = primary.SourceMode!.PositionX;
        var offsetY = primary.SourceMode!.PositionY;

        if (offsetX == 0 && offsetY == 0)
        {
            return;
        }

        foreach (var monitor in active)
        {
            monitor.SourceMode!.PositionX -= offsetX;
            monitor.SourceMode!.PositionY -= offsetY;
        }
    }

    public static IReadOnlyList<LayoutIssue> Validate(DisplaySnapshot snapshot)
    {
        var issues = new List<LayoutIssue>();
        var active = snapshot.Monitors.Where(m => m.Active).ToList();

        if (active.Count == 0)
        {
            issues.Add(new LayoutIssue(true, "At least one display must be enabled; Windows cannot turn every display off."));
            return issues;
        }

        var missingModes = active.Where(m => m.SourceMode is null).ToList();
        foreach (var monitor in missingModes)
        {
            issues.Add(new LayoutIssue(true, $"{monitor.Label} is enabled but has no resolution set."));
        }

        var positioned = active.Where(m => m.SourceMode is not null).ToList();
        if (positioned.Count == 0)
        {
            return issues;
        }

        if (positioned.Count(m => m.IsPrimary) != 1)
        {
            issues.Add(new LayoutIssue(true, "Exactly one display must be marked as the main display."));
        }

        foreach (var group in FindOverlaps(positioned))
        {
            issues.Add(new LayoutIssue(false, $"{group.Item1.Label} and {group.Item2.Label} overlap. Windows allows this but it is usually a mistake."));
        }

        var stranded = FindDisconnected(positioned);
        foreach (var monitor in stranded)
        {
            issues.Add(new LayoutIssue(true,
                $"{monitor.Label} is not touching any other display. Windows requires the desktop to be a single connected area."));
        }

        return issues;
    }

    private static IEnumerable<(MonitorSnapshot, MonitorSnapshot)> FindOverlaps(List<MonitorSnapshot> monitors)
    {
        for (var i = 0; i < monitors.Count; i++)
        {
            for (var j = i + 1; j < monitors.Count; j++)
            {
                if (Overlaps(monitors[i], monitors[j]))
                {
                    yield return (monitors[i], monitors[j]);
                }
            }
        }
    }

    /// <summary>Flood-fills the adjacency graph from the first display; anything unreached is stranded.</summary>
    private static List<MonitorSnapshot> FindDisconnected(List<MonitorSnapshot> monitors)
    {
        if (monitors.Count < 2)
        {
            return new List<MonitorSnapshot>();
        }

        var reached = new HashSet<MonitorSnapshot> { monitors[0] };
        var queue = new Queue<MonitorSnapshot>();
        queue.Enqueue(monitors[0]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var candidate in monitors)
            {
                if (reached.Contains(candidate) || !Touches(current, candidate))
                {
                    continue;
                }

                reached.Add(candidate);
                queue.Enqueue(candidate);
            }
        }

        return monitors.Where(m => !reached.Contains(m)).ToList();
    }

    private static bool Touches(MonitorSnapshot a, MonitorSnapshot b)
    {
        var (ax1, ay1, ax2, ay2) = Bounds(a);
        var (bx1, by1, bx2, by2) = Bounds(b);

        // Inclusive comparison, so sharing an edge counts as touching.
        return ax1 <= bx2 && bx1 <= ax2 && ay1 <= by2 && by1 <= ay2;
    }

    private static bool Overlaps(MonitorSnapshot a, MonitorSnapshot b)
    {
        var (ax1, ay1, ax2, ay2) = Bounds(a);
        var (bx1, by1, bx2, by2) = Bounds(b);

        // Strict comparison, so a shared edge is not an overlap.
        return ax1 < bx2 && bx1 < ax2 && ay1 < by2 && by1 < ay2;
    }

    private static (int Left, int Top, int Right, int Bottom) Bounds(MonitorSnapshot monitor)
    {
        var mode = monitor.SourceMode!;
        return (mode.PositionX, mode.PositionY, mode.PositionX + (int)mode.Width, mode.PositionY + (int)mode.Height);
    }
}

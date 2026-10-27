namespace AIHelper.Models;

/// <summary>
/// Pure rules for the intraday auto-scan (14:00-14:50 on weekdays, one scan every
/// <see cref="IntervalMinutes"/> minutes) and for diffing consecutive TopN snapshots.
/// No clock and no UI dependency, so the rules are directly unit-testable.
/// </summary>
public static class SparrowAutoScan
{
    /// <summary>Inclusive window start (local time).</summary>
    public static readonly TimeSpan WindowStart = new(14, 0, 0);
    /// <summary>Inclusive window end (local time): late enough for a 14:50 pick to still execute.</summary>
    public static readonly TimeSpan WindowEnd = new(14, 50, 0);
    /// <summary>Minimum minutes between automatic scans.</summary>
    public const int IntervalMinutes = 5;
    /// <summary>Maximum symbols named in a single Growl notification.</summary>
    public const int MaxNotifications = 5;

    /// <summary>True when <paramref name="now"/> falls in the weekday window and at least one interval has passed since the last tick (or no tick has happened yet).</summary>
    public static bool IsDue(DateTime now, DateTime? lastScanAt)
    {
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        TimeSpan time = now.TimeOfDay;
        if (time < WindowStart || time > WindowEnd) return false;
        return lastScanAt is null || (now - lastScanAt.Value).TotalMinutes >= IntervalMinutes;
    }

    /// <summary>Candidates whose code was not in the previous TopN snapshot, in ranking order.</summary>
    public static IReadOnlyList<SparrowRankedCandidate> NewEntrants(
        IReadOnlyCollection<string> previousCodes, IReadOnlyList<SparrowRankedCandidate> currentTopN)
    {
        ArgumentNullException.ThrowIfNull(previousCodes);
        ArgumentNullException.ThrowIfNull(currentTopN);
        if (previousCodes.Count == 0) return Array.Empty<SparrowRankedCandidate>();
        return currentTopN.Where(item => !previousCodes.Contains(item.Code)).ToArray();
    }
}

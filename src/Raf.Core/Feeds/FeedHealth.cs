namespace Raf.Core.Feeds;

public enum FeedStatus
{
    /// <summary>The last run succeeded.</summary>
    Healthy,

    /// <summary>One or more recent runs failed, fewer than the "down" threshold. Prices keep ageing normally.</summary>
    Degraded,

    /// <summary>Failed repeatedly. Its prices are still shown, with their age, until they expire.</summary>
    Down,

    /// <summary>No attempt for a long time: the problem is our scheduler, not the market.</summary>
    Unknown,
}

/// <summary>Feed health transitions. Health is about the pipe; freshness is about each price. They are reported separately.</summary>
public static class FeedHealth
{
    public static (FeedStatus Status, int ConsecutiveFailures) AfterRun(int previousFailures, bool succeeded, int downAfterFailures)
    {
        if (succeeded)
        {
            return (FeedStatus.Healthy, 0);
        }

        var failures = previousFailures + 1;
        return (failures >= downAfterFailures ? FeedStatus.Down : FeedStatus.Degraded, failures);
    }

    public static bool IsSilent(DateTimeOffset? lastAttemptAt, DateTimeOffset now, TimeSpan silentAfter) =>
        lastAttemptAt is null || now - lastAttemptAt.Value > silentAfter;

    public static string ToWire(this FeedStatus status) => status switch
    {
        FeedStatus.Healthy => "healthy",
        FeedStatus.Degraded => "degraded",
        FeedStatus.Down => "down",
        _ => "unknown",
    };

    public static FeedStatus FromWire(string status) => status switch
    {
        "healthy" => FeedStatus.Healthy,
        "degraded" => FeedStatus.Degraded,
        "down" => FeedStatus.Down,
        _ => FeedStatus.Unknown,
    };
}

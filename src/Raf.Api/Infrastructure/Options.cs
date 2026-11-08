using Raf.Core.Comparison;
using Raf.Core.Pricing;

namespace Raf.Api.Infrastructure;

public sealed class FreshnessOptions
{
    public const string Section = "Freshness";

    public TimeSpan FreshFor { get; set; } = FreshnessPolicy.Default.FreshFor;

    public TimeSpan AgingFor { get; set; } = FreshnessPolicy.Default.AgingFor;

    public TimeSpan ExpireAfter { get; set; } = FreshnessPolicy.Default.ExpireAfter;

    public FreshnessPolicy ToPolicy()
    {
        var policy = new FreshnessPolicy(FreshFor, AgingFor, ExpireAfter);
        policy.Validate();
        return policy;
    }
}

public sealed class ComparisonSettings
{
    public const string Section = "Comparison";

    public decimal MaxOverbuyRatio { get; set; } = ComparisonOptions.Default.MaxOverbuyRatio;

    /// <summary>Upper bound for a cached comparison; the real TTL is also capped by the first price expiry.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(10);

    public bool CacheEnabled { get; set; } = true;
}

public sealed class FeedMarketOptions
{
    public string Url { get; set; } = "";

    /// <summary>Hangfire cron; 6 fields means seconds granularity.</summary>
    public string Cron { get; set; } = "* * * * *";
}

public sealed class FeedOptions
{
    public const string Section = "Feeds";

    public Dictionary<string, FeedMarketOptions> Markets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>One failed run makes a feed "degraded"; this many in a row make it "down".</summary>
    public int DownAfterFailures { get; set; } = 3;

    /// <summary>A feed with no attempt for this long is "unknown": the scheduler, not the market, has stopped.</summary>
    public TimeSpan SilentAfter { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxPages { get; set; } = 50;
}

public sealed class JobOptions
{
    public const string Section = "Jobs";

    /// <summary>Run a Hangfire server in this process (off in tests and in the one-shot migrate/seed modes).</summary>
    public bool ServerEnabled { get; set; } = true;

    /// <summary>Create or update the recurring jobs at startup.</summary>
    public bool RegisterRecurringJobs { get; set; } = true;

    /// <summary>Read-only dashboard at /hangfire. Only for the local demo: it has no authentication.</summary>
    public bool DashboardEnabled { get; set; }

    public int WorkerCount { get; set; } = 4;

    public TimeSpan FeedRunRetention { get; set; } = TimeSpan.FromDays(14);
}

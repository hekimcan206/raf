using Hangfire;
using Microsoft.Extensions.Options;
using Raf.Api.Infrastructure;
using Raf.Api.Realtime;

namespace Raf.Api.Ingestion;

/// <summary>
/// Hangfire entry points. The job plan (schedules, retries, concurrency, failure semantics) is documented in
/// docs/hangfire-job-plan.md. Key choices:
/// <list type="bullet">
/// <item>no automatic retries for ingestion: the next scheduled run is the retry, and retries queued behind an outage
/// would only pile up and fire together when the feed recovers;</item>
/// <item>an unavailable feed is an expected outcome (recorded in feed health), so the job succeeds; only unexpected
/// errors (database down, bugs) fail the Hangfire job;</item>
/// <item>per-market mutual exclusion is a PostgreSQL advisory lock inside the service, not
/// [DisableConcurrentExecution], which would serialise all markets behind one method-level lock.</item>
/// </list>
/// </summary>
public sealed class IngestionJobs
{
    public const string IngestQueue = "ingest";
    public const string MaintenanceQueue = "maintenance";
    public const string WatchdogJobId = "feed-health-watchdog";
    public const string PruneJobId = "prune-feed-runs";

    private readonly FeedIngestionService _ingestion;
    private readonly FeedHealthStore _health;
    private readonly PriceChangeNotifier _notifier;
    private readonly IOptions<JobOptions> _jobOptions;

    public IngestionJobs(FeedIngestionService ingestion, FeedHealthStore health, PriceChangeNotifier notifier, IOptions<JobOptions> jobOptions)
    {
        _ingestion = ingestion;
        _health = health;
        _notifier = notifier;
        _jobOptions = jobOptions;
    }

    public static string IngestJobId(string market) => $"ingest-{market}";

    [AutomaticRetry(Attempts = 0, LogEvents = false)]
    [JobDisplayName("Ingest {0} feed")]
    public async Task<string> IngestAsync(string marketCode, CancellationToken cancellationToken)
    {
        var result = await _ingestion.RunAsync(marketCode, cancellationToken);
        return $"{result.Status}: {result.Matched} matched, {result.Inserted} new, {result.Changed} changed{(result.Error is null ? "" : $" ({result.Error})")}";
    }

    [AutomaticRetry(Attempts = 0, LogEvents = false)]
    [JobDisplayName("Feed health watchdog")]
    public async Task<int> WatchdogAsync(CancellationToken cancellationToken)
    {
        var silent = await _health.MarkSilentAsync(cancellationToken);
        foreach (var market in silent)
        {
            await _notifier.FeedHealthChangedAsync(new FeedHealthChangedEvent(market.Code, market.Status, market.LastSuccessAt));
        }

        return silent.Count;
    }

    [AutomaticRetry(Attempts = 3)]
    [JobDisplayName("Prune old feed runs")]
    public Task<int> PruneFeedRunsAsync(CancellationToken cancellationToken) =>
        _health.PruneRunsAsync(_jobOptions.Value.FeedRunRetention, cancellationToken);
}

internal static class JobRegistration
{
    /// <summary>Idempotent: AddOrUpdate on every start keeps schedules in code, removes jobs for markets no longer configured.</summary>
    public static void RegisterRecurringJobs(IRecurringJobManager jobs, FeedOptions feeds, IEnumerable<string> knownMarkets)
    {
        var options = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };
        foreach (var (market, feed) in feeds.Markets)
        {
            jobs.AddOrUpdate<IngestionJobs>(IngestionJobs.IngestJobId(market), IngestionJobs.IngestQueue,
                j => j.IngestAsync(market, CancellationToken.None), feed.Cron, options);
        }

        foreach (var stale in knownMarkets.Where(m => !feeds.Markets.ContainsKey(m)))
        {
            jobs.RemoveIfExists(IngestionJobs.IngestJobId(stale));
        }

        jobs.AddOrUpdate<IngestionJobs>(IngestionJobs.WatchdogJobId, IngestionJobs.MaintenanceQueue,
            j => j.WatchdogAsync(CancellationToken.None), "* * * * *", options);
        jobs.AddOrUpdate<IngestionJobs>(IngestionJobs.PruneJobId, IngestionJobs.MaintenanceQueue,
            j => j.PruneFeedRunsAsync(CancellationToken.None), "17 3 * * *", options);
    }
}

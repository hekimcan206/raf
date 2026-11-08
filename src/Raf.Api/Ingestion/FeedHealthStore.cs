using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Raf.Api.Infrastructure;
using Raf.Core.Feeds;

namespace Raf.Api.Ingestion;

public sealed record MarketHealthDto(
    string Code,
    string Name,
    string Status,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    int ConsecutiveFailures,
    string? LastError);

public sealed record FeedRunDto(
    long Id, string MarketCode, DateTimeOffset StartedAt, double DurationMs, string Status, int? HttpStatus, int Pages,
    int ItemsReceived, int ItemsMatched, int ItemsUnmatched, int ItemsRejected, int ObservationsInserted, int PricesChanged, string? Error);

public sealed class FeedHealthStore(NpgsqlDataSource dataSource, IOptions<FeedOptions> options, TimeProvider clock)
{
    /// <summary>
    /// Feed health changes at most once per ingestion run (every minute or more), but every comparison shows it. A
    /// short in-process snapshot keeps a Redis cache hit free of database round trips. Status changes made by this
    /// process drop the snapshot immediately, and another instance's changes show up within <see cref="SnapshotTtl"/>.
    /// </summary>
    public static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(5);

    private (IReadOnlyList<MarketHealthDto> Value, DateTimeOffset At)? _snapshot;

    public async Task<IReadOnlyList<MarketHealthDto>> GetSnapshotAsync(CancellationToken ct)
    {
        var snapshot = _snapshot;
        var now = clock.GetUtcNow();
        if (snapshot is { } s && now - s.At < SnapshotTtl)
        {
            return s.Value;
        }

        var fresh = await GetAllAsync(ct);
        _snapshot = (fresh, now);
        return fresh;
    }

    public void InvalidateSnapshot() => _snapshot = null;

    public async Task<IReadOnlyList<MarketHealthDto>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<HealthRow>(new CommandDefinition("""
            SELECT m.code, m.name, coalesce(s.status, 'unknown') AS status, s.last_success_at AS LastSuccessAt,
                   s.last_attempt_at AS LastAttemptAt, coalesce(s.consecutive_failures, 0) AS ConsecutiveFailures, s.last_error AS LastError
            FROM raf.market m LEFT JOIN raf.market_feed_state s ON s.market_code = m.code
            WHERE m.active
            ORDER BY m.code
            """, cancellationToken: ct));
        return [.. rows.Select(r => new MarketHealthDto(r.Code, r.Name, r.Status, Utc(r.LastSuccessAt), Utc(r.LastAttemptAt), r.ConsecutiveFailures, r.LastError))];
    }

    public async Task<IReadOnlyList<FeedRunDto>> RecentRunsAsync(string? market, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition("""
            SELECT id, market_code AS MarketCode, started_at AS StartedAt, finished_at AS FinishedAt, status, http_status AS HttpStatus, pages,
                   items_received AS ItemsReceived, items_matched AS ItemsMatched, items_unmatched AS ItemsUnmatched,
                   items_rejected AS ItemsRejected, observations_inserted AS ObservationsInserted, prices_changed AS PricesChanged, error
            FROM raf.feed_run
            WHERE @market IS NULL OR market_code = @market
            ORDER BY id DESC
            LIMIT @limit
            """, new { market, limit }, cancellationToken: ct));
        return [.. rows.Select(r => new FeedRunDto(r.Id, r.MarketCode, Utc(r.StartedAt)!.Value, (r.FinishedAt - r.StartedAt).TotalMilliseconds, r.Status,
            r.HttpStatus, r.Pages, r.ItemsReceived, r.ItemsMatched, r.ItemsUnmatched, r.ItemsRejected, r.ObservationsInserted, r.PricesChanged, r.Error))];
    }

    /// <summary>Applies one run's outcome to the market's rolling state inside the caller's transaction; returns the status change, if any.</summary>
    internal static async Task<(FeedStatus Before, FeedStatus After, DateTimeOffset? LastSuccessAt)> RecordRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string market, bool succeeded, DateTimeOffset at, string? error, int downAfterFailures)
    {
        var previous = await connection.QuerySingleOrDefaultAsync<StateRow>(
            "SELECT status, consecutive_failures AS ConsecutiveFailures, last_success_at AS LastSuccessAt FROM raf.market_feed_state WHERE market_code = @market FOR UPDATE",
            new { market }, tx);
        var before = previous is null ? FeedStatus.Unknown : FeedHealth.FromWire(previous.Status);
        var (after, failures) = FeedHealth.AfterRun(previous?.ConsecutiveFailures ?? 0, succeeded, downAfterFailures);
        var lastSuccess = succeeded ? at : Utc(previous?.LastSuccessAt);

        await connection.ExecuteAsync("""
            INSERT INTO raf.market_feed_state (market_code, status, last_attempt_at, last_success_at, consecutive_failures, last_error, changed_at)
            VALUES (@market, @status, @at, @lastSuccess, @failures, @error, @at)
            ON CONFLICT (market_code) DO UPDATE SET
                status = EXCLUDED.status,
                last_attempt_at = EXCLUDED.last_attempt_at,
                last_success_at = EXCLUDED.last_success_at,
                consecutive_failures = EXCLUDED.consecutive_failures,
                last_error = EXCLUDED.last_error,
                changed_at = CASE WHEN raf.market_feed_state.status = EXCLUDED.status THEN raf.market_feed_state.changed_at ELSE EXCLUDED.changed_at END
            """, new { market, status = after.ToWire(), at, lastSuccess, failures, error = succeeded ? null : error }, tx);
        return (before, after, lastSuccess);
    }

    /// <summary>Marks markets whose job has not run for <see cref="FeedOptions.SilentAfter"/> as unknown; returns those that changed.</summary>
    public async Task<IReadOnlyList<MarketHealthDto>> MarkSilentAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<HealthRow>(new CommandDefinition("""
            UPDATE raf.market_feed_state s SET status = 'unknown', changed_at = @now
            FROM raf.market m
            WHERE m.code = s.market_code AND s.status <> 'unknown' AND (s.last_attempt_at IS NULL OR s.last_attempt_at < @cutoff)
            RETURNING m.code, m.name, s.status, s.last_success_at AS LastSuccessAt, s.last_attempt_at AS LastAttemptAt,
                      s.consecutive_failures AS ConsecutiveFailures, s.last_error AS LastError
            """, new { now, cutoff = now - options.Value.SilentAfter }, cancellationToken: ct));
        return [.. rows.Select(r => new MarketHealthDto(r.Code, r.Name, r.Status, Utc(r.LastSuccessAt), Utc(r.LastAttemptAt), r.ConsecutiveFailures, r.LastError))];
    }

    public async Task<int> PruneRunsAsync(TimeSpan retention, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // Observations keep their history; only the run log is pruned (their feed_run_id becomes NULL).
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM raf.feed_run WHERE started_at < @cutoff", new { cutoff = clock.GetUtcNow() - retention }, cancellationToken: ct));
    }

    private static DateTimeOffset? Utc(DateTime? value) => value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private sealed class HealthRow
    {
        public string Code { get; init; } = "";

        public string Name { get; init; } = "";

        public string Status { get; init; } = "";

        public DateTime? LastSuccessAt { get; init; }

        public DateTime? LastAttemptAt { get; init; }

        public int ConsecutiveFailures { get; init; }

        public string? LastError { get; init; }
    }

    private sealed class StateRow
    {
        public string Status { get; init; } = "";

        public int ConsecutiveFailures { get; init; }

        public DateTime? LastSuccessAt { get; init; }
    }

    private sealed class RunRow
    {
        public long Id { get; init; }

        public string MarketCode { get; init; } = "";

        public DateTime StartedAt { get; init; }

        public DateTime FinishedAt { get; init; }

        public string Status { get; init; } = "";

        public int? HttpStatus { get; init; }

        public int Pages { get; init; }

        public int ItemsReceived { get; init; }

        public int ItemsMatched { get; init; }

        public int ItemsUnmatched { get; init; }

        public int ItemsRejected { get; init; }

        public int ObservationsInserted { get; init; }

        public int PricesChanged { get; init; }

        public string? Error { get; init; }
    }
}

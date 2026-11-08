using System.Text.Json;
using System.Xml;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Raf.Api.Catalog;
using Raf.Api.Infrastructure;
using Raf.Api.Realtime;
using Raf.Core.Catalog;
using Raf.Core.Feeds;

namespace Raf.Api.Ingestion;

public sealed record FeedRunResult(
    string MarketCode, string Status, int? HttpStatus, int Pages, int Received, int Matched, int Unmatched, int Rejected,
    int Inserted, int Changed, string? Error);

/// <summary>A feed could not be read: transport error, HTTP error, timeout or an unparseable document.</summary>
internal sealed class FeedUnavailableException(string message, int? httpStatus, Exception? inner = null) : Exception(message, inner)
{
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>
/// Fetches one market's feed, parses it with the market's format, matches items to the catalogue, and applies
/// prices idempotently. A run is all-or-nothing: a failed page fails the run and nothing is written, so a feed is
/// never half-applied. Every attempt is logged in raf.feed_run and moves the market's health state.
/// </summary>
public sealed partial class FeedIngestionService(
    IHttpClientFactory httpClientFactory,
    NpgsqlDataSource dataSource,
    IOptions<FeedOptions> options,
    PriceChangeNotifier notifier,
    RafMetrics metrics,
    TimeProvider clock,
    ILogger<FeedIngestionService> logger)
{
    public const string HttpClientName = "feeds";

    public async Task<FeedRunResult> RunAsync(string marketCode, CancellationToken ct)
    {
        if (!options.Value.Markets.TryGetValue(marketCode, out var market))
        {
            throw new ArgumentException($"Market '{marketCode}' has no feed configured.", nameof(marketCode));
        }

        // One run per market at a time, across all API instances: a session advisory lock held for the whole run.
        // A run that finds the lock taken is skipped, not queued: the next schedule will pick up newer data anyway.
        await using var lockConnection = await dataSource.OpenConnectionAsync(ct);
        var lockKey = $"raf:ingest:{marketCode}";
        if (!await lockConnection.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtext(@lockKey))", new { lockKey }))
        {
            LogSkipped(marketCode);
            metrics.FeedRun(marketCode, "skipped");
            return new FeedRunResult(marketCode, "skipped", null, 0, 0, 0, 0, 0, 0, 0, "previous run still in progress");
        }

        try
        {
            return await RunLockedAsync(marketCode, market.Url, ct);
        }
        finally
        {
            await lockConnection.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@lockKey))", new { lockKey });
        }
    }

    private async Task<FeedRunResult> RunLockedAsync(string marketCode, string url, CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        var format = FeedFormats.For(marketCode);
        List<FeedItem> items;
        List<string> rejected;
        int pages;
        int? httpStatus;
        try
        {
            (items, rejected, pages, httpStatus) = await FetchAsync(format, url, ct);
        }
        catch (FeedUnavailableException ex)
        {
            await RecordFailureAsync(marketCode, startedAt, ex.HttpStatus, ex.Message, ct);
            LogFeedFailed(marketCode, ex.HttpStatus, ex.Message);
            metrics.FeedRun(marketCode, "failed");
            return new FeedRunResult(marketCode, "failed", ex.HttpStatus, 0, 0, 0, 0, 0, 0, 0, ex.Message);
        }

        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var (matched, unmatched, rejectedByRules) = await MatchAsync(connection, marketCode, items, now, ct);
        rejected.AddRange(rejectedByRules);

        await using var tx = await connection.BeginTransactionAsync(ct);
        var runId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO raf.feed_run (market_code, started_at, finished_at, status, http_status, pages, items_received, items_matched, items_unmatched, items_rejected)
            VALUES (@marketCode, @startedAt, @now, 'succeeded', @httpStatus, @pages, @received, @matched, @unmatched, @rejected)
            RETURNING id
            """, new { marketCode, startedAt, now, httpStatus, pages, received = items.Count, matched = matched.Count, unmatched, rejected = rejected.Count }, tx);

        var (inserted, changes) = await PriceWriter.ApplyAsync(connection, tx, marketCode, matched, $"feed:{marketCode}", runId);
        await connection.ExecuteAsync(
            "UPDATE raf.feed_run SET observations_inserted = @inserted, prices_changed = @changed, finished_at = @finished, error = @error WHERE id = @runId",
            new { inserted, changed = changes.Count, finished = clock.GetUtcNow(), runId, error = rejected.Count > 0 ? Summarise(rejected) : null }, tx);
        var health = await FeedHealthStore.RecordRunAsync(connection, tx, marketCode, succeeded: true, now, null, options.Value.DownAfterFailures);
        await tx.CommitAsync(ct);

        // After commit: invalidate + push. If this process dies here, the cache entries still expire by TTL and the
        // next client refetch is correct; the push is best-effort by design (clients resync on reconnect).
        await notifier.PricesChangedAsync(changes, ct);
        await NotifyHealthAsync(marketCode, health);

        metrics.FeedRun(marketCode, "succeeded");
        metrics.PricesChanged(marketCode, changes.Count);
        LogFeedApplied(marketCode, items.Count, matched.Count, inserted, changes.Count);
        return new FeedRunResult(marketCode, "succeeded", httpStatus, pages, items.Count, matched.Count, unmatched, rejected.Count, inserted, changes.Count, null);
    }

    private async Task<(List<FeedItem> Items, List<string> Rejected, int Pages, int? HttpStatus)> FetchAsync(IFeedFormat format, string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var items = new List<FeedItem>();
        var rejected = new List<string>();
        var page = 1;
        var totalPages = 1;
        int? status = null;
        do
        {
            var pageUrl = format.IsPaged ? $"{url}{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}page={page}" : url;
            try
            {
                using var response = await client.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    throw new FeedUnavailableException($"HTTP {status} on page {page}", status);
                }

                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var parsed = format.Parse(body);
                items.AddRange(parsed.Items);
                rejected.AddRange(parsed.Rejected);
                totalPages = Math.Min(parsed.TotalPages, options.Value.MaxPages);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Polly.Timeout.TimeoutRejectedException
                                       && !ct.IsCancellationRequested)
            {
                throw new FeedUnavailableException($"{ex.GetType().Name} on page {page}: {ex.Message}", (int?)(ex as HttpRequestException)?.StatusCode, ex);
            }
            catch (Exception ex) when (ex is JsonException or XmlException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                throw new FeedUnavailableException($"unparseable feed on page {page}: {ex.Message}", status, ex);
            }

            page++;
        }
        while (page <= totalPages);

        return (items, rejected, totalPages, status);
    }

    /// <summary>
    /// GTIN feeds match on the canonical GTIN-14; SKU feeds through raf.market_sku. When the feed states a pack size
    /// that disagrees with the catalogue, the item is rejected: a re-used barcode or a mapping error must not
    /// silently turn a 500 g price into a 1 kg price (docs/adr/0002).
    /// </summary>
    private static async Task<(List<MatchedPrice> Matched, int Unmatched, List<string> Rejected)> MatchAsync(
        NpgsqlConnection connection, string market, IReadOnlyList<FeedItem> items, DateTimeOffset now, CancellationToken ct)
    {
        var byGtin = (await connection.QueryAsync<(string Gtin, Guid Id, string Dim, decimal Amount)>(new CommandDefinition(
                "SELECT gtin, id, size_dimension, size_base_amount FROM raf.product", cancellationToken: ct)))
            .ToDictionary(r => r.Gtin, r => (r.Id, Size: CatalogStore.ToSize(r.Dim, r.Amount)), StringComparer.Ordinal);
        var bySku = (await connection.QueryAsync<(string Sku, Guid Id, string Dim, decimal Amount)>(new CommandDefinition("""
                SELECT ms.sku, p.id, p.size_dimension, p.size_base_amount
                FROM raf.market_sku ms JOIN raf.product p ON p.id = ms.product_id
                WHERE ms.market_code = @market
                """, new { market }, cancellationToken: ct)))
            .ToDictionary(r => r.Sku, r => (r.Id, Size: CatalogStore.ToSize(r.Dim, r.Amount)), StringComparer.Ordinal);

        var matched = new Dictionary<Guid, MatchedPrice>();
        var rejected = new List<string>();
        var unmatched = 0;
        foreach (var item in items)
        {
            if (FeedItemRules.Validate(item, now) is { } reason)
            {
                rejected.Add($"{item.Gtin14 ?? item.MarketSku}: {reason}");
                continue;
            }

            var found = item.Gtin14 is not null
                ? byGtin.TryGetValue(item.Gtin14, out var g) ? g : ((Guid Id, UnitSize Size)?)null
                : bySku.TryGetValue(item.MarketSku!, out var s) ? s : null;
            if (found is not { } product)
            {
                unmatched++;
                continue;
            }

            if (UnitSize.TryParse(item.SizeText, out var stated) && stated != product.Size)
            {
                rejected.Add($"{item.Gtin14 ?? item.MarketSku}: feed size {stated} differs from catalogue {product.Size}");
                continue;
            }

            // Two lines for one product in one feed: keep the newest evidence.
            if (!matched.TryGetValue(product.Id, out var existing) || item.ObservedAt > existing.ObservedAt)
            {
                matched[product.Id] = new MatchedPrice(product.Id, item.PriceKurus, item.ObservedAt);
            }
        }

        return ([.. matched.Values], unmatched, rejected);
    }

    private async Task RecordFailureAsync(string marketCode, DateTimeOffset startedAt, int? httpStatus, string error, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO raf.feed_run (market_code, started_at, finished_at, status, http_status, error)
            VALUES (@marketCode, @startedAt, @now, 'failed', @httpStatus, @error)
            """, new { marketCode, startedAt, now, httpStatus, error }, tx);
        var health = await FeedHealthStore.RecordRunAsync(connection, tx, marketCode, succeeded: false, now, error, options.Value.DownAfterFailures);
        await tx.CommitAsync(ct);
        await NotifyHealthAsync(marketCode, health);
    }

    private async Task NotifyHealthAsync(string marketCode, (FeedStatus Before, FeedStatus After, DateTimeOffset? LastSuccessAt) health)
    {
        if (health.Before != health.After)
        {
            await notifier.FeedHealthChangedAsync(new FeedHealthChangedEvent(marketCode, health.After.ToWire(), health.LastSuccessAt));
        }
    }

    private static string Summarise(List<string> rejected) =>
        rejected.Count <= 5 ? string.Join("; ", rejected) : string.Join("; ", rejected.Take(5)) + $"; … {rejected.Count - 5} more";

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed {Market}: {Received} items, {Matched} matched, {Inserted} new observations, {Changed} price changes")]
    private partial void LogFeedApplied(string market, int received, int matched, int inserted, int changed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Feed {Market} unavailable (HTTP {HttpStatus}): {Error}")]
    private partial void LogFeedFailed(string market, int? httpStatus, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Feed {Market}: previous run still in progress, skipped")]
    private partial void LogSkipped(string market);
}

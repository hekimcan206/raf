using System.Diagnostics;
using Microsoft.Extensions.Options;
using Raf.Api.Catalog;
using Raf.Api.Infrastructure;
using Raf.Api.Ingestion;
using Raf.Core.Comparison;
using Raf.Core.Pricing;

namespace Raf.Api.Comparison;

public sealed record FreshnessPolicyDto(double FreshForMinutes, double AgingForMinutes, double ExpireAfterMinutes);

public sealed record FreshnessDto(long FreshKurus, long AgingKurus, long StaleKurus, decimal StaleShare, DateTimeOffset? OldestObservedAt);

public sealed record FeedDto(string Status, DateTimeOffset? LastSuccessAt);

public sealed record QuoteLineDto(
    Guid RequestedProductId, string RequestedName, int Quantity, string Status, Guid? UsedProductId, string? UsedName, string? UsedSize,
    int Packs, long PackPriceKurus, long LineTotalKurus, long? UnitPriceKurus, string? UnitPriceBasis, DateTimeOffset? ObservedAt,
    string? Freshness, string? Note);

public sealed record MarketQuoteDto(
    string Code, string Name, int Rank, long TotalKurus, bool Complete, int MissingLines, int SubstitutedLines,
    FreshnessDto Freshness, FeedDto Feed, IReadOnlyList<QuoteLineDto> Lines);

/// <param name="ComputedAt">When the prices were combined (may be earlier than ServedAt for a cached result).</param>
/// <param name="ServedAt">When freshness was evaluated; the client re-evaluates it as the result ages on the device.</param>
public sealed record ComparisonDto(
    DateTimeOffset ComputedAt,
    DateTimeOffset ServedAt,
    DateTimeOffset ValidUntil,
    string Cache,
    FreshnessPolicyDto Policy,
    string? CheapestCompleteMarket,
    long? SavingsVsMostExpensiveKurus,
    IReadOnlyList<MarketQuoteDto> Markets);

/// <summary>
/// Cache-aside comparison: Redis entry → else load snapshot from PostgreSQL, compare, store guarded by the price
/// epoch. Freshness labels and feed health are always computed per request, never cached.
/// </summary>
internal sealed class ComparisonService
{
    private readonly CatalogStore _catalog;
    private readonly ComparisonCache _cache;
    private readonly FeedHealthStore _health;
    private readonly RafMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly FreshnessPolicy _policy;
    private readonly ComparisonSettings _settings;

    public ComparisonService(
        CatalogStore catalog, ComparisonCache cache, FeedHealthStore health, RafMetrics metrics, TimeProvider clock,
        IOptions<FreshnessOptions> freshness, IOptions<ComparisonSettings> settings)
    {
        _catalog = catalog;
        _cache = cache;
        _health = health;
        _metrics = metrics;
        _clock = clock;
        _policy = freshness.Value.ToPolicy();
        _settings = settings.Value;
    }

    public FreshnessPolicy Policy => _policy;

    public async Task<ComparisonDto> CompareAsync(IReadOnlyCollection<BasketLine> lines, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var key = ComparisonCache.KeyFor(lines);
        string cacheResult;
        BasketComparison? comparison = null;

        if (_settings.CacheEnabled)
        {
            comparison = await _cache.GetAsync(key);
            // ValidUntil guards against serving a result whose prices have expired since it was cached.
            if (comparison is not null && comparison.ValidUntil <= _clock.GetUtcNow())
            {
                comparison = null;
            }
        }

        if (comparison is not null)
        {
            cacheResult = "hit";
        }
        else
        {
            // Epoch first, then prices: if a price changes in between, the epoch moves and the write is refused.
            var epoch = _settings.CacheEnabled ? await _cache.CurrentEpochAsync() : null;
            var snapshot = await _catalog.LoadSnapshotAsync([.. lines.Select(l => l.ProductId).Distinct()], ct);
            var now = _clock.GetUtcNow();
            comparison = BasketComparer.Compare(snapshot, lines, now, _policy, new ComparisonOptions(_settings.MaxOverbuyRatio));
            cacheResult = epoch is null ? "bypass" : "miss";
            if (epoch is not null)
            {
                var ttl = Min(_settings.CacheTtl, comparison.ValidUntil - now);
                await _cache.TrySetAsync(key, comparison, snapshot.Products.Keys, epoch, ttl);
            }
        }

        _metrics.CacheLookup(cacheResult);
        var health = (await _health.GetAllAsync(ct)).ToDictionary(h => h.Code, StringComparer.Ordinal);
        var dto = ToDto(comparison, health, _policy, _clock.GetUtcNow(), cacheResult);
        _metrics.ComparisonDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds, cacheResult);
        return dto;
    }

    internal static ComparisonDto ToDto(
        BasketComparison comparison, IReadOnlyDictionary<string, MarketHealthDto> health, FreshnessPolicy policy, DateTimeOffset now, string cache)
    {
        var markets = comparison.Markets.Select((q, i) =>
        {
            var summary = FreshnessSummary.For(q, policy, now);
            var feed = health.TryGetValue(q.MarketCode, out var h) ? new FeedDto(h.Status, h.LastSuccessAt) : new FeedDto("unknown", null);
            return new MarketQuoteDto(
                q.MarketCode, q.MarketName, i + 1, q.TotalKurus, q.Complete, q.MissingLines, q.SubstitutedLines,
                new FreshnessDto(summary.FreshKurus, summary.AgingKurus, summary.StaleKurus, summary.StaleShare, summary.OldestObservedAt),
                feed,
                [.. q.Lines.Select((l, j) => new QuoteLineDto(
                    l.RequestedProductId, l.RequestedName, l.Quantity, l.Status.ToString().ToLowerInvariant(), l.UsedProductId, l.UsedName, l.UsedSize,
                    l.Packs, l.PackPriceKurus, l.LineTotalKurus, l.UnitPriceKurus, l.UnitPriceBasis, l.ObservedAt,
                    summary.LineFreshness[j]?.ToString().ToLowerInvariant(), l.Note))]);
        }).ToList();

        var complete = markets.Where(m => m.Complete).ToList();
        return new ComparisonDto(
            comparison.ComputedAt,
            now,
            comparison.ValidUntil,
            cache,
            new FreshnessPolicyDto(policy.FreshFor.TotalMinutes, policy.AgingFor.TotalMinutes, policy.ExpireAfter.TotalMinutes),
            complete.FirstOrDefault()?.Code,
            complete.Count >= 2 ? complete[^1].TotalKurus - complete[0].TotalKurus : null,
            markets);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

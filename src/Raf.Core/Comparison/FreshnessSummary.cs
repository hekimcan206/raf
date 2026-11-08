using Raf.Core.Pricing;

namespace Raf.Core.Comparison;

/// <summary>
/// How much of a market total rests on old evidence, computed at read time. "Stale share" is by money, not by line
/// count: one stale olive-oil price matters more than one stale bread price.
/// </summary>
public sealed record FreshnessSummary(
    long FreshKurus,
    long AgingKurus,
    long StaleKurus,
    decimal StaleShare,
    DateTimeOffset? OldestObservedAt,
    IReadOnlyList<Freshness?> LineFreshness)
{
    public static FreshnessSummary For(MarketQuote quote, FreshnessPolicy policy, DateTimeOffset now)
    {
        long fresh = 0, aging = 0, stale = 0;
        DateTimeOffset? oldest = null;
        var perLine = new List<Freshness?>(quote.Lines.Count);
        foreach (var line in quote.Lines)
        {
            if (line.ObservedAt is not { } observedAt)
            {
                perLine.Add(null);
                continue;
            }

            var freshness = policy.Classify(observedAt, now);
            perLine.Add(freshness);
            oldest = oldest is null || observedAt < oldest ? observedAt : oldest;
            switch (freshness)
            {
                case Freshness.Fresh:
                    fresh += line.LineTotalKurus;
                    break;
                case Freshness.Aging:
                    aging += line.LineTotalKurus;
                    break;
                default:
                    // Expired can only appear if a cached result is read just past ValidUntil; count it as stale.
                    stale += line.LineTotalKurus;
                    break;
            }
        }

        var total = fresh + aging + stale;
        var share = total == 0 ? 0m : Math.Round((decimal)stale / total, 4, MidpointRounding.AwayFromZero);
        return new FreshnessSummary(fresh, aging, stale, share, oldest, perLine);
    }
}

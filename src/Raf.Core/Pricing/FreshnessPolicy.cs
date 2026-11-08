namespace Raf.Core.Pricing;

/// <summary>How much a price can be trusted, judged only by how old the evidence for it is.</summary>
public enum Freshness
{
    /// <summary>Recent enough to act on without a second thought.</summary>
    Fresh,

    /// <summary>Probably still right, but the shopper should know it is not from today's feed.</summary>
    Aging,

    /// <summary>Still used in totals (it is the best evidence we have) but called out in the UI and in the stale share.</summary>
    Stale,

    /// <summary>Too old to use at all: treated as missing, so the comparison substitutes or reports the gap.</summary>
    Expired,
}

/// <summary>
/// Age thresholds for price evidence. Age is measured from <c>observed_at</c> (when the market says the price was
/// valid), not from when we ingested it: re-reading an old feed must not make its prices look new.
/// See docs/adr/0001-stale-prices-and-disconnection.md.
/// </summary>
public sealed record FreshnessPolicy(TimeSpan FreshFor, TimeSpan AgingFor, TimeSpan ExpireAfter)
{
    /// <summary>Grocery shelf prices change at most daily in practice; a week-old price is no longer evidence.</summary>
    public static FreshnessPolicy Default { get; } = new(TimeSpan.FromHours(6), TimeSpan.FromHours(24), TimeSpan.FromDays(7));

    public Freshness Classify(DateTimeOffset observedAt, DateTimeOffset now)
    {
        // A feed clock slightly ahead of ours is not "fresher than fresh": negative ages count as zero.
        var age = now - observedAt;
        if (age <= FreshFor)
        {
            return Freshness.Fresh;
        }

        if (age <= AgingFor)
        {
            return Freshness.Aging;
        }

        return age <= ExpireAfter ? Freshness.Stale : Freshness.Expired;
    }

    public bool IsUsable(DateTimeOffset observedAt, DateTimeOffset now) => Classify(observedAt, now) != Freshness.Expired;

    /// <summary>The moment a price observed at <paramref name="observedAt"/> stops being usable.</summary>
    public DateTimeOffset ExpiresAt(DateTimeOffset observedAt) => observedAt + ExpireAfter;

    public void Validate()
    {
        if (FreshFor <= TimeSpan.Zero || AgingFor <= FreshFor || ExpireAfter <= AgingFor)
        {
            throw new InvalidOperationException("Freshness thresholds must be positive and strictly increasing: fresh < aging < expire.");
        }
    }
}

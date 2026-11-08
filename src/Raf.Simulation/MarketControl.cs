using System.Collections.Concurrent;

namespace Raf.Simulation;

public sealed record PriceOverride(long PriceKurus, DateTimeOffset SetAt);

/// <summary>What a request to a simulated feed should experience.</summary>
public enum FeedBehaviour
{
    Normal,
    Outage,
    RandomFailure,
    Hang,
}

/// <summary>
/// Runtime knobs for the simulator: forced outages and manual price changes (admin endpoints), and the chaos dice
/// for random failures, hangs and latency. Chaos can be switched off so tests and benchmarks are deterministic.
/// </summary>
public sealed class MarketControl(TimeProvider clock, bool chaosEnabled, int seed = 2026)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _outageUntil = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Market, string Gtin), PriceOverride> _overrides = new();
    private readonly Random _random = new(seed);
    private readonly Lock _randomLock = new();

    public bool ChaosEnabled { get; } = chaosEnabled;

    public void StartOutage(string market, TimeSpan duration) => _outageUntil[market] = clock.GetUtcNow() + duration;

    public void EndOutage(string market) => _outageUntil.TryRemove(market, out _);

    public DateTimeOffset? OutageUntil(string market) =>
        _outageUntil.TryGetValue(market, out var until) && until > clock.GetUtcNow() ? until : null;

    public void SetPrice(string market, string gtin14, long priceKurus) =>
        _overrides[(market, gtin14)] = new PriceOverride(priceKurus, clock.GetUtcNow());

    public void ClearPrices(string market)
    {
        foreach (var key in _overrides.Keys.Where(k => k.Market == market))
        {
            _overrides.TryRemove(key, out _);
        }
    }

    public PriceOverride? OverrideFor(string market, string gtin14) =>
        _overrides.TryGetValue((market, gtin14), out var o) ? o : null;

    /// <summary>Decides the fate of one feed request: forced outage, maintenance window, or (with chaos) a random failure or hang.</summary>
    public (FeedBehaviour Behaviour, TimeSpan Latency) NextRequest(MarketProfile market)
    {
        var now = clock.GetUtcNow();
        if (OutageUntil(market.Code) is not null)
        {
            return (FeedBehaviour.Outage, TimeSpan.Zero);
        }

        if (!ChaosEnabled)
        {
            return (FeedBehaviour.Normal, TimeSpan.Zero);
        }

        if (market.ScheduledOutageMinutes is var (from, to) && now.Minute >= from && now.Minute <= to)
        {
            return (FeedBehaviour.Outage, TimeSpan.Zero);
        }

        double roll, jitter;
        lock (_randomLock)
        {
            roll = _random.NextDouble();
            jitter = _random.NextDouble();
        }

        var latency = market.BaseLatency + (market.LatencyJitter * jitter);
        if (roll < market.HangRate)
        {
            return (FeedBehaviour.Hang, TimeSpan.FromSeconds(12));
        }

        return roll < market.HangRate + market.FailureRate ? (FeedBehaviour.RandomFailure, latency) : (FeedBehaviour.Normal, latency);
    }
}

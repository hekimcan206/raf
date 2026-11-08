using System.Text;
using Raf.Core.Feeds;

namespace Raf.Simulation;

/// <summary>A price as a simulated market publishes it.</summary>
public sealed record SimPrice(SimProduct Product, long PriceKurus, DateTimeOffset ObservedAt);

/// <summary>
/// Deterministic, stateless price generator: the same market, product and time always give the same price, so the
/// simulator needs no database, restarts are invisible, and tests can predict values. Prices move in steps at
/// "publication epochs" (each market's republish interval) with a fixed probability, like a shelf re-price.
/// Manual overrides (<see cref="MarketControl"/>) win over the model so demos and tests can force a change.
/// </summary>
public sealed class PriceModel(MarketControl control)
{
    private const int ChangePercent = 20;     // chance that a given epoch re-prices a given item
    private const int MaxLookbackEpochs = 60;

    public IReadOnlyList<SimPrice> PriceList(MarketProfile market, DateTimeOffset now)
    {
        var list = new List<SimPrice>(SyntheticCatalog.Products.Count);
        foreach (var product in SyntheticCatalog.Products)
        {
            if (!market.Unlisted.Contains(product.Index))
            {
                list.Add(PriceOf(market, product, now));
            }
        }

        return list;
    }

    public SimPrice PriceOf(MarketProfile market, SimProduct product, DateTimeOffset now)
    {
        if (control.OverrideFor(market.Code, product.Gtin14) is { } manual)
        {
            // A republishing market re-confirms an overridden price at each publication, like any other price.
            var confirmed = market.Code == MarketProfiles.Tazelik.Code ? manual.SetAt : Max(manual.SetAt, PublicationTime(market, now));
            return new SimPrice(product, manual.PriceKurus, confirmed);
        }

        var epoch = EpochOf(market, now);

        if (market.Dormant.TryGetValue(product.Index, out var days))
        {
            // Untouched for N days: the price and its timestamp are frozen at the start of that day.
            var frozen = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-days);
            return new SimPrice(product, Shelf(market, product, LevelAt(market, product, EpochOf(market, frozen))), frozen);
        }

        var (level, changedEpoch) = LevelAndChange(market, product, epoch);
        var observedAt = market.Code == MarketProfiles.Tazelik.Code
            // Tazelik reports when the price last changed, not when it last published the list.
            ? StartOf(market, changedEpoch)
            : PublicationTime(market, now);
        return new SimPrice(product, Shelf(market, product, level), observedAt);
    }

    /// <summary>When the market last issued its list: floor(now, period); nightly lists at 03:00 Turkey time.</summary>
    public static DateTimeOffset PublicationTime(MarketProfile market, DateTimeOffset now) => StartOf(market, EpochOf(market, now));

    private static long EpochOf(MarketProfile market, DateTimeOffset t) =>
        (t.ToUnixTimeSeconds() - OffsetSeconds(market)) / (long)market.RepublishEvery.TotalSeconds;

    private static DateTimeOffset StartOf(MarketProfile market, long epoch) =>
        DateTimeOffset.FromUnixTimeSeconds((epoch * (long)market.RepublishEvery.TotalSeconds) + OffsetSeconds(market));

    // Daily lists are generated at 03:00 local (UTC+3) = 00:00 UTC; shift the epoch grid so it starts there.
    private static long OffsetSeconds(MarketProfile market) =>
        market.RepublishEvery >= TimeSpan.FromDays(1) ? (long)(TimeSpan.FromHours(3) - FeedFormats.TurkeyOffset).TotalSeconds : 0;

    private static int LevelAt(MarketProfile market, SimProduct product, long epoch) => LevelAndChange(market, product, epoch).Level;

    /// <summary>The price level (-1..2 steps of 3 %) in force at an epoch, and the epoch it was set.</summary>
    private static (int Level, long ChangedEpoch) LevelAndChange(MarketProfile market, SimProduct product, long epoch)
    {
        for (var e = epoch; e > epoch - MaxLookbackEpochs; e--)
        {
            if (Hash(market.Code, product.Gtin14, e, "change") % 100 < ChangePercent)
            {
                return ((int)(Hash(market.Code, product.Gtin14, e, "level") % 4) - 1, e);
            }
        }

        return (0, epoch - MaxLookbackEpochs + 1);
    }

    /// <summary>Reference price × market factor × level × a fixed per-item spread, rounded to a shelf-style x.20/x.45/x.70/x.95.</summary>
    private static long Shelf(MarketProfile market, SimProduct product, int level)
    {
        var spread = 1m + (((decimal)(Hash(market.Code, product.Gtin14, 0, "spread") % 81) - 40m) / 1000m); // ±4 %
        var raw = product.BasePriceKurus * market.PriceFactor * (1m + (0.03m * level)) * spread;
        var rounded = (long)Math.Round(raw / 25m, MidpointRounding.AwayFromZero) * 25;
        return Math.Max(rounded - 5, 95);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>FNV-1a 64: stable across processes and runtimes, unlike string.GetHashCode.</summary>
    internal static ulong Hash(string market, string gtin, long epoch, string salt)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes($"{market}|{gtin}|{epoch}|{salt}"))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}

namespace Raf.Simulation;

/// <summary>How a simulated market behaves on the wire and how it prices.</summary>
/// <param name="RepublishEvery">How often the market re-issues its whole price list (and so re-confirms every price).</param>
/// <param name="ScheduledOutageMinutes">Minutes past each hour during which the feed answers 503 (maintenance window).</param>
/// <param name="Unlisted">Catalogue indexes this market does not sell; drives substitutions and missing lines.</param>
/// <param name="Dormant">Index → days since the market last touched the price; those items keep an old observed_at.</param>
public sealed record MarketProfile(
    string Code,
    string Name,
    string FeedPath,
    string ContentType,
    decimal PriceFactor,
    TimeSpan RepublishEvery,
    TimeSpan BaseLatency,
    TimeSpan LatencyJitter,
    double FailureRate,
    double HangRate,
    (int From, int To)? ScheduledOutageMinutes,
    IReadOnlySet<int> Unlisted,
    IReadOnlyDictionary<int, int> Dormant);

public static class MarketProfiles
{
    /// <summary>Bereket: fast and reliable JSON; re-confirms every price every 10 minutes.</summary>
    public static MarketProfile Bereket { get; } = new(
        "bereket", "Bereket Market", "/feeds/bereket/prices.json", "application/json",
        1.00m, TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(80),
        FailureRate: 0.01, HangRate: 0, ScheduledOutageMinutes: null,
        Unlisted: new HashSet<int> { 2, 35 }, Dormant: new Dictionary<int, int>());

    /// <summary>Çarşı: the discounter. CSV snapshot every 15 minutes, slow and flaky (about one request in ten fails).</summary>
    public static MarketProfile Carsi { get; } = new(
        "carsi", "Çarşı Express", "/feeds/carsi/fiyatlar.csv", "text/csv; charset=utf-8",
        0.95m, TimeSpan.FromMinutes(15), TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(400),
        FailureRate: 0.10, HangRate: 0, ScheduledOutageMinutes: null,
        Unlisted: new HashSet<int> { 9, 22, 32 }, Dormant: new Dictionary<int, int>());

    /// <summary>Kuzey: nightly XML list generated at 03:00, so its prices age during the day; maintenance at :40–:44 every hour.</summary>
    public static MarketProfile Kuzey { get; } = new(
        "kuzey", "Kuzey Gıda", "/feeds/kuzey/fiyat-listesi.xml", "application/xml",
        1.05m, TimeSpan.FromDays(1), TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(600),
        FailureRate: 0.02, HangRate: 0, ScheduledOutageMinutes: (40, 44),
        Unlisted: new HashSet<int> { 30, 13 }, Dormant: new Dictionary<int, int>());

    /// <summary>
    /// Tazelik: paged JSON keyed by its own SKUs. Each item carries the time its price last changed, and part of the
    /// range has not been touched for days (stale), one item for longer than the expiry window. Highly variable latency
    /// and occasional hangs longer than the client timeout.
    /// </summary>
    public static MarketProfile Tazelik { get; } = new(
        "tazelik", "Tazelik", "/feeds/tazelik/products", "application/json",
        0.98m, TimeSpan.FromHours(6), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(2200),
        FailureRate: 0.03, HangRate: 0.03, ScheduledOutageMinutes: null,
        Unlisted: new HashSet<int> { 6, 27, 36 },
        Dormant: new Dictionary<int, int> { [4] = 2, [12] = 3, [19] = 2, [24] = 4, [26] = 3, [15] = 9 });

    public static IReadOnlyList<MarketProfile> All { get; } = [Bereket, Carsi, Kuzey, Tazelik];

    public static MarketProfile Get(string code) =>
        All.FirstOrDefault(m => m.Code == code) ?? throw new KeyNotFoundException($"Unknown market '{code}'.");
}

namespace Raf.Core.Feeds;

/// <summary>
/// One price line from a market feed, after format-specific parsing and before product matching.
/// Exactly one of <see cref="Gtin14"/> (barcode feeds) or <see cref="MarketSku"/> (feeds keyed by the market's own
/// item code) identifies the product.
/// </summary>
public sealed record FeedItem(string? Gtin14, string? MarketSku, string? Name, string? SizeText, long PriceKurus, DateTimeOffset ObservedAt);

/// <param name="Rejected">Lines that could not be parsed, with the reason; counted in feed health, never silently dropped.</param>
/// <param name="TotalPages">For paged feeds: how many pages the feed has. 1 for single-document feeds.</param>
public sealed record FeedPage(IReadOnlyList<FeedItem> Items, IReadOnlyList<string> Rejected, int TotalPages = 1);

/// <summary>A market's wire format. Parsers are pure (stream in, items out) so every format has golden tests.</summary>
public interface IFeedFormat
{
    /// <summary>The market this format belongs to.</summary>
    string MarketCode { get; }

    /// <summary>True when the feed is paged (<c>?page=n</c>) and the fetcher must follow <see cref="FeedPage.TotalPages"/>.</summary>
    bool IsPaged { get; }

    FeedPage Parse(Stream content);
}

/// <summary>Sanity rules applied to every parsed item regardless of format.</summary>
public static class FeedItemRules
{
    /// <summary>No grocery item costs more than 100 000 TL; a bigger number is a unit error (kuruş sent as lira, etc.).</summary>
    public const long MaxPriceKurus = 10_000_000;

    /// <summary>Feed clocks drift; anything further ahead than this is rejected instead of looking "fresher than now".</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static string? Validate(FeedItem item, DateTimeOffset now)
    {
        if (item.PriceKurus <= 0)
        {
            return "price must be positive";
        }

        if (item.PriceKurus > MaxPriceKurus)
        {
            return "price implausibly high";
        }

        if (item.ObservedAt > now + MaxClockSkew)
        {
            return "observed_at is in the future";
        }

        return item.Gtin14 is null && string.IsNullOrWhiteSpace(item.MarketSku) ? "no product identifier" : null;
    }
}

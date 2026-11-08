namespace Raf.Core.Comparison;

/// <summary>Everything a comparison needs, loaded once: markets, the products involved and their latest prices.</summary>
public sealed class PriceSnapshot
{
    private readonly Dictionary<(string Market, Guid Product), MarketPrice> _prices;
    private readonly ILookup<string, CatalogProduct> _groups;

    public PriceSnapshot(IEnumerable<Market> markets, IEnumerable<CatalogProduct> products, IEnumerable<MarketPrice> prices)
    {
        Markets = [.. markets.OrderBy(m => m.Code, StringComparer.Ordinal)];
        Products = products.ToDictionary(p => p.Id);
        _prices = [];
        foreach (var price in prices)
        {
            // Defensive: if two rows for one market/product slip in, the newest evidence wins.
            var key = (price.MarketCode, price.ProductId);
            if (!_prices.TryGetValue(key, out var existing) || price.ObservedAt > existing.ObservedAt)
            {
                _prices[key] = price;
            }
        }

        _groups = Products.Values
            .Where(p => p.SubstitutionGroup is not null)
            .ToLookup(p => p.SubstitutionGroup!, StringComparer.Ordinal);
    }

    public IReadOnlyList<Market> Markets { get; }

    public IReadOnlyDictionary<Guid, CatalogProduct> Products { get; }

    public MarketPrice? PriceOf(string marketCode, Guid productId) =>
        _prices.TryGetValue((marketCode, productId), out var price) ? price : null;

    public IEnumerable<CatalogProduct> GroupMembers(string group) => _groups[group];
}

using Raf.Core.Catalog;

namespace Raf.Core.Comparison;

public sealed record Market(string Code, string Name);

/// <param name="SubstitutionGroup">
/// Products in one group are acceptable stand-ins for each other (for example "UHT whole milk"), across brands and
/// pack sizes. Curated, not inferred from names: see docs/adr/0002-product-matching-and-unit-normalisation.md.
/// </param>
public sealed record CatalogProduct(Guid Id, string Gtin, string Name, string Brand, UnitSize Size, string? SubstitutionGroup);

/// <summary>The latest known shelf price of one product in one market, and when the market said it was valid.</summary>
public sealed record MarketPrice(string MarketCode, Guid ProductId, long PriceKurus, DateTimeOffset ObservedAt);

public sealed record BasketLine(Guid ProductId, int Quantity, bool AllowSubstitutes = true);

public enum LineStatus
{
    /// <summary>The requested product, priced in this market.</summary>
    Exact,

    /// <summary>The requested product has no usable price here; a product from the same group stands in.</summary>
    Substituted,

    /// <summary>Neither the product nor an acceptable substitute has a usable price here.</summary>
    Missing,
}

/// <param name="Packs">How many packs of the used product cover the requested amount (more than the quantity when a smaller pack substitutes).</param>
/// <param name="UnitPriceKurus">Normalised price per <paramref name="UnitPriceBasis"/> (kg, L or piece), for comparing across pack sizes.</param>
/// <param name="ObservedAt">When the market last stated this price; drives freshness. Null for missing lines.</param>
public sealed record QuoteLine(
    Guid RequestedProductId,
    string RequestedName,
    int Quantity,
    LineStatus Status,
    Guid? UsedProductId,
    string? UsedName,
    string? UsedSize,
    int Packs,
    long PackPriceKurus,
    long LineTotalKurus,
    long? UnitPriceKurus,
    string? UnitPriceBasis,
    DateTimeOffset? ObservedAt,
    string? Note);

public sealed record MarketQuote(
    string MarketCode,
    string MarketName,
    long TotalKurus,
    bool Complete,
    int MissingLines,
    int SubstitutedLines,
    IReadOnlyList<QuoteLine> Lines);

/// <summary>
/// The price part of a comparison. It deliberately carries no freshness labels: those depend on the time the result
/// is read, so they are derived on every read (<see cref="FreshnessSummary"/>), which keeps a cached comparison honest.
/// </summary>
/// <param name="ValidUntil">The first moment one of the used prices expires; the result must not be served after it.</param>
public sealed record BasketComparison(IReadOnlyList<MarketQuote> Markets, DateTimeOffset ComputedAt, DateTimeOffset ValidUntil);

/// <summary>A basket line could not be compared because the product is unknown.</summary>
public sealed class UnknownProductException(IReadOnlyList<Guid> productIds)
    : Exception($"Unknown product(s): {string.Join(", ", productIds)}")
{
    public IReadOnlyList<Guid> ProductIds { get; } = productIds;
}

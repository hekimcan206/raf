using Dapper;
using Npgsql;
using Raf.Core.Catalog;
using Raf.Core.Comparison;

namespace Raf.Api.Catalog;

public sealed record ProductDto(
    Guid Id, string Gtin, string Name, string Brand, string Category, string Size, string UnitPriceBasis,
    string? SubstitutionGroup, long? LowestPriceKurus, int MarketsListing);

public sealed record PricePointDto(string MarketCode, long PriceKurus, DateTimeOffset ObservedAt, string Source);

/// <summary>Read side of the catalogue and price data. All SQL is parameterised; the API role cannot write here.</summary>
internal sealed class CatalogStore(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<ProductDto>> SearchAsync(string? query, string? category, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ProductRow>(new CommandDefinition("""
            SELECT p.id, p.gtin, p.name, p.brand, p.category, p.size_dimension AS SizeDimension, p.size_base_amount AS SizeBaseAmount,
                   p.substitution_group AS SubstitutionGroup, min(cp.price_kurus) AS LowestPriceKurus, count(cp.market_code)::int AS MarketsListing
            FROM raf.product p
            LEFT JOIN raf.current_price cp ON cp.product_id = p.id
            WHERE (@query IS NULL OR lower(p.name) LIKE '%' || lower(@query) || '%' OR p.gtin LIKE '%' || @query)
              AND (@category IS NULL OR p.category = @category)
            GROUP BY p.id
            ORDER BY p.category, p.name
            LIMIT @limit
            """, new { query = string.IsNullOrWhiteSpace(query) ? null : query.Trim(), category, limit }, cancellationToken: ct));
        return [.. rows.Select(r => r.ToDto())];
    }

    public async Task<IReadOnlyList<PricePointDto>> HistoryAsync(Guid productId, int days, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<HistoryRow>(new CommandDefinition("""
            SELECT market_code AS MarketCode, price_kurus AS PriceKurus, observed_at AS ObservedAt, source AS Source
            FROM raf.price_observation
            WHERE product_id = @productId AND observed_at >= now() - make_interval(days => @days)
            ORDER BY observed_at, market_code
            """, new { productId, days }, cancellationToken: ct));
        return [.. rows.Select(r => new PricePointDto(r.MarketCode, r.PriceKurus, new DateTimeOffset(r.ObservedAt, TimeSpan.Zero), r.Source))];
    }

    public async Task<bool> ProductExistsAsync(Guid productId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM raf.product WHERE id = @productId)", new { productId }, cancellationToken: ct));
    }

    /// <summary>
    /// The requested products, every member of their substitution groups, and the current prices of all of them,
    /// in one round trip.
    /// </summary>
    public async Task<PriceSnapshot> LoadSnapshotAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition("""
            SELECT code, name FROM raf.market WHERE active ORDER BY code;

            SELECT p.id, p.gtin, p.name, p.brand, p.category, p.size_dimension AS SizeDimension, p.size_base_amount AS SizeBaseAmount,
                   p.substitution_group AS SubstitutionGroup, NULL::bigint AS LowestPriceKurus, 0 AS MarketsListing
            FROM raf.product p
            WHERE p.id = ANY(@ids)
               OR p.substitution_group IN (SELECT substitution_group FROM raf.product WHERE id = ANY(@ids) AND substitution_group IS NOT NULL);

            SELECT cp.market_code AS MarketCode, cp.product_id AS ProductId, cp.price_kurus AS PriceKurus, cp.observed_at AS ObservedAt
            FROM raf.current_price cp
            JOIN raf.product p ON p.id = cp.product_id
            WHERE p.id = ANY(@ids)
               OR p.substitution_group IN (SELECT substitution_group FROM raf.product WHERE id = ANY(@ids) AND substitution_group IS NOT NULL);
            """, new { ids = productIds.ToArray() }, cancellationToken: ct));

        var markets = (await grid.ReadAsync<MarketRow>()).Select(m => new Market(m.Code, m.Name)).ToList();
        var products = (await grid.ReadAsync<ProductRow>()).Select(r => r.ToCatalogProduct()).ToList();
        var prices = (await grid.ReadAsync<PriceRow>())
            .Select(r => new MarketPrice(r.MarketCode, r.ProductId, r.PriceKurus, new DateTimeOffset(r.ObservedAt, TimeSpan.Zero)))
            .ToList();
        return new PriceSnapshot(markets, products, prices);
    }

    internal static UnitSize ToSize(string dimension, decimal amount) => new(dimension switch
    {
        "mass" => Dimension.Mass,
        "volume" => Dimension.Volume,
        _ => Dimension.Count,
    }, amount);

    internal static string ToDimension(Dimension dimension) => dimension switch
    {
        Dimension.Mass => "mass",
        Dimension.Volume => "volume",
        _ => "count",
    };

    private sealed record MarketRow(string Code, string Name);

    private sealed class ProductRow
    {
        public Guid Id { get; init; }

        public string Gtin { get; init; } = "";

        public string Name { get; init; } = "";

        public string Brand { get; init; } = "";

        public string Category { get; init; } = "";

        public string SizeDimension { get; init; } = "";

        public decimal SizeBaseAmount { get; init; }

        public string? SubstitutionGroup { get; init; }

        public long? LowestPriceKurus { get; init; }

        public int MarketsListing { get; init; }

        public CatalogProduct ToCatalogProduct() =>
            new(Id, Gtin, Name, Brand, ToSize(SizeDimension, SizeBaseAmount), SubstitutionGroup);

        public ProductDto ToDto()
        {
            var size = ToSize(SizeDimension, SizeBaseAmount);
            return new ProductDto(Id, Core.Catalog.Gtin.Display(Gtin), Name, Brand, Category, size.ToString(), size.UnitPriceBasis,
                SubstitutionGroup, LowestPriceKurus, MarketsListing);
        }
    }

    private sealed class PriceRow
    {
        public string MarketCode { get; init; } = "";

        public Guid ProductId { get; init; }

        public long PriceKurus { get; init; }

        public DateTime ObservedAt { get; init; }
    }

    private sealed class HistoryRow
    {
        public string MarketCode { get; init; } = "";

        public long PriceKurus { get; init; }

        public DateTime ObservedAt { get; init; }

        public string Source { get; init; } = "";
    }
}

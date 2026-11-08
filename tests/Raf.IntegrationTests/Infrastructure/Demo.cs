using Raf.Api.Demo;
using Raf.Simulation;

namespace Raf.IntegrationTests.Infrastructure;

/// <summary>Catalogue lookups by the simulator's product index, mapped to the ids the seed uses.</summary>
public static class Demo
{
    public static Guid Id(int index) => DemoSeeder.ProductId(SyntheticCatalog.ByIndex(index).Gtin14);

    public static string Gtin(int index) => SyntheticCatalog.ByIndex(index).Gtin14;

    public static string Sku(int index) => SyntheticCatalog.ByIndex(index).TazelikSku;

    public static Guid BasketId => DemoSeeder.DemoBasketId;

    /// <summary>A price different from anything the model produces (models end in 20/45/70/95 kuruş).</summary>
    public static long UniquePrice() => 10_000 + (Random.Shared.Next(1, 4_000) * 25) + 1;
}

public sealed record ComparisonResponse(string Cache, string? CheapestCompleteMarket, IReadOnlyList<MarketResponse> Markets);

public sealed record MarketResponse(string Code, int Rank, long TotalKurus, bool Complete, int MissingLines, int SubstitutedLines,
    FreshnessResponse Freshness, IReadOnlyList<LineResponse> Lines);

public sealed record FreshnessResponse(long FreshKurus, long AgingKurus, long StaleKurus, decimal StaleShare);

public sealed record LineResponse(Guid RequestedProductId, string Status, Guid? UsedProductId, int Packs, long PackPriceKurus,
    long LineTotalKurus, string? Freshness, string? Note);

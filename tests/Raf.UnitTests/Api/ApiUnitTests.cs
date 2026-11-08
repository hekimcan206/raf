using Raf.Api.Comparison;
using Raf.Api.Demo;
using Raf.Api.Ingestion;
using Raf.Api.Persistence;
using Raf.Core.Comparison;
using Raf.Core.Pricing;

namespace Raf.UnitTests.Api;

public sealed class ApiUnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cache_key_ignores_line_order_and_merges_duplicates()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var k1 = ComparisonCache.KeyFor([new(a, 2), new(b, 1)]);
        var k2 = ComparisonCache.KeyFor([new(b, 1), new(a, 1), new(a, 1)]);
        k1.Should().Be(k2).And.StartWith("raf:cmp:v1:");
    }

    [Fact]
    public void Cache_key_differs_when_substitution_preference_differs()
    {
        var a = Guid.NewGuid();
        ComparisonCache.KeyFor([new(a, 1)]).Should().NotBe(ComparisonCache.KeyFor([new(a, 1, AllowSubstitutes: false)]));
    }

    [Fact]
    public void Dto_reports_cheapest_complete_market_and_savings_and_evaluates_freshness_at_read_time()
    {
        var line = new QuoteLine(Guid.NewGuid(), "p", 1, LineStatus.Exact, Guid.NewGuid(), "p", "1 L", 1, 1000, 1000, 1000, "L", Now.AddHours(-30), null);
        var comparison = new BasketComparison(
            [
                new MarketQuote("a", "A", 1000, true, 0, 0, [line]),
                new MarketQuote("b", "B", 1500, true, 0, 0, [line with { LineTotalKurus = 1500, PackPriceKurus = 1500 }]),
                new MarketQuote("c", "C", 500, false, 1, 0, [line with { LineTotalKurus = 500, PackPriceKurus = 500 }]),
            ],
            Now.AddHours(-1),
            Now.AddDays(5));
        var health = new Dictionary<string, MarketHealthDto>
        {
            ["a"] = new("a", "A", "healthy", Now, Now, 0, null),
        };

        var dto = ComparisonService.ToDto(comparison, health, FreshnessPolicy.Default, Now, "hit");

        dto.CheapestCompleteMarket.Should().Be("a");
        dto.SavingsVsMostExpensiveKurus.Should().Be(500);
        dto.Markets.Select(m => m.Rank).Should().Equal(1, 2, 3);
        dto.Markets[0].Feed.Status.Should().Be("healthy");
        dto.Markets[1].Feed.Status.Should().Be("unknown");
        dto.Markets[0].Lines[0].Freshness.Should().Be("stale");
        dto.Markets[0].Freshness.StaleShare.Should().Be(1m);
        dto.Policy.FreshForMinutes.Should().Be(360);
    }

    [Fact]
    public void Demo_product_ids_are_stable_and_distinct()
    {
        var ids = Raf.Simulation.SyntheticCatalog.Products.Select(p => DemoSeeder.ProductId(p.Gtin14)).ToList();
        ids.Should().OnlyHaveUniqueItems();
        DemoSeeder.ProductId("02000000000015").Should().Be(DemoSeeder.ProductId("02000000000015"));
    }

    [Fact]
    public void Migrations_are_embedded_and_numbered()
    {
        var migrations = MigrationRunner.Load();
        migrations.Should().NotBeEmpty();
        migrations.Select(m => m.Version).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        migrations[0].Sql.Should().Contain("price_observation_once");
    }
}

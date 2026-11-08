using Raf.Core.Catalog;
using Raf.Core.Comparison;
using Raf.Core.Pricing;

namespace Raf.UnitTests.Comparison;

public sealed class BasketComparerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly FreshnessPolicy Policy = FreshnessPolicy.Default;

    private static readonly CatalogProduct MilkA1L = P("Milk A 1 L", "1 L", "milk");
    private static readonly CatalogProduct MilkB1L = P("Milk B 1 L", "1 L", "milk");
    private static readonly CatalogProduct MilkA500 = P("Milk A 500 ml", "500 ml", "milk");
    private static readonly CatalogProduct Oil1L = P("Olive oil 1 L", "1 L", "olive");
    private static readonly CatalogProduct Oil2L = P("Olive oil 2 L", "2 L", "olive");
    private static readonly CatalogProduct Soap = P("Dish soap", "750 ml", null);
    private static readonly CatalogProduct Rice1Kg = P("Rice 1 kg", "1 kg", "milk"); // wrong dimension in the group on purpose

    private static readonly Market A = new("a", "Market A");
    private static readonly Market B = new("b", "Market B");

    [Fact]
    public void Prices_exact_products_with_unit_prices()
    {
        var snapshot = Snapshot([A], Price(A, MilkA1L, 4000), Price(A, Soap, 6000));
        var result = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 2), new(Soap.Id, 1)], Now, Policy);

        var quote = result.Markets.Single();
        quote.TotalKurus.Should().Be(14_000);
        quote.Complete.Should().BeTrue();
        var milk = quote.Lines.Single(l => l.RequestedProductId == MilkA1L.Id);
        milk.Status.Should().Be(LineStatus.Exact);
        milk.LineTotalKurus.Should().Be(8_000);
        milk.UnitPriceKurus.Should().Be(4_000);
        milk.UnitPriceBasis.Should().Be("L");
        var soap = quote.Lines.Single(l => l.RequestedProductId == Soap.Id);
        soap.UnitPriceKurus.Should().Be(8_000); // 60 TL / 0.75 L
    }

    [Fact]
    public void Substitutes_another_brand_when_the_product_is_not_listed()
    {
        var snapshot = Snapshot([A], Price(A, MilkB1L, 4200));
        var line = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 2)], Now, Policy).Markets[0].Lines[0];

        line.Status.Should().Be(LineStatus.Substituted);
        line.UsedProductId.Should().Be(MilkB1L.Id);
        line.Packs.Should().Be(2);
        line.LineTotalKurus.Should().Be(8_400);
        line.Note.Should().Contain("not listed");
    }

    [Fact]
    public void Substitutes_smaller_packs_covering_the_requested_amount()
    {
        var snapshot = Snapshot([A], Price(A, MilkA500, 2300));
        var line = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 3)], Now, Policy).Markets[0].Lines[0];

        line.Packs.Should().Be(6); // 3 × 1 L = 6 × 500 ml
        line.LineTotalKurus.Should().Be(13_800);
    }

    [Fact]
    public void Picks_the_cheapest_line_total_among_substitutes()
    {
        var snapshot = Snapshot([A], Price(A, MilkB1L, 4200), Price(A, MilkA500, 2000));
        var line = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1)], Now, Policy).Markets[0].Lines[0];

        line.UsedProductId.Should().Be(MilkA500.Id); // 2 × 20.00 < 1 × 42.00
    }

    [Fact]
    public void Refuses_a_substitute_that_overbuys_beyond_the_cap()
    {
        // 1 L requested, only 2 L available: 100 % extra exceeds the default 50 % cap.
        var snapshot = Snapshot([A], Price(A, Oil2L, 70_000));
        var line = BasketComparer.Compare(snapshot, [new(Oil1L.Id, 1)], Now, Policy).Markets[0].Lines[0];

        line.Status.Should().Be(LineStatus.Missing);
        line.Note.Should().Contain("no acceptable substitute");
    }

    [Fact]
    public void A_wider_overbuy_cap_accepts_the_bigger_pack()
    {
        var snapshot = Snapshot([A], Price(A, Oil2L, 70_000));
        var line = BasketComparer.Compare(snapshot, [new(Oil1L.Id, 1)], Now, Policy, new ComparisonOptions(MaxOverbuyRatio: 1.0m)).Markets[0].Lines[0];

        line.Status.Should().Be(LineStatus.Substituted);
        line.Packs.Should().Be(1);
    }

    [Fact]
    public void Never_substitutes_across_dimensions()
    {
        var snapshot = Snapshot([A], Price(A, Rice1Kg, 100));
        BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1)], Now, Policy).Markets[0].Lines[0].Status.Should().Be(LineStatus.Missing);
    }

    [Fact]
    public void An_expired_price_is_treated_as_missing_and_triggers_substitution()
    {
        var snapshot = Snapshot([A], Price(A, MilkA1L, 3000, Now.AddDays(-8)), Price(A, MilkB1L, 4200));
        var line = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1)], Now, Policy).Markets[0].Lines[0];

        line.Status.Should().Be(LineStatus.Substituted);
        line.UsedProductId.Should().Be(MilkB1L.Id);
        line.Note.Should().Contain("price expired");
    }

    [Fact]
    public void A_stale_but_unexpired_price_is_still_used()
    {
        var snapshot = Snapshot([A], Price(A, MilkA1L, 3000, Now.AddDays(-3)), Price(A, MilkB1L, 4200));
        var line = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1)], Now, Policy).Markets[0].Lines[0];

        line.Status.Should().Be(LineStatus.Exact);
        line.ObservedAt.Should().Be(Now.AddDays(-3));
    }

    [Fact]
    public void Respects_a_line_that_forbids_substitutes()
    {
        var snapshot = Snapshot([A], Price(A, MilkB1L, 4200));
        var line = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1, AllowSubstitutes: false)], Now, Policy).Markets[0].Lines[0];

        line.Status.Should().Be(LineStatus.Missing);
        line.Note.Should().Be("requested item not listed");
    }

    [Fact]
    public void Ranks_complete_baskets_before_cheaper_incomplete_ones()
    {
        // B is cheaper for milk but does not sell soap at all: its total is not comparable.
        var snapshot = Snapshot([A, B], Price(A, MilkA1L, 4000), Price(A, Soap, 6000), Price(B, MilkA1L, 3000));
        var result = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1), new(Soap.Id, 1)], Now, Policy);

        result.Markets.Select(m => m.MarketCode).Should().Equal("a", "b");
        result.Markets[1].Complete.Should().BeFalse();
        result.Markets[1].MissingLines.Should().Be(1);
    }

    [Fact]
    public void Merges_duplicate_lines()
    {
        var snapshot = Snapshot([A], Price(A, MilkA1L, 4000));
        var quote = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1), new(MilkA1L.Id, 2)], Now, Policy).Markets[0];

        quote.Lines.Should().ContainSingle().Which.Quantity.Should().Be(3);
        quote.TotalKurus.Should().Be(12_000);
    }

    [Fact]
    public void Valid_until_is_the_first_expiry_among_used_prices()
    {
        var older = Now.AddDays(-2);
        var snapshot = Snapshot([A], Price(A, MilkA1L, 4000, Now.AddHours(-1)), Price(A, Soap, 6000, older));
        var result = BasketComparer.Compare(snapshot, [new(MilkA1L.Id, 1), new(Soap.Id, 1)], Now, Policy);

        result.ValidUntil.Should().Be(older + Policy.ExpireAfter);
    }

    [Fact]
    public void Unknown_products_are_reported()
    {
        var snapshot = Snapshot([A]);
        var unknown = Guid.NewGuid();
        var act = () => BasketComparer.Compare(snapshot, [new(unknown, 1)], Now, Policy);
        act.Should().Throw<UnknownProductException>().Which.ProductIds.Should().Equal(unknown);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Rejects_out_of_range_quantities(int quantity)
    {
        var snapshot = Snapshot([A], Price(A, MilkA1L, 4000));
        var act = () => BasketComparer.Compare(snapshot, [new(MilkA1L.Id, quantity)], Now, Policy);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_an_empty_basket() =>
        FluentActions.Invoking(() => BasketComparer.Compare(Snapshot([A]), [], Now, Policy)).Should().Throw<ArgumentException>();

    [Fact]
    public void Newest_evidence_wins_when_a_snapshot_has_duplicates()
    {
        var snapshot = Snapshot([A], Price(A, MilkA1L, 4000, Now.AddHours(-3)), Price(A, MilkA1L, 4100, Now.AddHours(-1)));
        snapshot.PriceOf("a", MilkA1L.Id)!.PriceKurus.Should().Be(4100);
    }

    internal static CatalogProduct P(string name, string size, string? group) =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("N")[..14], name, "Brand", UnitSize.Parse(size), group);

    internal static MarketPrice Price(Market market, CatalogProduct product, long kurus, DateTimeOffset? observedAt = null) =>
        new(market.Code, product.Id, kurus, observedAt ?? Now.AddMinutes(-10));

    private static PriceSnapshot Snapshot(Market[] markets, params MarketPrice[] prices) =>
        new(markets, [MilkA1L, MilkB1L, MilkA500, Oil1L, Oil2L, Soap, Rice1Kg], prices);
}

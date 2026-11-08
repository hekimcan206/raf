using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Raf.Core.Catalog;
using Raf.Core.Comparison;
using Raf.Core.Pricing;

namespace Raf.UnitTests.Comparison;

/// <summary>Invariants of the comparer over random catalogues, availability and baskets.</summary>
public sealed class BasketComparerPropertyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Sizes = ["250 g", "500 g", "1 kg", "2 kg", "500 ml", "1 L", "1,5 L", "2 L", "6 adet", "10 adet", "15 adet"];
    private static readonly string[] Groups = ["g1", "g2", "g3"];

    public sealed record Scenario(PriceSnapshot Snapshot, BasketLine[] Basket);

    public static Arbitrary<Scenario> Scenarios() =>
        (from productCount in Gen.Choose(2, 12)
         from sizes in Gen.Elements(Sizes).ArrayOf(productCount)
         from groups in Gen.Elements<string?>([.. Groups, null]).ArrayOf(productCount)
         from prices in Gen.Choose(100, 50_000).ArrayOf(productCount * 3)
         from listed in Gen.Elements(true, true, false).ArrayOf(productCount * 3)
         from ages in Gen.Choose(0, 10 * 24 * 60).ArrayOf(productCount * 3)
         from lineCount in Gen.Choose(1, Math.Min(6, productCount))
         from quantities in Gen.Choose(1, 5).ArrayOf(lineCount)
         from noSub in Gen.Elements(false, false, true).ArrayOf(lineCount)
         select Build(sizes, groups, prices, listed, ages, quantities, noSub)).ToArbitrary();

    private static Scenario Build(string[] sizes, string?[] groups, int[] prices, bool[] listed, int[] ages, int[] quantities, bool[] noSub)
    {
        var products = sizes.Select((s, i) => new CatalogProduct(Guid.NewGuid(), i.ToString("D14", System.Globalization.CultureInfo.InvariantCulture), $"p{i}", "b",
            UnitSize.Parse(s), groups[i])).ToList();
        var markets = new[] { new Market("m1", "M1"), new Market("m2", "M2"), new Market("m3", "M3") };
        var marketPrices = new List<MarketPrice>();
        for (var m = 0; m < markets.Length; m++)
        {
            for (var p = 0; p < products.Count; p++)
            {
                var k = (m * products.Count) + p;
                if (listed[k])
                {
                    marketPrices.Add(new MarketPrice(markets[m].Code, products[p].Id, prices[k], Now.AddMinutes(-ages[k])));
                }
            }
        }

        var basket = quantities.Select((q, i) => new BasketLine(products[i].Id, q, !noSub[i])).ToArray();
        return new Scenario(new PriceSnapshot(markets, products, marketPrices), basket);
    }

    [Property(Arbitrary = [typeof(BasketComparerPropertyTests)], MaxTest = 300)]
    public bool Market_total_is_the_sum_of_its_lines(Scenario s) =>
        BasketComparer.Compare(s.Snapshot, s.Basket, Now, FreshnessPolicy.Default).Markets
            .All(m => m.TotalKurus == m.Lines.Sum(l => l.LineTotalKurus) && m.Lines.All(l => l.LineTotalKurus == l.Packs * l.PackPriceKurus));

    [Property(Arbitrary = [typeof(BasketComparerPropertyTests)], MaxTest = 300)]
    public bool Complete_markets_come_first_and_cheapest_first(Scenario s)
    {
        var markets = BasketComparer.Compare(s.Snapshot, s.Basket, Now, FreshnessPolicy.Default).Markets;
        var complete = markets.TakeWhile(m => m.Complete).ToList();
        return markets.Skip(complete.Count).All(m => !m.Complete)
               && complete.Zip(complete.Skip(1)).All(p => p.First.TotalKurus <= p.Second.TotalKurus);
    }

    [Property(Arbitrary = [typeof(BasketComparerPropertyTests)], MaxTest = 300)]
    public bool Substitutes_cover_the_requested_amount_within_the_cap_and_same_dimension(Scenario s)
    {
        var options = ComparisonOptions.Default;
        foreach (var line in BasketComparer.Compare(s.Snapshot, s.Basket, Now, FreshnessPolicy.Default, options).Markets.SelectMany(m => m.Lines))
        {
            if (line.Status != LineStatus.Substituted)
            {
                continue;
            }

            var requested = s.Snapshot.Products[line.RequestedProductId];
            var used = s.Snapshot.Products[line.UsedProductId!.Value];
            var needed = requested.Size.BaseAmount * line.Quantity;
            var bought = used.Size.BaseAmount * line.Packs;
            if (used.Size.Dimension != requested.Size.Dimension || used.SubstitutionGroup != requested.SubstitutionGroup
                || bought < needed || bought - needed > needed * options.MaxOverbuyRatio)
            {
                return false;
            }
        }

        return true;
    }

    [Property(Arbitrary = [typeof(BasketComparerPropertyTests)], MaxTest = 300)]
    public bool No_used_price_is_expired_and_result_is_valid_until_the_first_expiry(Scenario s)
    {
        var policy = FreshnessPolicy.Default;
        var result = BasketComparer.Compare(s.Snapshot, s.Basket, Now, policy);
        var used = result.Markets.SelectMany(m => m.Lines).Where(l => l.ObservedAt is not null).Select(l => l.ObservedAt!.Value).ToList();
        return used.All(o => policy.IsUsable(o, Now))
               && (used.Count == 0 ? result.ValidUntil == DateTimeOffset.MaxValue : result.ValidUntil == used.Min() + policy.ExpireAfter);
    }

    [Property(Arbitrary = [typeof(BasketComparerPropertyTests)], MaxTest = 200)]
    public bool Line_order_does_not_change_the_result(Scenario s)
    {
        var a = BasketComparer.Compare(s.Snapshot, s.Basket, Now, FreshnessPolicy.Default);
        var b = BasketComparer.Compare(s.Snapshot, [.. s.Basket.Reverse()], Now, FreshnessPolicy.Default);
        return a.Markets.Select(m => (m.MarketCode, m.TotalKurus, m.MissingLines))
            .SequenceEqual(b.Markets.Select(m => (m.MarketCode, m.TotalKurus, m.MissingLines)));
    }
}

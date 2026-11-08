using Microsoft.Extensions.Time.Testing;
using Raf.Core.Catalog;
using Raf.Core.Feeds;
using Raf.Simulation;

namespace Raf.UnitTests.Simulation;

public sealed class SimulationTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 7, 11, 23, 0, TimeSpan.Zero);

    [Fact]
    public void Catalogue_uses_valid_restricted_circulation_GTINs_and_parseable_sizes()
    {
        SyntheticCatalog.Products.Should().OnlyContain(p => Gtin.IsValid(p.Gtin14) && p.Ean13.StartsWith("20", StringComparison.Ordinal));
        SyntheticCatalog.Products.Select(p => p.Gtin14).Should().OnlyHaveUniqueItems();
        SyntheticCatalog.Products.Select(p => p.TazelikSku).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Price_model_is_deterministic_across_instances()
    {
        var a = new PriceModel(new MarketControl(new FakeTimeProvider(T), false)).PriceList(MarketProfiles.Carsi, T);
        var b = new PriceModel(new MarketControl(new FakeTimeProvider(T), false)).PriceList(MarketProfiles.Carsi, T);
        a.Should().Equal(b);
    }

    [Fact]
    public void Unlisted_items_are_absent_from_the_feed()
    {
        var list = new PriceModel(new MarketControl(new FakeTimeProvider(T), false)).PriceList(MarketProfiles.Bereket, T);
        list.Select(p => p.Product.Index).Should().NotContain(MarketProfiles.Bereket.Unlisted);
        list.Should().HaveCount(SyntheticCatalog.Products.Count - MarketProfiles.Bereket.Unlisted.Count);
    }

    [Theory]
    [InlineData("bereket", "2026-11-07T11:20:00Z")] // every 10 minutes
    [InlineData("carsi", "2026-11-07T11:15:00Z")]   // every 15 minutes
    [InlineData("kuzey", "2026-11-07T00:00:00Z")]   // nightly at 03:00 Turkey time
    public void Republishing_markets_stamp_the_publication_time(string code, string expected)
    {
        var price = new PriceModel(new MarketControl(new FakeTimeProvider(T), false)).PriceList(MarketProfiles.Get(code), T)[0];
        price.ObservedAt.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Dormant_Tazelik_items_keep_an_old_timestamp()
    {
        var model = new PriceModel(new MarketControl(new FakeTimeProvider(T), false));
        var stale = model.PriceOf(MarketProfiles.Tazelik, SyntheticCatalog.ByIndex(24), T);
        stale.ObservedAt.Should().Be(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero)); // 4 days dormant
    }

    [Fact]
    public void Tazelik_reports_when_a_price_last_changed_not_when_it_was_published()
    {
        var model = new PriceModel(new MarketControl(new FakeTimeProvider(T), false));
        var price = model.PriceOf(MarketProfiles.Tazelik, SyntheticCatalog.ByIndex(1), T);
        price.ObservedAt.Should().BeOnOrBefore(PriceModel.PublicationTime(MarketProfiles.Tazelik, T));
        // The level in force did not change between that moment and now.
        model.PriceOf(MarketProfiles.Tazelik, SyntheticCatalog.ByIndex(1), price.ObservedAt).PriceKurus.Should().Be(price.PriceKurus);
    }

    [Fact]
    public void Prices_look_like_shelf_prices()
    {
        var list = new PriceModel(new MarketControl(new FakeTimeProvider(T), false)).PriceList(MarketProfiles.Kuzey, T);
        list.Should().OnlyContain(p => p.PriceKurus % 25 == 20); // x.20, x.45, x.70, x.95
    }

    [Fact]
    public void Manual_price_overrides_win_and_are_stamped()
    {
        var clock = new FakeTimeProvider(T);
        var control = new MarketControl(clock, false);
        var product = SyntheticCatalog.ByIndex(1);
        control.SetPrice("tazelik", product.Gtin14, 1234);

        var price = new PriceModel(control).PriceOf(MarketProfiles.Tazelik, product, T.AddMinutes(1));
        price.PriceKurus.Should().Be(1234);
        price.ObservedAt.Should().Be(T);

        control.ClearPrices("tazelik");
        new PriceModel(control).PriceOf(MarketProfiles.Tazelik, product, T).PriceKurus.Should().NotBe(1234);
    }

    [Fact]
    public void Forced_outages_expire()
    {
        var clock = new FakeTimeProvider(T);
        var control = new MarketControl(clock, chaosEnabled: false);
        control.StartOutage("carsi", TimeSpan.FromMinutes(2));

        control.NextRequest(MarketProfiles.Carsi).Behaviour.Should().Be(FeedBehaviour.Outage);
        clock.Advance(TimeSpan.FromMinutes(3));
        control.NextRequest(MarketProfiles.Carsi).Behaviour.Should().Be(FeedBehaviour.Normal);
    }

    [Fact]
    public void Kuzey_maintenance_window_applies_only_with_chaos()
    {
        var at = new DateTimeOffset(2026, 11, 7, 11, 42, 0, TimeSpan.Zero);
        new MarketControl(new FakeTimeProvider(at), chaosEnabled: true).NextRequest(MarketProfiles.Kuzey).Behaviour.Should().Be(FeedBehaviour.Outage);
        new MarketControl(new FakeTimeProvider(at), chaosEnabled: false).NextRequest(MarketProfiles.Kuzey).Behaviour.Should().Be(FeedBehaviour.Normal);
    }

    [Fact]
    public void Chaos_produces_failures_near_the_configured_rate()
    {
        var control = new MarketControl(new FakeTimeProvider(T), chaosEnabled: true, seed: 7);
        var outcomes = Enumerable.Range(0, 5000).Select(_ => control.NextRequest(MarketProfiles.Carsi).Behaviour).ToList();
        var failureRate = outcomes.Count(o => o == FeedBehaviour.RandomFailure) / 5000.0;
        failureRate.Should().BeInRange(0.08, 0.12);
    }

    [Theory]
    [InlineData(0, true, 3, FeedStatus.Healthy, 0)]
    [InlineData(2, true, 3, FeedStatus.Healthy, 0)]
    [InlineData(0, false, 3, FeedStatus.Degraded, 1)]
    [InlineData(1, false, 3, FeedStatus.Degraded, 2)]
    [InlineData(2, false, 3, FeedStatus.Down, 3)]
    [InlineData(7, false, 3, FeedStatus.Down, 8)]
    public void Feed_health_transitions(int previousFailures, bool succeeded, int downAfter, FeedStatus status, int failures) =>
        FeedHealth.AfterRun(previousFailures, succeeded, downAfter).Should().Be((status, failures));

    [Fact]
    public void A_feed_without_attempts_for_too_long_is_silent()
    {
        FeedHealth.IsSilent(null, T, TimeSpan.FromMinutes(15)).Should().BeTrue();
        FeedHealth.IsSilent(T.AddMinutes(-16), T, TimeSpan.FromMinutes(15)).Should().BeTrue();
        FeedHealth.IsSilent(T.AddMinutes(-5), T, TimeSpan.FromMinutes(15)).Should().BeFalse();
    }
}

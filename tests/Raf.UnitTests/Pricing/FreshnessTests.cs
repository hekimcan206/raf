using Raf.Core.Comparison;
using Raf.Core.Pricing;

namespace Raf.UnitTests.Pricing;

public sealed class FreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly FreshnessPolicy Policy = FreshnessPolicy.Default;

    [Theory]
    [InlineData(0, Freshness.Fresh)]
    [InlineData(-3, Freshness.Fresh)]            // feed clock slightly ahead
    [InlineData(6 * 60, Freshness.Fresh)]        // boundary is inclusive
    [InlineData((6 * 60) + 1, Freshness.Aging)]
    [InlineData(24 * 60, Freshness.Aging)]
    [InlineData((24 * 60) + 1, Freshness.Stale)]
    [InlineData(7 * 24 * 60, Freshness.Stale)]
    [InlineData((7 * 24 * 60) + 1, Freshness.Expired)]
    public void Classifies_by_age_of_the_evidence(int ageMinutes, Freshness expected) =>
        Policy.Classify(Now.AddMinutes(-ageMinutes), Now).Should().Be(expected);

    [Fact]
    public void Expired_prices_are_not_usable() => Policy.IsUsable(Now.AddDays(-8), Now).Should().BeFalse();

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 3)]
    [InlineData(1, 2, 2)]
    public void Thresholds_must_increase(int fresh, int aging, int expire)
    {
        var policy = new FreshnessPolicy(TimeSpan.FromHours(fresh), TimeSpan.FromHours(aging), TimeSpan.FromHours(expire));
        policy.Invoking(p => p.Validate()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Stale_share_is_weighted_by_money_not_line_count()
    {
        // Two cheap fresh lines and one expensive stale line: 1/3 of lines, but 80 % of the money is stale.
        var quote = Quote(
            Line(1_000, Now.AddMinutes(-5)),
            Line(1_000, Now.AddMinutes(-5)),
            Line(8_000, Now.AddDays(-2)));

        var summary = FreshnessSummary.For(quote, Policy, Now);

        summary.StaleKurus.Should().Be(8_000);
        summary.FreshKurus.Should().Be(2_000);
        summary.StaleShare.Should().Be(0.8m);
        summary.OldestObservedAt.Should().Be(Now.AddDays(-2));
        summary.LineFreshness.Should().Equal(Freshness.Fresh, Freshness.Fresh, Freshness.Stale);
    }

    [Fact]
    public void The_same_cached_result_ages_as_time_passes()
    {
        var quote = Quote(Line(5_000, Now.AddHours(-5)));
        FreshnessSummary.For(quote, Policy, Now).LineFreshness[0].Should().Be(Freshness.Fresh);
        FreshnessSummary.For(quote, Policy, Now.AddHours(2)).LineFreshness[0].Should().Be(Freshness.Aging);
        FreshnessSummary.For(quote, Policy, Now.AddDays(2)).LineFreshness[0].Should().Be(Freshness.Stale);
    }

    [Fact]
    public void Missing_lines_have_no_freshness_and_no_weight()
    {
        var missing = new QuoteLine(Guid.NewGuid(), "x", 1, LineStatus.Missing, null, null, null, 0, 0, 0, null, null, null, "n/a");
        var summary = FreshnessSummary.For(Quote(missing), Policy, Now);
        summary.StaleShare.Should().Be(0);
        summary.LineFreshness.Should().Equal((Freshness?)null);
    }

    private static QuoteLine Line(long total, DateTimeOffset observedAt) =>
        new(Guid.NewGuid(), "p", 1, LineStatus.Exact, Guid.NewGuid(), "p", "1 kg", 1, total, total, total, "kg", observedAt, null);

    private static MarketQuote Quote(params QuoteLine[] lines) =>
        new("m", "M", lines.Sum(l => l.LineTotalKurus), true, 0, 0, lines);
}

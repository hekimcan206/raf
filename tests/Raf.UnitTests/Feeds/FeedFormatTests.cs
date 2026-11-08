using System.Text;
using System.Xml;
using Microsoft.Extensions.Time.Testing;
using Raf.Core.Feeds;
using Raf.Simulation;

namespace Raf.UnitTests.Feeds;

/// <summary>Golden samples per format (hand-written edge cases) and writer → parser round trips over the simulator.</summary>
public sealed class FeedFormatTests
{
    private static FileStream Sample(string name) => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Feeds", "Samples", name));

    [Fact]
    public void Bereket_json_parses_lira_decimals_and_offsets_and_reports_bad_items()
    {
        using var stream = Sample("bereket.json");
        var page = new BereketJsonFormat().Parse(stream);

        page.Items.Should().HaveCount(3);
        page.Items[0].Should().Be(new FeedItem("02000000000015", null, "Ovacık Tam Yağlı Süt 1 L", "1 L", 3995,
            new DateTimeOffset(2026, 11, 7, 14, 0, 0, TimeSpan.FromHours(3))));
        page.Items[1].Gtin14.Should().Be("02000000000039"); // GTIN-14 in the feed, same canonical key
        page.Items[2].PriceKurus.Should().Be(8500);          // 84.999 TL rounds half away from zero to 85.00
        page.Items[2].ObservedAt.Should().Be(new DateTimeOffset(2026, 11, 7, 11, 0, 0, TimeSpan.Zero));
        page.Rejected.Should().HaveCount(2).And.Contain(r => r.Contains("invalid barcode", StringComparison.Ordinal));
    }

    [Fact]
    public void Carsi_csv_parses_decimal_comma_thousands_dot_and_local_time()
    {
        using var stream = Sample("carsi.csv");
        var page = new CarsiCsvFormat().Parse(stream);

        page.Items.Should().HaveCount(2);
        page.Items[0].PriceKurus.Should().Be(3720);
        page.Items[0].ObservedAt.Should().Be(new DateTimeOffset(2026, 11, 7, 14, 15, 0, TimeSpan.FromHours(3)));
        page.Items[1].PriceKurus.Should().Be(124_990); // "1.249,90"
        page.Items[1].SizeText.Should().Be("5 lt");
        page.Rejected.Should().HaveCount(3);
        page.Rejected.Should().Contain(r => r.Contains("expected 5 fields", StringComparison.Ordinal))
            .And.Contain(r => r.Contains("invalid price", StringComparison.Ordinal))
            .And.Contain(r => r.Contains("invalid timestamp", StringComparison.Ordinal));
    }

    [Fact]
    public void Carsi_csv_with_an_unexpected_header_is_rejected_as_a_whole()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("BARCODE,PRICE\n1,2\n"));
        new CarsiCsvFormat().Invoking(f => f.Parse(stream)).Should().Throw<FormatException>();
    }

    [Fact]
    public void Kuzey_xml_uses_the_list_timestamp_for_every_item()
    {
        using var stream = Sample("kuzey.xml");
        var page = new KuzeyXmlFormat().Parse(stream);

        page.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(i => i.ObservedAt == new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));
        page.Items[1].Gtin14.Should().Be("02000000000022"); // EAN-13 attribute normalised
        page.Items[0].PriceKurus.Should().Be(4195);
        page.Rejected.Should().HaveCount(2);
    }

    [Fact]
    public void Kuzey_xml_refuses_DTDs_so_external_entities_cannot_be_resolved()
    {
        using var stream = Sample("kuzey-xxe.xml");
        new KuzeyXmlFormat().Invoking(f => f.Parse(stream)).Should().Throw<XmlException>();
    }

    [Fact]
    public void Tazelik_page_parses_skus_unix_timestamps_and_page_count()
    {
        using var stream = Sample("tazelik-page1.json");
        var page = new TazelikPagedJsonFormat().Parse(stream);

        page.TotalPages.Should().Be(3);
        page.Items.Should().HaveCount(2);
        page.Items[0].Should().Be(new FeedItem(null, "TZ-10007", "Ovacık Tam Yağlı Süt 1 L", "1 lt", 3895, DateTimeOffset.FromUnixTimeSeconds(1794052800)));
        page.Rejected.Should().HaveCount(3);
    }

    public static TheoryData<string> Markets => [.. MarketProfiles.All.Select(m => m.Code)];

    [Theory]
    [MemberData(nameof(Markets))]
    public void Simulator_output_round_trips_through_the_parser(string code)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 11, 23, 0, TimeSpan.Zero));
        var market = MarketProfiles.Get(code);
        var model = new PriceModel(new MarketControl(clock, chaosEnabled: false));
        var prices = model.PriceList(market, clock.GetUtcNow());
        var format = FeedFormats.For(code);

        var parsed = new List<FeedItem>();
        if (format.IsPaged)
        {
            var pages = 1;
            for (var p = 1; p <= pages; p++)
            {
                var page = format.Parse(new MemoryStream(Encoding.UTF8.GetBytes(FeedWriters.TazelikPage(prices, p))));
                pages = page.TotalPages;
                page.Rejected.Should().BeEmpty();
                parsed.AddRange(page.Items);
            }
        }
        else
        {
            var body = code switch
            {
                "bereket" => FeedWriters.Bereket(prices, clock.GetUtcNow()),
                "carsi" => FeedWriters.Carsi(prices),
                _ => FeedWriters.Kuzey(prices, PriceModel.PublicationTime(market, clock.GetUtcNow())),
            };
            var page = format.Parse(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            page.Rejected.Should().BeEmpty();
            parsed.AddRange(page.Items);
        }

        parsed.Should().HaveCount(prices.Count);
        foreach (var (item, price) in parsed.Zip(prices))
        {
            item.PriceKurus.Should().Be(price.PriceKurus);
            item.ObservedAt.Should().Be(price.ObservedAt);
            (item.Gtin14 ?? item.MarketSku).Should().Be(code == "tazelik" ? price.Product.TazelikSku : price.Product.Gtin14);
            Raf.Core.Catalog.UnitSize.Parse(item.SizeText!).Should().Be(price.Product.Size);
        }
    }

    [Fact]
    public void Item_rules_reject_implausible_items()
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        FeedItem Item(long price, DateTimeOffset at, string? gtin = "02000000000015") => new(gtin, null, null, null, price, at);

        FeedItemRules.Validate(Item(100, now), now).Should().BeNull();
        FeedItemRules.Validate(Item(0, now), now).Should().Be("price must be positive");
        FeedItemRules.Validate(Item(FeedItemRules.MaxPriceKurus + 1, now), now).Should().Be("price implausibly high");
        FeedItemRules.Validate(Item(100, now.AddMinutes(10)), now).Should().Be("observed_at is in the future");
        FeedItemRules.Validate(Item(100, now.AddMinutes(4)), now).Should().BeNull(); // tolerated clock skew
        FeedItemRules.Validate(Item(100, now, gtin: null), now).Should().Be("no product identifier");
    }
}

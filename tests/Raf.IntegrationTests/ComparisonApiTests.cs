using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Raf.Api.Baskets;
using Raf.Api.Comparison;
using Raf.IntegrationTests.Infrastructure;

namespace Raf.IntegrationTests;

public sealed class ComparisonApiTests(StackFixture stack) : IAsyncLifetime
{
    private ApiFactory _api = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _api = stack.CreateApi();
        _client = _api.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        stack.SimulatorControl.ClearPrices("carsi");
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Demo_basket_shows_substitutions_gaps_and_staleness()
    {
        var result = await CompareDemoAsync();

        var bereket = result.Markets.Single(m => m.Code == "bereket");
        bereket.Complete.Should().BeTrue();
        var milk = bereket.Lines.Single(l => l.RequestedProductId == Demo.Id(2));
        milk.Status.Should().Be("substituted");          // Bereket does not list Dağ Süt milk
        milk.UsedProductId.Should().Be(Demo.Id(1));

        var carsi = result.Markets.Single(m => m.Code == "carsi");
        carsi.Complete.Should().BeFalse();                // 1 L olive oil missing; 2 L would be 100 % extra
        carsi.Lines.Single(l => l.RequestedProductId == Demo.Id(22)).Status.Should().Be("missing");
        var eggs = carsi.Lines.Single(l => l.RequestedProductId == Demo.Id(9));
        eggs.Status.Should().Be("substituted");
        eggs.UsedProductId.Should().Be(Demo.Id(10));      // 2 × 10 eggs cover 15 within the cap
        eggs.Packs.Should().Be(2);
        result.Markets[^1].Code.Should().Be("carsi");  // incomplete totals rank last

        var tazelik = result.Markets.Single(m => m.Code == "tazelik");
        tazelik.Freshness.StaleShare.Should().BeGreaterThan(0.2m); // several prices untouched for days
        tazelik.Lines.Should().Contain(l => l.Freshness == "stale");

        result.CheapestCompleteMarket.Should().Be(result.Markets[0].Code);
        result.Markets.Where(m => m.Complete).Select(m => m.TotalKurus).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Second_identical_request_is_served_from_Redis()
    {
        var items = new[] { new BasketItemDto(Demo.Id(17), 2), new BasketItemDto(Demo.Id(33), 1) };
        var first = await PostCompareAsync(items);
        var second = await PostCompareAsync([.. items.Reverse()]);

        first.Cache.Should().Be("miss");
        second.Cache.Should().Be("hit");
        second.Markets.Select(m => m.TotalKurus).Should().Equal(first.Markets.Select(m => m.TotalKurus));
    }

    [Fact]
    public async Task A_price_change_invalidates_exactly_the_comparisons_that_depend_on_it()
    {
        var affected = new[] { new BasketItemDto(Demo.Id(25), 1) };    // sugar 3 kg
        var unaffected = new[] { new BasketItemDto(Demo.Id(32), 1) };  // Turkish coffee, other group
        await _api.Ingestion.RunAsync("carsi", TestContext.Current.CancellationToken); // start from current prices
        await PostCompareAsync(affected);
        await PostCompareAsync(unaffected);

        // Change the 1 kg sugar: same substitution group as the 3 kg, so the sugar comparison depends on it.
        var price = Demo.UniquePrice();
        stack.SimulatorControl.SetPrice("carsi", Demo.Gtin(24), price);
        (await _api.Ingestion.RunAsync("carsi", TestContext.Current.CancellationToken)).Changed.Should().BeGreaterThanOrEqualTo(1);

        (await PostCompareAsync(affected)).Cache.Should().Be("miss");
        (await PostCompareAsync(unaffected)).Cache.Should().Be("hit");

        var exact = await PostCompareAsync([new BasketItemDto(Demo.Id(24), 1)]);
        exact.Markets.Single(m => m.Code == "carsi").TotalKurus.Should().Be(price);
    }

    [Fact]
    public async Task Cache_write_is_refused_when_prices_changed_during_the_computation()
    {
        var cache = _api.Services.GetService(typeof(ComparisonCache)) as ComparisonCache;
        var key = ComparisonCache.KeyFor([new(Demo.Id(8), 1)]);
        var epoch = await cache!.CurrentEpochAsync();

        // A price change lands between "read epoch + load prices" and "write result".
        await cache.InvalidateProductsAsync([Demo.Id(30)]);

        var stored = await cache.TrySetAsync(key, new Raf.Core.Comparison.BasketComparison([], DateTimeOffset.UtcNow, DateTimeOffset.MaxValue),
            [Demo.Id(8)], epoch!, TimeSpan.FromMinutes(1));
        stored.Should().BeFalse();
        (await cache.GetAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task Without_Redis_the_API_still_answers_uncached()
    {
        await using var noRedis = stack.CreateApi(b => b.UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,connectTimeout=200"));
        var response = await noRedis.CreateClient().PostAsJsonAsync("/api/v1/comparisons",
            new { items = new[] { new BasketItemDto(Demo.Id(1), 1) } }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Cache").Should().Equal("bypass");
    }

    [Fact]
    public async Task Baskets_are_saved_under_client_ids_with_optimistic_concurrency()
    {
        var id = Guid.NewGuid();
        var created = await _client.PutAsJsonAsync($"/api/v1/baskets/{id}",
            new { name = "Offline basket", items = new[] { new BasketItemDto(Demo.Id(1), 2), new BasketItemDto(Demo.Id(1), 1) } },
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        created.Headers.ETag!.Tag.Should().Be("\"1\"");
        var basket = await created.Content.ReadFromJsonAsync<BasketDto>(TestContext.Current.CancellationToken);
        basket!.Items.Should().ContainSingle().Which.Quantity.Should().Be(3);

        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/baskets/{id}")
        {
            Content = JsonContent.Create(new { name = "x", items = new[] { new BasketItemDto(Demo.Id(2), 1) } }),
        };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"7\""));
        (await _client.SendAsync(stale, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);

        var comparison = await _client.GetAsync($"/api/v1/baskets/{id}/comparison", TestContext.Current.CancellationToken);
        comparison.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Invalid_quantities_are_a_validation_problem(int quantity)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/comparisons",
            new { items = new[] { new BasketItemDto(Demo.Id(1), quantity) } }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_products_are_a_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/comparisons",
            new { items = new[] { new BasketItemDto(Guid.NewGuid(), 1) } }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Catalogue_search_and_price_history_work()
    {
        var products = await _client.GetFromJsonAsync<List<Raf.Api.Catalog.ProductDto>>("/api/v1/products?q=süt", TestContext.Current.CancellationToken);
        products!.Should().NotBeEmpty().And.OnlyContain(p => p.Name.Contains("Süt", StringComparison.Ordinal));

        var history = await _client.GetFromJsonAsync<List<Raf.Api.Catalog.PricePointDto>>($"/api/v1/products/{Demo.Id(1)}/prices?days=14",
            TestContext.Current.CancellationToken);
        history!.Select(h => h.MarketCode).Distinct().Should().HaveCount(4);
        history!.Count.Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task Readiness_depends_on_the_database_only()
    {
        var response = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("database").And.NotContain("redis");
    }

    private async Task<ComparisonResponse> CompareDemoAsync() =>
        (await _client.GetFromJsonAsync<ComparisonResponse>($"/api/v1/baskets/{Demo.BasketId}/comparison", TestContext.Current.CancellationToken))!;

    private async Task<ComparisonResponse> PostCompareAsync(BasketItemDto[] items)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/comparisons", new { items }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ComparisonResponse>(TestContext.Current.CancellationToken))!;
    }
}

using System.Net.Http.Json;
using Dapper;
using Raf.Api.Ingestion;
using Raf.IntegrationTests.Infrastructure;

namespace Raf.IntegrationTests;

public sealed class IngestionTests(StackFixture stack) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    public ValueTask InitializeAsync()
    {
        _api = stack.CreateApi();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var market in new[] { "bereket", "carsi", "kuzey", "tazelik" })
        {
            stack.SimulatorControl.EndOutage(market);
            stack.SimulatorControl.ClearPrices(market);
        }

        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Rereading_an_unchanged_feed_appends_nothing()
    {
        await _api.Ingestion.RunAsync("carsi", TestContext.Current.CancellationToken);
        var second = await _api.Ingestion.RunAsync("carsi", TestContext.Current.CancellationToken);

        second.Status.Should().Be("succeeded");
        second.Matched.Should().Be(33);
        second.Inserted.Should().Be(0);
        second.Changed.Should().Be(0);
    }

    [Fact]
    public async Task A_price_change_appends_history_and_moves_the_current_price()
    {
        var price = Demo.UniquePrice();
        stack.SimulatorControl.SetPrice("kuzey", Demo.Gtin(5), price);

        var run = await _api.Ingestion.RunAsync("kuzey", TestContext.Current.CancellationToken);

        run.Status.Should().Be("succeeded", run.Error);
        run.Changed.Should().BeGreaterThanOrEqualTo(1);
        await using var ds = stack.AppDataSource();
        await using var connection = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>(
                "SELECT price_kurus FROM raf.current_price WHERE market_code = 'kuzey' AND product_id = @id", new { id = Demo.Id(5) }))
            .Should().Be(price);
        (await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM raf.price_observation WHERE market_code = 'kuzey' AND product_id = @id AND price_kurus = @price AND source = 'feed:kuzey'",
                new { id = Demo.Id(5), price }))
            .Should().Be(1);
    }

    [Fact]
    public async Task Older_evidence_is_kept_as_history_but_never_rolls_the_current_price_back()
    {
        await using var ds = stack.MigratorDataSource();
        await using var connection = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var current = await connection.QuerySingleAsync<(long Price, DateTime ObservedAt)>(
            "SELECT price_kurus, observed_at FROM raf.current_price WHERE market_code = 'bereket' AND product_id = @id", new { id = Demo.Id(7) });

        await using var tx = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var late = new MatchedPrice(Demo.Id(7), 99_999, new DateTimeOffset(current.ObservedAt, TimeSpan.Zero).AddHours(-1).AddSeconds(7));
        var (inserted, changes) = await PriceWriter.ApplyAsync(connection, tx, "bereket", [late], "late-replay", null);
        await tx.CommitAsync(TestContext.Current.CancellationToken);

        inserted.Should().Be(1);
        changes.Should().BeEmpty();
        (await connection.ExecuteScalarAsync<long>(
                "SELECT price_kurus FROM raf.current_price WHERE market_code = 'bereket' AND product_id = @id", new { id = Demo.Id(7) }))
            .Should().Be(current.Price);
    }

    [Fact]
    public async Task An_unavailable_feed_degrades_then_goes_down_then_recovers()
    {
        await _api.Ingestion.RunAsync("tazelik", TestContext.Current.CancellationToken);
        stack.SimulatorControl.StartOutage("tazelik", TimeSpan.FromMinutes(5));

        var statuses = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var failed = await _api.Ingestion.RunAsync("tazelik", TestContext.Current.CancellationToken);
            failed.Status.Should().Be("failed");
            failed.HttpStatus.Should().Be(503);
            statuses.Add(await StatusAsync("tazelik"));
        }

        statuses.Should().Equal("degraded", "degraded", "down");

        stack.SimulatorControl.EndOutage("tazelik");
        (await _api.Ingestion.RunAsync("tazelik", TestContext.Current.CancellationToken)).Status.Should().Be("succeeded");
        (await StatusAsync("tazelik")).Should().Be("healthy");

        var runs = await _api.CreateClient().GetFromJsonAsync<List<FeedRunDto>>("/api/v1/feeds/runs?market=tazelik&limit=4", TestContext.Current.CancellationToken);
        runs!.Select(r => r.Status).Should().Equal("succeeded", "failed", "failed", "failed");
    }

    [Fact]
    public async Task Size_disagreement_between_feed_and_catalogue_rejects_the_item()
    {
        // Simulate a mapping error: Tazelik's 1 L milk SKU pointed at the 500 ml product.
        await using var ds = stack.MigratorDataSource();
        await using var connection = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("UPDATE raf.market_sku SET product_id = @wrong WHERE market_code = 'tazelik' AND sku = @sku",
            new { wrong = Demo.Id(3), sku = Demo.Sku(1) });
        try
        {
            var run = await _api.Ingestion.RunAsync("tazelik", TestContext.Current.CancellationToken);
            run.Rejected.Should().Be(1);
            var runs = await _api.CreateClient().GetFromJsonAsync<List<FeedRunDto>>("/api/v1/feeds/runs?market=tazelik&limit=1", TestContext.Current.CancellationToken);
            runs![0].Error.Should().Contain("feed size 1 L differs from catalogue 500 ml");
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE raf.market_sku SET product_id = @right WHERE market_code = 'tazelik' AND sku = @sku",
                new { right = Demo.Id(1), sku = Demo.Sku(1) });
        }
    }

    [Fact]
    public async Task Unmapped_SKUs_are_counted_not_guessed()
    {
        await using var ds = stack.MigratorDataSource();
        await using var connection = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("DELETE FROM raf.market_sku WHERE market_code = 'tazelik' AND sku = @sku", new { sku = Demo.Sku(10) });
        try
        {
            var run = await _api.Ingestion.RunAsync("tazelik", TestContext.Current.CancellationToken);
            run.Unmatched.Should().Be(1);
            run.Matched.Should().Be(32);
        }
        finally
        {
            await connection.ExecuteAsync("INSERT INTO raf.market_sku (market_code, sku, product_id) VALUES ('tazelik', @sku, @id)",
                new { sku = Demo.Sku(10), id = Demo.Id(10) });
        }
    }

    [Fact]
    public async Task A_run_is_skipped_while_another_instance_holds_the_market_lock()
    {
        await using var ds = stack.AppDataSource();
        await using var holder = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await holder.ExecuteAsync("SELECT pg_advisory_lock(hashtext('raf:ingest:bereket'))");

        var skipped = await _api.Ingestion.RunAsync("bereket", TestContext.Current.CancellationToken);
        var other = await _api.Ingestion.RunAsync("carsi", TestContext.Current.CancellationToken);

        skipped.Status.Should().Be("skipped");
        other.Status.Should().Be("succeeded"); // the lock is per market, not global
        await holder.ExecuteAsync("SELECT pg_advisory_unlock(hashtext('raf:ingest:bereket'))");
        (await _api.Ingestion.RunAsync("bereket", TestContext.Current.CancellationToken)).Status.Should().Be("succeeded");
    }

    private async Task<string> StatusAsync(string market)
    {
        var markets = await _api.CreateClient().GetFromJsonAsync<List<MarketHealthDto>>("/api/v1/markets", TestContext.Current.CancellationToken);
        return markets!.Single(m => m.Code == market).Status;
    }
}

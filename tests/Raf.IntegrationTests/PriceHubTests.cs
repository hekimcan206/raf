using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Raf.Api.Realtime;
using Raf.IntegrationTests.Infrastructure;

namespace Raf.IntegrationTests;

/// <summary>SignalR end to end: ingestion → commit → cache invalidation → push to the watchers of that product only.</summary>
public sealed class PriceHubTests(StackFixture stack) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private ApiFactory _api = null!;

    public ValueTask InitializeAsync()
    {
        _api = stack.CreateApi();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        stack.SimulatorControl.ClearPrices("bereket");
        stack.SimulatorControl.EndOutage("kuzey");
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Watchers_of_a_product_receive_its_price_changes_and_others_do_not()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var watcher = _api.CreateHubConnection();
        await using var bystander = _api.CreateHubConnection();
        var received = Channel.CreateUnbounded<PriceChangedEvent>();
        var bystanderReceived = Channel.CreateUnbounded<PriceChangedEvent>();
        watcher.On<PriceChangedEvent>(nameof(IPriceClient.PriceChanged), e => received.Writer.TryWrite(e));
        bystander.On<PriceChangedEvent>(nameof(IPriceClient.PriceChanged), e => bystanderReceived.Writer.TryWrite(e));
        await watcher.StartAsync(ct);
        await bystander.StartAsync(ct);
        (await watcher.InvokeAsync<int>(nameof(PriceHub.Watch), new[] { Demo.Id(14) }, ct)).Should().Be(1);
        await bystander.InvokeAsync<int>(nameof(PriceHub.Watch), new[] { Demo.Id(33) }, ct);

        var price = Demo.UniquePrice();
        stack.SimulatorControl.SetPrice("bereket", Demo.Gtin(14), price);
        await _api.Ingestion.RunAsync("bereket", ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Wait);
        var change = await received.Reader.ReadAsync(timeout.Token);
        change.ProductId.Should().Be(Demo.Id(14));
        change.MarketCode.Should().Be("bereket");
        change.NewPriceKurus.Should().Be(price);
        change.OldPriceKurus.Should().NotBe(price);
        bystanderReceived.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Group_membership_does_not_survive_a_new_connection_so_clients_must_rewatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var first = _api.CreateHubConnection())
        {
            await first.StartAsync(ct);
            await first.InvokeAsync<int>(nameof(PriceHub.Watch), new[] { Demo.Id(18) }, ct);
        }

        // Same client, reconnected: a fresh connection id with no groups until it calls Watch again.
        await using var reconnected = _api.CreateHubConnection();
        var received = Channel.CreateUnbounded<PriceChangedEvent>();
        reconnected.On<PriceChangedEvent>(nameof(IPriceClient.PriceChanged), e => received.Writer.TryWrite(e));
        await reconnected.StartAsync(ct);

        stack.SimulatorControl.SetPrice("bereket", Demo.Gtin(18), Demo.UniquePrice());
        await _api.Ingestion.RunAsync("bereket", ct);
        await Task.Delay(500, ct);
        received.Reader.TryRead(out _).Should().BeFalse();

        await reconnected.InvokeAsync<int>(nameof(PriceHub.Watch), new[] { Demo.Id(18) }, ct);
        stack.SimulatorControl.SetPrice("bereket", Demo.Gtin(18), Demo.UniquePrice());
        await _api.Ingestion.RunAsync("bereket", ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Wait);
        (await received.Reader.ReadAsync(timeout.Token)).ProductId.Should().Be(Demo.Id(18));
    }

    [Fact]
    public async Task Feed_health_changes_are_broadcast()
    {
        var ct = TestContext.Current.CancellationToken;
        await _api.Ingestion.RunAsync("kuzey", ct); // healthy baseline
        await using var client = _api.CreateHubConnection();
        var received = Channel.CreateUnbounded<FeedHealthChangedEvent>();
        client.On<FeedHealthChangedEvent>(nameof(IPriceClient.FeedHealthChanged), e => received.Writer.TryWrite(e));
        await client.StartAsync(ct);

        stack.SimulatorControl.StartOutage("kuzey", TimeSpan.FromMinutes(5));
        await _api.Ingestion.RunAsync("kuzey", ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Wait);
        var change = await received.Reader.ReadAsync(timeout.Token);
        change.Should().BeEquivalentTo(new { MarketCode = "kuzey", Status = "degraded" });
        stack.SimulatorControl.EndOutage("kuzey");
        await _api.Ingestion.RunAsync("kuzey", ct);
    }

    [Fact]
    public async Task Watching_too_many_products_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = _api.CreateHubConnection();
        await client.StartAsync(ct);
        var tooMany = Enumerable.Range(0, PriceHub.MaxWatched + 1).Select(_ => Guid.NewGuid()).ToArray();

        var act = () => client.InvokeAsync<int>(nameof(PriceHub.Watch), tooMany, ct);
        await act.Should().ThrowAsync<HubException>();
    }
}

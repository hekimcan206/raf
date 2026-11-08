using Microsoft.AspNetCore.SignalR;
using Raf.Api.Comparison;
using Raf.Api.Ingestion;

namespace Raf.Api.Realtime;

/// <summary>A current price changed value in one market.</summary>
public sealed record PriceChangedEvent(Guid ProductId, string MarketCode, long? OldPriceKurus, long NewPriceKurus, DateTimeOffset ObservedAt);

/// <summary>A market feed moved between healthy / degraded / down / unknown.</summary>
public sealed record FeedHealthChangedEvent(string MarketCode, string Status, DateTimeOffset? LastSuccessAt);

public interface IPriceClient
{
    Task PriceChanged(PriceChangedEvent change);

    Task FeedHealthChanged(FeedHealthChangedEvent change);
}

/// <summary>
/// Pushes price changes for the products in a client's basket. Group membership is per connection and is lost on
/// reconnect (the server sees a new connection), so clients call <see cref="Watch"/> again after every (re)connect;
/// they also refetch the comparison then, because changes made while disconnected are not replayed.
/// </summary>
public sealed class PriceHub : Hub<IPriceClient>
{
    public const string Path = "/hubs/prices";
    public const int MaxWatched = 200;

    public static string GroupFor(Guid productId) => "product:" + productId.ToString("N");

    /// <summary>Subscribes this connection to the given products (and replaces nothing: call Unwatch to drop).</summary>
    public async Task<int> Watch(Guid[] productIds)
    {
        if (productIds.Length > MaxWatched)
        {
            throw new HubException($"At most {MaxWatched} products can be watched per connection.");
        }

        foreach (var id in productIds.Distinct())
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(id));
        }

        return productIds.Length;
    }

    public async Task Unwatch(Guid[] productIds)
    {
        foreach (var id in productIds.Distinct().Take(MaxWatched))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(id));
        }
    }
}

/// <summary>
/// The one place that reacts to committed price changes: drop affected cache entries first, then tell clients.
/// In that order, a client that refetches on the push can never be served the pre-change cached result.
/// </summary>
public sealed partial class PriceChangeNotifier(
    ComparisonCache cache, FeedHealthStore health, IHubContext<PriceHub, IPriceClient> hub, ILogger<PriceChangeNotifier> logger)
{
    public async Task PricesChangedAsync(IReadOnlyList<PriceChangedEvent> changes, CancellationToken ct)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var dropped = await cache.InvalidateProductsAsync([.. changes.Select(c => c.ProductId).Distinct()]);
        LogInvalidated(changes.Count, dropped);
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            await hub.Clients.Group(PriceHub.GroupFor(change.ProductId)).PriceChanged(change);
        }
    }

    public Task FeedHealthChangedAsync(FeedHealthChangedEvent change)
    {
        health.InvalidateSnapshot();
        return hub.Clients.All.FeedHealthChanged(change);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Changes} price changes; {Dropped} cached comparisons invalidated")]
    private partial void LogInvalidated(int changes, long dropped);
}

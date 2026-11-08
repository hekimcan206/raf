using Dapper;
using Npgsql;
using Raf.Core.Comparison;

namespace Raf.Api.Baskets;

public sealed record BasketItemDto(Guid ProductId, int Quantity, bool AllowSubstitutes = true);

public sealed record BasketDto(Guid Id, string Name, int Version, DateTimeOffset UpdatedAt, IReadOnlyList<BasketItemDto> Items)
{
    public IReadOnlyList<BasketLine> ToLines() => [.. Items.Select(i => new BasketLine(i.ProductId, i.Quantity, i.AllowSubstitutes))];
}

public enum SaveOutcome
{
    Saved,
    NotFound,
    VersionConflict,
}

/// <summary>
/// Baskets live on the device first; the server copy lets a comparison be requested by id and survives reinstalls.
/// Writes replace the whole item list (the device sends its full basket on resync). Optimistic concurrency through
/// the version is available (If-Match) for clients that share a basket; single-device clients may skip it.
/// </summary>
internal sealed class BasketStore(NpgsqlDataSource dataSource, TimeProvider clock)
{
    public async Task<BasketDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var basket = await connection.QuerySingleOrDefaultAsync<BasketRow>(new CommandDefinition(
            "SELECT id, name, version, updated_at AS UpdatedAt FROM raf.basket WHERE id = @id", new { id }, cancellationToken: ct));
        if (basket is null)
        {
            return null;
        }

        var items = await connection.QueryAsync<BasketItemRow>(new CommandDefinition(
            "SELECT product_id AS ProductId, quantity, allow_substitutes AS AllowSubstitutes FROM raf.basket_item WHERE basket_id = @id ORDER BY product_id",
            new { id }, cancellationToken: ct));
        return new BasketDto(basket.Id, basket.Name, basket.Version, new DateTimeOffset(DateTime.SpecifyKind(basket.UpdatedAt, DateTimeKind.Utc)),
            [.. items.Select(i => new BasketItemDto(i.ProductId, i.Quantity, i.AllowSubstitutes))]);
    }

    /// <summary>Creates the basket with the client-chosen id (so an offline-created basket keeps its id), or replaces it.</summary>
    public async Task<(SaveOutcome Outcome, BasketDto? Basket)> UpsertAsync(
        Guid id, string name, IReadOnlyList<BasketItemDto> items, int? expectedVersion, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var current = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT version FROM raf.basket WHERE id = @id FOR UPDATE", new { id }, tx);

        if (expectedVersion is not null && current != expectedVersion)
        {
            return (current is null ? SaveOutcome.NotFound : SaveOutcome.VersionConflict, null);
        }

        if (current is null)
        {
            await connection.ExecuteAsync(
                "INSERT INTO raf.basket (id, name, version, created_at, updated_at) VALUES (@id, @name, 1, @now, @now)", new { id, name, now }, tx);
        }
        else
        {
            await connection.ExecuteAsync(
                "UPDATE raf.basket SET name = @name, version = version + 1, updated_at = @now WHERE id = @id", new { id, name, now }, tx);
        }

        await connection.ExecuteAsync("DELETE FROM raf.basket_item WHERE basket_id = @id", new { id }, tx);
        var merged = items.GroupBy(i => i.ProductId)
            .Select(g => new BasketItemDto(g.Key, g.Sum(i => i.Quantity), g.All(i => i.AllowSubstitutes)))
            .ToArray();
        await connection.ExecuteAsync("""
            INSERT INTO raf.basket_item (basket_id, product_id, quantity, allow_substitutes)
            SELECT @id, t.product_id, t.quantity, t.allow_substitutes
            FROM unnest(@productIds, @quantities, @allow) AS t(product_id, quantity, allow_substitutes)
            """, new
        {
            id,
            productIds = merged.Select(i => i.ProductId).ToArray(),
            quantities = merged.Select(i => i.Quantity).ToArray(),
            allow = merged.Select(i => i.AllowSubstitutes).ToArray(),
        }, tx);
        await tx.CommitAsync(ct);
        return (SaveOutcome.Saved, await GetAsync(id, ct));
    }

    public async Task<IReadOnlyList<Guid>> UnknownProductsAsync(IEnumerable<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.Distinct().ToArray();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var known = await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM raf.product WHERE id = ANY(@ids)", new { ids }, cancellationToken: ct));
        return [.. ids.Except(known)];
    }

    private sealed class BasketRow
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = "";

        public int Version { get; init; }

        public DateTime UpdatedAt { get; init; }
    }

    private sealed class BasketItemRow
    {
        public Guid ProductId { get; init; }

        public int Quantity { get; init; }

        public bool AllowSubstitutes { get; init; }
    }
}

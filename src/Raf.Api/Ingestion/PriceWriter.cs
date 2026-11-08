using Dapper;
using Npgsql;
using Raf.Api.Realtime;

namespace Raf.Api.Ingestion;

/// <summary>A feed item resolved to a catalogue product.</summary>
public sealed record MatchedPrice(Guid ProductId, long PriceKurus, DateTimeOffset ObservedAt);

/// <summary>
/// The idempotent write path shared by feed ingestion and the demo seed. Two statements in the caller's transaction:
/// <list type="number">
/// <item>append observations; the unique (market, product, observed_at) makes a re-read feed a no-op;</item>
/// <item>upsert the current price only when the new evidence is newer (observed_at moves forward only), and
/// report which prices changed value so caches and clients can react.</item>
/// </list>
/// </summary>
internal static class PriceWriter
{
    public static async Task<(int Inserted, IReadOnlyList<PriceChangedEvent> Changes)> ApplyAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string market, IReadOnlyList<MatchedPrice> prices, string source, long? feedRunId)
    {
        if (prices.Count == 0)
        {
            return (0, []);
        }

        var ids = prices.Select(p => p.ProductId).ToArray();
        var amounts = prices.Select(p => p.PriceKurus).ToArray();
        var observed = prices.Select(p => p.ObservedAt.UtcDateTime).ToArray();

        var inserted = await connection.ExecuteAsync("""
            INSERT INTO raf.price_observation (market_code, product_id, price_kurus, observed_at, source, feed_run_id)
            SELECT @market, t.product_id, t.price_kurus, t.observed_at, @source, @feedRunId
            FROM unnest(@ids, @amounts, @observed) AS t(product_id, price_kurus, observed_at)
            ON CONFLICT ON CONSTRAINT price_observation_once DO NOTHING
            """, new { market, source, feedRunId, ids, amounts, observed }, tx);

        // `prev` reads the pre-statement snapshot (all CTEs of one statement share it), so it holds the old price.
        var rows = await connection.QueryAsync<ChangeRow>("""
            WITH incoming AS (
                SELECT * FROM unnest(@ids, @amounts, @observed) AS t(product_id, price_kurus, observed_at)
            ), prev AS (
                SELECT cp.product_id, cp.price_kurus
                FROM raf.current_price cp
                WHERE cp.market_code = @market AND cp.product_id = ANY(@ids)
            ), up AS (
                INSERT INTO raf.current_price AS cp (market_code, product_id, price_kurus, observed_at, price_since, updated_at)
                SELECT @market, product_id, price_kurus, observed_at, observed_at, now() FROM incoming
                ON CONFLICT (market_code, product_id) DO UPDATE SET
                    price_since = CASE WHEN cp.price_kurus = EXCLUDED.price_kurus THEN cp.price_since ELSE EXCLUDED.observed_at END,
                    price_kurus = EXCLUDED.price_kurus,
                    observed_at = EXCLUDED.observed_at,
                    updated_at  = now()
                WHERE EXCLUDED.observed_at > cp.observed_at
                RETURNING cp.product_id, cp.price_kurus, cp.observed_at
            )
            SELECT up.product_id AS ProductId, prev.price_kurus AS OldPriceKurus, up.price_kurus AS NewPriceKurus, up.observed_at AS ObservedAt
            FROM up LEFT JOIN prev ON prev.product_id = up.product_id
            WHERE prev.price_kurus IS DISTINCT FROM up.price_kurus
            """, new { market, ids, amounts, observed }, tx);

        var changes = rows
            .Select(r => new PriceChangedEvent(r.ProductId, market, r.OldPriceKurus, r.NewPriceKurus,
                new DateTimeOffset(DateTime.SpecifyKind(r.ObservedAt, DateTimeKind.Utc))))
            .ToList();
        return (inserted, changes);
    }

    private sealed class ChangeRow
    {
        public Guid ProductId { get; init; }

        public long? OldPriceKurus { get; init; }

        public long NewPriceKurus { get; init; }

        public DateTime ObservedAt { get; init; }
    }
}

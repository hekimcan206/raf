using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using Raf.Api.Catalog;
using Raf.Api.Ingestion;
using Raf.Simulation;

namespace Raf.Api.Demo;

/// <summary>
/// Loads the synthetic catalogue, the four fictional markets, Tazelik's SKU mapping, two weeks of price history
/// and a demo basket. Runs as the schema owner (reference data is read-only for the API role) and is idempotent:
/// fixed ids, ON CONFLICT DO NOTHING, and the unique observation key.
/// </summary>
internal sealed partial class DemoSeeder(NpgsqlDataSource dataSource, TimeProvider clock, ILogger<DemoSeeder> logger)
{
    public static readonly Guid DemoBasketId = Guid.Parse("5eed0000-0000-4000-8000-00000000ba5e");

    public const int HistoryDays = 14;

    /// <summary>Deterministic product id derived from the GTIN, so every environment (tests, compose, app) agrees.</summary>
    public static Guid ProductId(string gtin14)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes("raf-product:" + gtin14))[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version-5-like marker
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    /// <summary>
    /// The demo basket is chosen to show every rule: a brand substitution (Bereket lacks Dağ Süt milk), a smaller-pack
    /// substitution (Çarşı lacks 15 eggs: 2 × 10), a gap the overbuy cap refuses (Çarşı lacks 1 L olive oil; 2 L
    /// would be 100 % extra), Kuzey's nightly (ageing) prices, and Tazelik's stale prices.
    /// </summary>
    public static IReadOnlyList<(int Index, int Quantity)> DemoBasket { get; } =
        [(2, 2), (4, 1), (9, 1), (12, 2), (19, 1), (22, 1), (24, 1), (26, 1), (28, 1), (34, 1)];

    public async Task RunAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            foreach (var market in MarketProfiles.All)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO raf.market (code, name) VALUES (@Code, @Name) ON CONFLICT (code) DO NOTHING", new { market.Code, market.Name }, tx);
            }

            foreach (var p in SyntheticCatalog.Products)
            {
                await connection.ExecuteAsync("""
                    INSERT INTO raf.product (id, gtin, name, brand, category, size_dimension, size_base_amount, substitution_group)
                    VALUES (@id, @gtin, @name, @brand, @category, @dimension, @amount, @group)
                    ON CONFLICT (id) DO NOTHING
                    """, new
                {
                    id = ProductId(p.Gtin14),
                    gtin = p.Gtin14,
                    name = p.Name,
                    brand = p.Brand,
                    category = p.Category,
                    dimension = CatalogStore.ToDimension(p.Size.Dimension),
                    amount = p.Size.BaseAmount,
                    group = p.SubstitutionGroup,
                }, tx);
                await connection.ExecuteAsync("""
                    INSERT INTO raf.market_sku (market_code, sku, product_id) VALUES ('tazelik', @sku, @id)
                    ON CONFLICT DO NOTHING
                    """, new { sku = p.TazelikSku, id = ProductId(p.Gtin14) }, tx);
            }

            await tx.CommitAsync(ct);
        }

        // History: one price list per market per day at 12:00 UTC, oldest first, through the same idempotent
        // write path as live ingestion. Live jobs then continue from "now".
        var model = new PriceModel(new MarketControl(clock, chaosEnabled: false));
        var today = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var inserted = 0;
        for (var day = HistoryDays; day >= 0; day--)
        {
            var at = day == 0 ? clock.GetUtcNow() : today.AddDays(-day).AddHours(12);
            await using var tx = await connection.BeginTransactionAsync(ct);
            foreach (var market in MarketProfiles.All)
            {
                var prices = model.PriceList(market, at)
                    .Select(p => new MatchedPrice(ProductId(p.Product.Gtin14), p.PriceKurus, p.ObservedAt))
                    .ToList();
                inserted += (await PriceWriter.ApplyAsync(connection, tx, market.Code, prices, "seed", null)).Inserted;
            }

            await tx.CommitAsync(ct);
        }

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await connection.ExecuteAsync("""
                INSERT INTO raf.basket (id, name) VALUES (@id, 'Haftalık alışveriş') ON CONFLICT (id) DO NOTHING
                """, new { id = DemoBasketId }, tx);
            foreach (var (index, quantity) in DemoBasket)
            {
                await connection.ExecuteAsync("""
                    INSERT INTO raf.basket_item (basket_id, product_id, quantity) VALUES (@id, @product, @quantity) ON CONFLICT DO NOTHING
                    """, new { id = DemoBasketId, product = ProductId(SyntheticCatalog.ByIndex(index).Gtin14), quantity }, tx);
            }

            await tx.CommitAsync(ct);
        }

        LogSeeded(SyntheticCatalog.Products.Count, MarketProfiles.All.Count, inserted);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo seed: {Products} products, {Markets} markets, {Inserted} new price observations")]
    private partial void LogSeeded(int products, int markets, int inserted);
}

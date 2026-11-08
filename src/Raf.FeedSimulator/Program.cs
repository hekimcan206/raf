using System.Globalization;
using Raf.Core.Catalog;
using Raf.Simulation;

namespace Raf.FeedSimulator;

/// <summary>
/// Four fictional markets publishing prices in their own formats, with their own latency, flakiness and outages.
/// Stands in for real retailer feeds: nothing is scraped and no real retailer is imitated.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args) => await BuildApp(args).RunAsync();

    public static WebApplication BuildApp(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(sp => new MarketControl(
            sp.GetRequiredService<TimeProvider>(),
            builder.Configuration.GetValue("Simulator:Chaos", defaultValue: true)));
        builder.Services.AddSingleton<PriceModel>();

        var app = builder.Build();
        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

        foreach (var market in MarketProfiles.All)
        {
            app.MapGet(market.FeedPath, async (HttpContext http, PriceModel model, MarketControl control, TimeProvider clock, int? page) =>
            {
                var (behaviour, latency) = control.NextRequest(market);
                switch (behaviour)
                {
                    case FeedBehaviour.Outage:
                        http.Response.Headers.RetryAfter = "60";
                        return Results.Problem($"{market.Name} feed is under maintenance.", statusCode: StatusCodes.Status503ServiceUnavailable);
                    case FeedBehaviour.Hang:
                        // Longer than any sane client timeout; the ingestion job must give up, not wait.
                        await Task.Delay(latency, clock, http.RequestAborted);
                        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
                    case FeedBehaviour.RandomFailure:
                        await Task.Delay(latency, clock, http.RequestAborted);
                        return Results.Problem("Upstream error.", statusCode: StatusCodes.Status502BadGateway);
                }

                if (latency > TimeSpan.Zero)
                {
                    await Task.Delay(latency, clock, http.RequestAborted);
                }

                var now = clock.GetUtcNow();
                var prices = model.PriceList(market, now);
                var body = market.Code switch
                {
                    "bereket" => FeedWriters.Bereket(prices, now),
                    "carsi" => FeedWriters.Carsi(prices),
                    "kuzey" => FeedWriters.Kuzey(prices, PriceModel.PublicationTime(market, now)),
                    _ => FeedWriters.TazelikPage(prices, Math.Max(1, page ?? 1)),
                };
                return Results.Text(body, market.ContentType);
            }).WithName($"feed-{market.Code}");
        }

        // Admin endpoints for demos and tests. The simulator is never exposed beyond the compose network/loopback.
        var admin = app.MapGroup("/admin/markets");
        admin.MapGet("/", (MarketControl control) => MarketProfiles.All.Select(m => new
        {
            m.Code,
            m.Name,
            outageUntil = control.OutageUntil(m.Code),
            chaos = control.ChaosEnabled,
        }));
        admin.MapPost("/{code}/outage", (string code, int? seconds, MarketControl control) =>
        {
            var market = MarketProfiles.All.FirstOrDefault(m => m.Code == code);
            if (market is null)
            {
                return Results.NotFound();
            }

            control.StartOutage(code, TimeSpan.FromSeconds(Math.Clamp(seconds ?? 300, 1, 86_400)));
            return Results.Ok(new { code, outageUntil = control.OutageUntil(code) });
        });
        admin.MapDelete("/{code}/outage", (string code, MarketControl control) =>
        {
            control.EndOutage(code);
            return Results.NoContent();
        });
        admin.MapPost("/{code}/prices", (string code, PriceChange change, MarketControl control) =>
        {
            if (MarketProfiles.All.All(m => m.Code != code))
            {
                return Results.NotFound();
            }

            if (!Gtin.TryNormalize(change.Gtin, out var gtin14) || change.PriceKurus <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["gtin"] = ["A valid GTIN and a positive price are required."] });
            }

            control.SetPrice(code, gtin14, change.PriceKurus);
            return Results.Ok(new { code, gtin = gtin14, change.PriceKurus });
        });
        admin.MapDelete("/{code}/prices", (string code, MarketControl control) =>
        {
            control.ClearPrices(code);
            return Results.NoContent();
        });

        app.MapGet("/catalog", () => SyntheticCatalog.Products.Select(p => new
        {
            p.Index,
            gtin = p.Ean13,
            p.Name,
            size = p.Size.ToString(),
            basePrice = (p.BasePriceKurus / 100m).ToString("0.00", CultureInfo.InvariantCulture),
        }));
    }

    /// <summary>Body of a manual price change.</summary>
    public sealed record PriceChange(string Gtin, long PriceKurus);
}

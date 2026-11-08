using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Raf.Api.Baskets;
using Raf.Api.Catalog;
using Raf.Api.Comparison;
using Raf.Api.Ingestion;
using Raf.Core.Comparison;

namespace Raf.Api.Features;

public sealed record SaveBasketRequest(string? Name, IReadOnlyList<BasketItemDto>? Items);

public sealed record CompareRequest(IReadOnlyList<BasketItemDto>? Items);

internal static class Endpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/products", async (string? q, string? category, int? limit, CatalogStore store, CancellationToken ct) =>
                TypedResults.Ok(await store.SearchAsync(q, category, Math.Clamp(limit ?? 100, 1, 200), ct)))
            .WithSummary("Search the catalogue by name or barcode; includes the lowest current price and how many markets list it.");

        api.MapGet("/products/{id:guid}/prices", async Task<Results<Ok<IReadOnlyList<PricePointDto>>, NotFound>> (
                Guid id, int? days, CatalogStore store, CancellationToken ct) =>
                await store.ProductExistsAsync(id, ct)
                    ? TypedResults.Ok(await store.HistoryAsync(id, Math.Clamp(days ?? 14, 1, 90), ct))
                    : TypedResults.NotFound())
            .WithSummary("Append-only price history of a product in every market.");

        api.MapGet("/markets", async (FeedHealthStore health, CancellationToken ct) => TypedResults.Ok(await health.GetAllAsync(ct)))
            .WithSummary("Markets with their feed health (healthy, degraded, down, unknown).");

        api.MapGet("/feeds/runs", async (string? market, int? limit, FeedHealthStore health, CancellationToken ct) =>
                TypedResults.Ok(await health.RecentRunsAsync(market, Math.Clamp(limit ?? 50, 1, 500), ct)))
            .WithSummary("Recent ingestion runs: outcome, HTTP status, items received/matched/rejected, price changes.");
        return api;
    }

    public static RouteGroupBuilder MapBasketEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/baskets/{id:guid}", async Task<Results<Ok<BasketDto>, NotFound>> (Guid id, HttpContext http, BasketStore store, CancellationToken ct) =>
        {
            var basket = await store.GetAsync(id, ct);
            if (basket is null)
            {
                return TypedResults.NotFound();
            }

            http.Response.Headers.ETag = ETag(basket.Version);
            return TypedResults.Ok(basket);
        });

        api.MapPut("/baskets/{id:guid}", async Task<IResult> (Guid id, SaveBasketRequest request, HttpContext http, BasketStore store, CancellationToken ct) =>
        {
            var errors = Validate(request.Items);
            var name = string.IsNullOrWhiteSpace(request.Name) ? "Basket" : request.Name.Trim();
            if (name.Length > 80)
            {
                errors["name"] = ["At most 80 characters."];
            }

            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            var unknown = await store.UnknownProductsAsync(request.Items!.Select(i => i.ProductId), ct);
            if (unknown.Count > 0)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [$"Unknown products: {string.Join(", ", unknown)}"] });
            }

            int? expected = null;
            if (http.Request.Headers.IfMatch.FirstOrDefault() is { } ifMatch)
            {
                if (!int.TryParse(ifMatch.Trim('"', 'W', '/'), NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["If-Match"] = ["Expected an ETag returned by this API."] });
                }

                expected = v;
            }

            var (outcome, basket) = await store.UpsertAsync(id, name, request.Items!, expected, ct);
            switch (outcome)
            {
                case SaveOutcome.NotFound:
                    return TypedResults.Problem("Basket does not exist; omit If-Match to create it.", statusCode: StatusCodes.Status412PreconditionFailed);
                case SaveOutcome.VersionConflict:
                    return TypedResults.Problem("Basket was changed by another client.", statusCode: StatusCodes.Status412PreconditionFailed);
            }

            http.Response.Headers.ETag = ETag(basket!.Version);
            return TypedResults.Ok(basket);
        }).WithSummary("Create or replace a basket under a client-chosen id (offline-created baskets keep their id). Optional If-Match.");

        api.MapGet("/baskets/{id:guid}/comparison", async Task<IResult> (Guid id, HttpContext http, BasketStore store, ComparisonService comparisons, CancellationToken ct) =>
        {
            var basket = await store.GetAsync(id, ct);
            if (basket is null)
            {
                return TypedResults.NotFound();
            }

            if (basket.Items.Count == 0)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["The basket is empty."] });
            }

            return await CompareAsync(basket.ToLines(), http, comparisons, ct);
        }).WithSummary("Compare a saved basket across all markets.");

        api.MapPost("/comparisons", async Task<IResult> (CompareRequest request, HttpContext http, ComparisonService comparisons, CancellationToken ct) =>
        {
            var errors = Validate(request.Items);
            return errors.Count > 0
                ? TypedResults.ValidationProblem(errors)
                : await CompareAsync([.. request.Items!.Select(i => new BasketLine(i.ProductId, i.Quantity, i.AllowSubstitutes))], http, comparisons, ct);
        }).WithSummary("Compare an ad-hoc basket: per-market totals with unit-price normalisation, substitutions and per-line freshness.");

        return api;
    }

    private static async Task<IResult> CompareAsync(IReadOnlyCollection<BasketLine> lines, HttpContext http, ComparisonService comparisons, CancellationToken ct)
    {
        try
        {
            var result = await comparisons.CompareAsync(lines, ct);
            http.Response.Headers["X-Cache"] = result.Cache;
            // The client caches the result itself (offline mode); intermediaries must not, because freshness is per request.
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(result);
        }
        catch (UnknownProductException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [ex.Message] });
        }
    }

    private static Dictionary<string, string[]> Validate(IReadOnlyList<BasketItemDto>? items)
    {
        var errors = new Dictionary<string, string[]>();
        if (items is null || items.Count == 0)
        {
            errors["items"] = ["At least one item is required."];
        }
        else if (items.Count > BasketComparer.MaxLines)
        {
            errors["items"] = [$"At most {BasketComparer.MaxLines} items."];
        }
        else if (items.Any(i => i.Quantity is < 1 or > BasketComparer.MaxQuantity))
        {
            errors["items"] = [$"Quantities must be between 1 and {BasketComparer.MaxQuantity}."];
        }
        else if (items.GroupBy(i => i.ProductId).Any(g => g.Sum(i => i.Quantity) > BasketComparer.MaxQuantity))
        {
            errors["items"] = [$"The total quantity of one product must not exceed {BasketComparer.MaxQuantity}."];
        }

        return errors;
    }

    private static string ETag(int version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
}

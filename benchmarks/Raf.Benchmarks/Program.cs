using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

// Basket comparison latency, cold (cache miss: PostgreSQL snapshot + compare + Redis write) vs warm (Redis hit),
// against a running stack (`docker compose up`). Usage:
//   dotnet run -c Release --project benchmarks/Raf.Benchmarks -- [baseUrl] [baskets] [lines] [concurrency]
// Every basket has distinct content, so the first pass is all misses; the second pass replays the same baskets.

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:58280";
var baskets = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 500;
var lines = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 10;
var concurrency = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
var products = (await http.GetFromJsonAsync<List<ProductRow>>("/api/v1/products?limit=200", JsonSerializerOptions.Web))!;
if (products.Count < lines)
{
    throw new InvalidOperationException($"Catalogue has {products.Count} products; cannot build {lines}-line baskets.");
}

// Deterministic baskets: a run is reproducible, and distinct (product set, quantities) give distinct cache keys.
var bodies = MakeBaskets(baskets, seed: 20261107);

// Warm-up on separate baskets, at the measured concurrency: JIT, thread pool, Npgsql and Redis connections, PostgreSQL
// plan cache. Both paths (miss, then hit) are exercised. Not measured: an earlier version warmed up sequentially and
// the first concurrent pass then mostly measured connection-pool growth.
var warmup = MakeBaskets(Math.Max(100, concurrency * 25), seed: 1);
await PassAsync("warm-up miss", warmup);
await PassAsync("warm-up hit", warmup);

var cold = await PassAsync("cold (first request per basket)", bodies);
var warm = await PassAsync("warm (same baskets again)", bodies);

Console.WriteLine();
Console.WriteLine($"| pass | n | cache hit/miss/bypass | p50 ms | p95 ms | p99 ms | mean ms | req/s |");
Console.WriteLine($"|------|---|-----------------------|--------|--------|--------|---------|-------|");
Print(cold);
Print(warm);
Console.WriteLine();
Console.WriteLine(FormattableString.Invariant($"lines per basket: {lines}, concurrency: {concurrency}, p95 speed-up: {cold.P(0.95) / warm.P(0.95):0.0}x"));

List<string> MakeBaskets(int count, int seed)
{
    var random = new Random(seed);
    return [.. Enumerable.Range(0, count).Select(i =>
    {
        var items = products.OrderBy(_ => random.Next()).Take(lines)
            .Select(p => new BasketItem(p.Id, random.Next(1, 4)))
            .ToList();
        // Make each basket's content unique even if the random choice repeats.
        items[0] = items[0] with { Quantity = 1 + (i % 9) };
        items[1] = items[1] with { Quantity = 1 + (i / 9 % 9) };
        return JsonSerializer.Serialize(new { items }, JsonSerializerOptions.Web);
    })];
}

async Task<Result> PassAsync(string name, IReadOnlyList<string> payloads)
{
    var latencies = new double[payloads.Count];
    var caches = new string[payloads.Count];
    var next = -1;
    var total = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
    {
        int i;
        while ((i = Interlocked.Increment(ref next)) < payloads.Count)
        {
            var sw = Stopwatch.StartNew();
            caches[i] = await PostAsync(http, payloads[i]);
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }
    }));
    total.Stop();
    return new Result(name, latencies, caches, total.Elapsed);
}

static async Task<string> PostAsync(HttpClient http, string body)
{
    using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
    using var response = await http.PostAsync("/api/v1/comparisons", content);
    response.EnsureSuccessStatusCode();
    await response.Content.ReadAsByteArrayAsync(); // include body transfer in the measurement
    return response.Headers.TryGetValues("X-Cache", out var v) ? v.First() : "?";
}

static void Print(Result r) => Console.WriteLine(FormattableString.Invariant(
    $"| {r.Name} | {r.Latencies.Length} | {r.Count("hit")}/{r.Count("miss")}/{r.Count("bypass")} | {r.P(0.50):0.00} | {r.P(0.95):0.00} | {r.P(0.99):0.00} | {r.Latencies.Average():0.00} | {r.Latencies.Length / r.Elapsed.TotalSeconds:0} |"));

internal sealed record ProductRow(Guid Id, string Name);

internal sealed record BasketItem(Guid ProductId, int Quantity, bool AllowSubstitutes = true);

internal sealed record Result(string Name, double[] Latencies, string[] Caches, TimeSpan Elapsed)
{
    public int Count(string cache) => Caches.Count(c => c == cache);

    /// <summary>Nearest-rank percentile.</summary>
    public double P(double q)
    {
        var sorted = Latencies.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}

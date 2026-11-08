# Benchmarks: basket comparison latency, uncached vs Redis miss vs Redis hit

## Question

How much does the Redis comparison cache ([ADR 0003](adr/0003-comparison-cache-and-invalidation.md)) buy on the
hot endpoint, `POST /api/v1/comparisons`? And what does a cache miss cost compared with not having a cache at all?

## Method

- Harness: [`benchmarks/Raf.Benchmarks`](../benchmarks/Raf.Benchmarks/Program.cs), an HTTP client against the
  running compose stack (`docker compose up`, API on `localhost:58280`). Every request is a full HTTP round trip
  including the response body.
- Baskets: N baskets with **distinct content** (deterministic seed). The first pass is therefore all cache misses.
  The second pass replays the same baskets, which are all hits unless a price changes in between. The `X-Cache`
  header is counted, so hits and misses are reported, not assumed.
- Warm-up: a separate set of baskets is run twice at the measured concurrency before measuring (JIT, thread pool,
  Npgsql and Redis connections, PostgreSQL plan cache). An earlier version of the harness warmed up sequentially. Its
  first concurrent pass mostly measured connection-pool growth (p50 ≈ 35 ms with *or without* cache), and those
  numbers were discarded.
- Three configurations: 10-line baskets sequential, 25-line baskets sequential, 10-line baskets with 16 concurrent
  clients.
- Three modes:
  - **uncached**: `CACHE_ENABLED=false`, every request loads the snapshot from PostgreSQL and computes
    (`X-Cache: bypass`, no Redis calls at all);
  - **Redis miss**: cache on, first pass (MGET entry + epoch, PostgreSQL snapshot, compute, Lua check-and-set with
    the reverse index);
  - **Redis hit**: cache on, second pass (one MGET, deserialise, freshness re-evaluated, feed health from the 5 s
    snapshot).
- Redis was flushed (`FLUSHALL`) before every cache-on run. Each cell is the **median of 3 runs**. Percentiles are
  nearest-rank.
- Data: the demo seed. 36 products, 4 markets, about 1,900 price observations of history, current prices kept up to
  date by the live Hangfire jobs during the run.

## Environment

| | |
|-|-|
| CPU | Intel Core i7-13620H (10 cores / 16 threads), laptop |
| RAM | 15.7 GB (Docker Desktop VM: 16 vCPU, 7.6 GB) |
| OS | Windows 11 Home 10.0.26200, Docker Desktop 29.9.1 (WSL 2) |
| Runtime | .NET 10.0.4xx (SDK 10.0.401), API in the chiseled `aspnet:10.0` image; PostgreSQL 17-alpine; Redis 7-alpine |
| Harness | Release build, running on the Windows host (so every request crosses the Docker Desktop port forward) |
| Host load | other unrelated containers were running on the same Docker engine (11 at measurement time, including another project's database); runs show visible run-to-run noise |
| Date | 2026-11-08 |

## Results

Latency in ms; throughput in requests per second. Each cell is the median of 3 runs of 500 requests (1,000 for
concurrency 16).

### 10 lines, sequential

| mode | p50 | p95 | p99 | req/s |
|------|-----|-----|-----|-------|
| uncached (PostgreSQL every time) | 2.45 | 6.43 | 12.82 | 327 |
| Redis miss | 3.50 | 8.69 | 18.42 | 214 |
| **Redis hit** | **1.41** | **3.66** | **8.79** | **553** |

### 25 lines, sequential

| mode | p50 | p95 | p99 | req/s |
|------|-----|-----|-----|-------|
| uncached | 2.77 | 6.18 | 12.85 | 298 |
| Redis miss | 3.54 | 7.66 | 14.91 | 233 |
| **Redis hit** | **1.94** | **5.75** | **12.47** | **363** |

### 10 lines, 16 concurrent clients

| mode | p50 | p95 | p99 | req/s |
|------|-----|-----|-----|-------|
| uncached | 5.00 | 12.65 | 19.03 | 2,624 |
| Redis miss | 7.34 | 15.93 | 23.64 | 1,912 |
| **Redis hit** | **3.02** | **10.43** | **15.34** | **4,164** |

### Raw runs

<details>
<summary>All 27 measured runs (median taken per cell)</summary>

```
uncached (CACHE_ENABLED=false), cold pass shown; the second pass is the same path
10 lines, c=1   p50 3.43 / 2.39 / 2.45   p95 7.29 / 6.43 / 5.82    p99 21.39 / 12.82 / 10.84   rps 207 / 327 / 343
25 lines, c=1   p50 3.19 / 2.77 / 2.67   p95 8.38 / 6.18 / 4.98    p99 12.85 / 12.98 / 9.81    rps 257 / 298 / 326
10 lines, c=16  p50 5.00 / 7.48 / 4.93   p95 12.65 / 30.97 / 11.17 p99 19.03 / 402.27 / 15.75  rps 2624 / 920 / 2871

Redis miss (first pass)
10 lines, c=1   p50 5.60 / 3.21 / 3.50   p95 11.27 / 8.69 / 6.86   p99 18.42 / 26.84 / 9.83    rps 155 / 214 / 252
25 lines, c=1   p50 3.85 / 3.35 / 3.54   p95 10.95 / 7.62 / 7.66   p99 18.45 / 12.84 / 14.91   rps 206 / 252 / 233
10 lines, c=16  p50 7.34 / 7.38 / 7.19   p95 15.93 / 24.24 / 12.29 p99 23.64 / 55.04 / 17.71   rps 1912 / 1656 / 2078

Redis hit (second pass; all 500/1000 requests were hits in these runs)
10 lines, c=1   p50 2.72 / 1.32 / 1.41   p95 9.83 / 3.04 / 3.66    p99 31.78 / 8.79 / 6.29     rps 232 / 585 / 553
25 lines, c=1   p50 1.94 / 2.12 / 1.91   p95 4.34 / 5.93 / 5.75    p99 8.02 / 12.47 / 19.55    rps 433 / 363 / 356
10 lines, c=16  p50 3.62 / 2.79 / 3.02   p95 10.43 / 8.18 / 11.19  p99 15.47 / 14.37 / 15.34   rps 3638 / 4580 / 4164
```

</details>

## Reading the numbers

- **A hit is about 1.7× faster than computing from PostgreSQL** at p50 (1.41 vs 2.45 ms sequential) and at p95
  (3.66 vs 6.43 ms). Under 16 concurrent clients it serves **1.6× the throughput** (4,164 vs 2,624 req/s).
- **A miss costs about 1 ms more than having no cache** (3.50 vs 2.45 ms p50): one MGET before the database and one
  Lua check-and-set after it. With every price changing at most a few times per hour per market, and many shoppers
  sharing similar baskets, most requests are hits. On this host the break-even is at a hit ratio of roughly 50 %
  (1 ms extra on a miss vs 1 ms saved on a hit).
- **The database path is already cheap here**: three small queries in one round trip over a 36-product catalogue. The
  cache's absolute gain is therefore milliseconds, not tens of milliseconds. A catalogue in the tens of thousands, as
  in the public Market Fiyatı service, with more markets and branch-level prices, would make the snapshot query
  heavier. That case was **not** measured here, and no number is claimed for it.
- Invalidation works in practice, too. In an earlier 25-line run (before the MGET change, not part of the table), a live Hangfire ingestion
  changed prices in the middle of the second pass. 208 of the 500 "warm" requests came back as misses, because the
  affected entries had been dropped. That is the intended behaviour (correct, slower), and it is why the harness
  reports hit/miss counts instead of assuming them.
- Two changes came out of this measurement (commit `perf(api): …`): feed health served from a 5 s in-process
  snapshot (a hit no longer touches PostgreSQL), and entry + epoch read in a single MGET (a miss saves one Redis round
  trip). Their individual effect is within this host's run-to-run noise, so no separate gain is claimed.
- The p99 values and the occasional outlier run (402 ms p99 for uncached at c=16) come from host contention, with
  other containers sharing the Docker VM. Medians of 3 runs are reported for that reason.

## Reproduce

```bash
docker compose up -d --build                   # wait for http://localhost:58280/health/ready
docker exec raf-redis-1 redis-cli FLUSHALL
dotnet run -c Release --project benchmarks/Raf.Benchmarks -- http://localhost:58280 500 10 1
dotnet run -c Release --project benchmarks/Raf.Benchmarks -- http://localhost:58280 500 25 1
docker exec raf-redis-1 redis-cli FLUSHALL
dotnet run -c Release --project benchmarks/Raf.Benchmarks -- http://localhost:58280 1000 10 16

# uncached baseline
CACHE_ENABLED=false docker compose up -d api
dotnet run -c Release --project benchmarks/Raf.Benchmarks -- http://localhost:58280 500 10 1
docker compose up -d api                       # back to cached
```

Arguments: `baseUrl baskets linesPerBasket concurrency`.

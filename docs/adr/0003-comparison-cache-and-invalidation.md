# ADR 0003: Redis cache-aside for comparisons with targeted invalidation and an epoch guard

- Status: accepted
- Date: 2026-11-08

## Context

The hot path is "compare this basket": load the products, their substitution-group members and every market's
current price, then run the comparer. Many shoppers have near-identical baskets, and prices change at most a few
times per hour per market. A comparison can be cached, with two complications:

1. **Prices change.** A cached result must disappear when a price it depends on changes, not after a TTL that
   happens to be long enough to mislead someone.
2. **Freshness depends on the time of reading.** A result computed at 09:00 with a 6-hour-old price is "aging" by
   noon (ADR 0001).

## Options considered

- **TTL only (e.g. 60 s).** Simple, but either too short to help or long enough to serve outdated totals after a
  price change.
- **Flush everything on any price change.** Correct, but with four markets re-publishing every few minutes the hit
  rate would be close to zero.
- **Versioned keys** (key includes a per-product version). Requires reading N versions before every lookup.
- **Targeted invalidation via a reverse index, plus an epoch guard (chosen).**

## Decision

- **Key** = SHA-256 of the normalised basket (lines merged by product, sorted, quantity and substitution flag), so equal
  baskets share one entry regardless of line order.
- **Value** = the price part of the comparison only (`BasketComparison`): totals, lines and `observed_at`.
  **Freshness labels, stale share and feed health are computed per request** from that value.
- **Reverse index**: for every product the result depends on (the requested items *and every member of their
  substitution groups*, because a cheaper substitute appearing changes the result), a Redis set
  `raf:cmp:idx:{productId}` lists the cache keys to drop.
- **Invalidation** happens after the ingestion transaction commits, and before the SignalR push. A Lua script
  increments a global `raf:price-epoch`, then deletes every entry in the affected index sets. Because the push comes
  after the delete, a client that refetches when it receives the push can never get the pre-change result.
- **Epoch guard.** Race: request R reads prices, ingestion commits a change and invalidates, and then R writes its
  now-outdated result. R prevents this by reading the epoch *before* loading prices. The write is a Lua
  check-and-set that only stores if the epoch is unchanged. An integration test covers the race.
- **TTL** = min(`Comparison:CacheTtl` (10 min), `ValidUntil − now`), where `ValidUntil` is the first moment a used
  price expires. Read-side guard: an entry past `ValidUntil` is ignored.
- **Redis is an optimisation, never a dependency.** Timeouts are short (500 ms), and connecting does not fail fast.
  Any Redis error means the comparison is computed directly (`X-Cache: bypass`). Redis reports `Degraded` in health,
  and it is not part of readiness. An integration test runs the API against a closed port.

## Consequences

- Measured on the demo stack: see [docs/benchmarks.md](../benchmarks.md) (cold vs Redis-cached p50/p95/p99).
- Invalidation granularity is the product, across all markets. A change in one market drops the entries for that
  product in every market. That is simpler than per-market entries, and still targeted (unrelated baskets keep their
  entries; tested).
- The Lua scripts use keys passed as arguments (the index set names), which is not Redis Cluster-safe. That is
  acceptable for a single node. Sharding would need hash tags or a per-key script.
- If invalidation fails (Redis down at that moment), entries live until their TTL (at most 10 minutes) and an error
  is logged. The epoch still prevents writes of results computed before the change.
- `Cache-Control: no-store` on comparison responses: the device keeps its own copy for offline use, and
  intermediaries must not cache a response whose freshness labels depend on the time of the request.

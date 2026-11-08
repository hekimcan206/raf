# Hangfire job plan

Storage: PostgreSQL, schema `hangfire` (Hangfire 1.8.25, Hangfire.PostgreSql 1.21.1). It is installed by the
migrator, and the API role has DML only ([ADR 0004](adr/0004-hangfire-ingestion-on-postgresql.md)). The server runs
inside the API process (`Jobs:ServerEnabled`), with 4 workers on queues `ingest` and `maintenance`. The schedule
polling interval is 5 s. All cron expressions are UTC.

## Jobs

| id | queue | cron (default) | what it does | retries | concurrency |
|----|-------|----------------|--------------|---------|-------------|
| `ingest-bereket` | ingest | `* * * * *` (every minute) | fetch JSON feed → parse → match → idempotent write → health | none | advisory lock per market; overlapping run is skipped |
| `ingest-carsi` | ingest | `*/2 * * * *` | same, CSV feed | none | same |
| `ingest-kuzey` | ingest | `*/5 * * * *` | same, nightly XML list (most runs change nothing) | none | same |
| `ingest-tazelik` | ingest | `*/3 * * * *` | same, paged JSON (all pages or nothing) | none | same |
| `feed-health-watchdog` | maintenance | `* * * * *` | marks markets with no attempt for `Feeds:SilentAfter` (15 min) as `unknown`; broadcasts the change over SignalR | none | idempotent UPDATE |
| `prune-feed-runs` | maintenance | `17 3 * * *` | deletes `raf.feed_run` rows older than `Jobs:FeedRunRetention` (14 d); observations are kept (their `feed_run_id` becomes NULL) | 3 (Hangfire default backoff) | idempotent DELETE |

Schedules come from `Feeds:Markets:{code}:Cron` and are registered with `AddOrUpdate` at every start
(`JobRegistration`), so the code is the source of truth. Polling intervals are deliberately shorter than each market's
republish cadence (10 min / 15 min / nightly / 6 h). A new list is picked up within one interval, and re-reading an
unchanged list costs one HTTP request and a no-op insert.

## One ingestion run

```mermaid
sequenceDiagram
    participant H as Hangfire worker
    participant S as FeedIngestionService
    participant PG as PostgreSQL
    participant F as Market feed
    participant R as Redis
    participant C as SignalR clients
    H->>S: IngestAsync(market)
    S->>PG: pg_try_advisory_lock(hashtext('raf:ingest:'||market))
    alt lock taken by another instance
        S-->>H: skipped
    else
        loop every page (1 for single-document feeds)
            S->>F: GET (5 s timeout, no retry)
        end
        alt HTTP error / timeout / unparseable
            S->>PG: INSERT feed_run(failed) + health: degraded → down after 3
            S->>C: feedHealthChanged (if status changed)
        else
            S->>PG: match GTIN / SKU, check pack size, reject implausible items
            S->>PG: BEGIN; feed_run; INSERT observations ON CONFLICT DO NOTHING;<br/>UPSERT current_price WHERE newer RETURNING changes; health; COMMIT
            S->>R: INCR epoch; DEL entries indexed under changed products
            S->>C: priceChanged → group product:{id}
        end
        S->>PG: pg_advisory_unlock
    end
```

## Failure semantics

| situation | what happens | where it shows |
|-----------|--------------|----------------|
| feed 5xx / timeout / hang | run recorded `failed`, nothing written, health `degraded` (1st, 2nd) then `down` (3rd in a row); the next schedule retries | `/api/v1/feeds/runs`, `/api/v1/markets`, `feedHealthChanged` push, `raf.feed.runs{status=failed}` metric |
| unparseable document | same as above, `error = unparseable feed on page n: …` | same |
| individual bad items | item rejected (bad GTIN, price ≤ 0 or > 100 000 TL, `observed_at` > now + 5 min, size mismatch), rest applied | `items_rejected`, first 5 reasons in `feed_run.error` |
| unknown SKU / GTIN | counted, not guessed | `items_unmatched` |
| same feed read twice | no new observations, no changes (unique `(market, product, observed_at)`) | `observations_inserted = 0` |
| older feed after a newer one | observations appended as history; current price untouched (`WHERE EXCLUDED.observed_at > current.observed_at`) | n/a |
| run still going when next fires | second run skipped | `status = skipped` (log + metric) |
| database down | job throws → Hangfire marks it failed (no retry); readiness fails | Hangfire dashboard, `/health/ready` |
| Redis down during invalidation | prices committed; cache entries expire by TTL (≤ 10 min); error logged; epoch guard still blocks writes of pre-change results computed after the bump | logs, Redis health `Degraded` |
| process dies after commit, before push | prices correct in DB; clients learn on next refetch or reconnect (pushes are best-effort by design) | n/a |
| scheduler stopped | watchdog cannot run either; markets go `unknown` on the next start | `/api/v1/markets` |

## Operating it

- Dashboard (local demo): `http://localhost:58280/hangfire` (read-only, loopback only).
- Pause a market: remove it from `Feeds:Markets` and restart. Its recurring job is removed, and its prices age
  normally until they expire.
- Force a run: "Trigger now" in the dashboard, or `IRecurringJobManager.Trigger("ingest-carsi")`.
- Simulate an outage: `curl -X POST "http://localhost:58282/admin/markets/carsi/outage?seconds=300"`.

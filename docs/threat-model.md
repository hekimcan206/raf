# Threat model

Scope: the API, the ingestion pipeline, the feed simulator and the Expo app as run by `docker compose up`. This is a
portfolio system with synthetic data. The model records which threats the design already handles and which ones are
left for a production deployment.

## Assets

| asset | why it matters |
|-------|----------------|
| Price history and current prices | the product's credibility: a manipulated price misleads every shopper comparing that basket |
| Feed health and run log | operators rely on it to judge price trustworthiness |
| Baskets | low sensitivity (no accounts, no personal data), but a basket id is a capability: whoever knows it can overwrite it |
| Database credentials | migrator (DDL) and app (DML) roles |

No personal data is processed. Baskets are anonymous and device-generated, and there are no accounts, names,
locations or payment data. All data is synthetic. KVKK (Turkish personal data protection law) obligations are
therefore minimal. If accounts or location-based "nearest branch" features are added, they would bring personal
data and need a separate assessment.

## Actors

- Shoppers (anonymous app users)
- Market feeds (external, semi-trusted: they can be wrong, late, malformed or hostile)
- Operators (dashboard, admin endpoints of the simulator)
- An attacker on the internet

## Threats (STRIDE) and mitigations

| # | threat | mitigation in this repo | residual / next step |
|---|--------|-------------------------|----------------------|
| S1 | Spoofed feed (DNS/MITM) injects prices | feeds are configured URLs; per-item plausibility rules (price range, future timestamps, size cross-check) | production: HTTPS with pinned hosts, signed feeds or mTLS per market |
| T1 | Rewriting price history | `price_observation` is append-only at the database level (`UPDATE/DELETE/TRUNCATE` revoked from the app role; tested) | backups / WAL archiving for the owner role |
| T2 | Malformed feed corrupts state | parsers collect per-item rejections; a document-level error fails the whole run; all-or-nothing transaction | n/a |
| T3 | XML external entities / entity expansion in a feed | `DtdProcessing.Prohibit`, `XmlResolver = null` (golden test with an XXE sample) | n/a |
| T4 | SQL injection via search or feed content | all SQL parameterised (Dapper/Npgsql); no dynamic SQL | n/a |
| T5 | Old feed replay rolls prices back | monotonic upsert (`observed_at` moves forward only); replay is a no-op for history | n/a |
| T6 | Basket overwrite by someone who knows the id | ids are random UUIDs generated on device; optional `If-Match` | accounts or signed basket tokens before baskets hold anything sensitive |
| R1 | Unattributable price change | every observation records `source` and `feed_run_id`; every run is logged with counts and errors | n/a |
| I1 | Information disclosure via errors / health | RFC 9457 problem details without stack traces; health endpoints return status and timing only | n/a |
| I2 | Hangfire dashboard exposes job data | disabled by default; demo enables it read-only on a loopback-bound port | production: authentication or not deployed |
| D1 | Oversized requests (huge baskets, many SignalR subscriptions) | ≤ 100 lines, quantity 1–99, ≤ 200 watched products per connection (hub throws) | production: rate limiting per client IP, request size limits at the proxy |
| D2 | Hung or slow feed blocks ingestion | 5 s timeout, no in-run retry, per-market advisory lock (other markets unaffected) | n/a |
| D3 | Redis outage | cache bypass with short timeouts; Redis not part of readiness (tested) | n/a |
| E1 | API compromise leads to schema change | app role has DML only (no DDL on `raf` or `hangfire`, no access to `raf_admin`; tested); containers run as non-root on chiseled images | read-only root filesystem, network policies |
| E2 | Simulator admin endpoints (force outages and prices) | the simulator exists only in the demo, on the compose network and a loopback port | never deployed outside a demo |

## Mobile client

- The app stores only the basket and cached API responses (AsyncStorage / localStorage). No secrets, tokens or
  personal data.
- On the web the API is same-origin (nginx proxy), so there is no CORS. The development CORS list exists only for the
  Expo dev server.

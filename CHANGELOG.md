# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/),
and the file is maintained by hand.

## [Unreleased]

## [0.1.0] - 2026-11-09

First public milestone of a project that is still in progress (see README "Status and roadmap").

### Added

- Basket comparison per market with unit-price normalisation (per kg / L / piece), substitutions within curated
  groups under an overbuy cap, and incomplete markets ranked after complete ones.
- Freshness per price line (fresh / aging / stale / expired) from the market's own `observed_at`, a money-weighted
  stale share per total, and expired prices excluded. Labels are computed at read time on the server and on the device.
- Ingestion of four simulated market feeds (JSON, Turkish CSV, XML, paged SKU-keyed JSON) through Hangfire recurring
  jobs on PostgreSQL. Ingestion is idempotent and monotonic, with feed health (healthy / degraded / down / unknown), a
  per-market advisory lock and all-or-nothing runs.
- Append-only price history enforced by database grants. A least-privilege application role, also for Hangfire's
  schema.
- Redis cache for comparisons, with a reverse product index for targeted invalidation and an epoch check-and-set
  against stale writes. It falls back to uncached operation when Redis is unavailable.
- SignalR hub that pushes price changes to the watchers of a product and feed health changes to everyone.
- Expo (React Native, TypeScript) app: offline basket, comparison cache persisted for 7 days, "as of" age badges,
  stale warnings, connectivity banners, and SignalR reconnect with re-watch and refetch.
- `docker compose up` demo with seed data, Playwright screenshots and GIF, and a latency benchmark (uncached vs Redis
  miss vs Redis hit).

[Unreleased]: https://github.com/hekimcan206/raf/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/hekimcan206/raf/releases/tag/v0.1.0

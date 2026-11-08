# ADR 0004: Hangfire recurring jobs on PostgreSQL for feed ingestion

- Status: accepted
- Date: 2026-11-08

## Context

Each market feed has to be polled on its own schedule. Every poll must be recorded, a slow or hung feed must not
block the others, two instances must not ingest the same market at the same time, and operators need to see what
ran. PostgreSQL is already the system of record.

## Options considered

- **`BackgroundService` + `PeriodicTimer`.** No dependencies, but scheduling, a run history, multi-instance
  coordination and a dashboard would all have to be built.
- **Quartz.NET.** Capable, but its ADO.NET job store adds 11 tables and more configuration than this needs.
- **Hangfire with Hangfire.PostgreSql (chosen).** Cron-based recurring jobs, a distributed scheduler (one instance
  enqueues each occurrence), job history and a dashboard, stored in the same database.

## Decision

- Hangfire 1.8 with `Hangfire.PostgreSql` 1.21, schema `hangfire`. Details are in the
  [job plan](../hangfire-job-plan.md).
- **Least privilege.** The migrator role installs Hangfire's schema (`PostgreSqlObjectsInstaller`) together with our
  migrations. The API role gets DML only (`PrepareSchemaIfNecessary = false`). Tests prove that the app role cannot
  create tables in `hangfire` and that a real job still runs.
- **No automatic retries for ingestion** (`[AutomaticRetry(Attempts = 0)]`), and no retries inside a run. The next
  scheduled run is the retry. Retries queued behind an outage would fire together when the feed recovers.
- **Expected failures do not fail jobs.** An unavailable feed is recorded in `raf.feed_run` and in the market's health
  state, and the job succeeds. Unexpected errors (database down, bugs) throw, so they surface as failed jobs.
- **Per-market mutual exclusion** uses a PostgreSQL session advisory lock taken by the ingestion service,
  `pg_try_advisory_lock(hashtext('raf:ingest:' || market))`. A run that finds the lock taken is *skipped*, not
  queued. `[DisableConcurrentExecution]` was rejected because it locks per method, which would serialise all markets
  behind one lock.
- **All-or-nothing runs.** Fetch every page, parse, match, then write observations, current prices, the run record and
  the health state in one transaction. A failure on page 3 writes nothing. A half-applied feed would mix two
  snapshots.

## Consequences

- One more schema in PostgreSQL, and Hangfire's polling load. `QueuePollInterval` is 2 s. That is negligible here,
  but should be revisited if the database ever becomes a bottleneck.
- The dashboard is enabled read-only in the local demo only (`Jobs:DashboardEnabled`, bound to loopback). It has no
  authentication.
- Recurring jobs are registered in code at startup (`AddOrUpdate`), so schedules are versioned with the code. Jobs for
  markets removed from configuration are deleted.

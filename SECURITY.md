# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within
5 working days; if the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Price history | Append-only at the database level: the API role has no UPDATE, DELETE or TRUNCATE on `price_observation`; observed_at only moves forward in `current_price` |
| Database access | The API runs as `raf_app` (DML only, also on Hangfire's schema; no DDL); migrations and the demo seed run in a one-shot process as `raf_migrator` |
| Feed input | Per-item plausibility checks, pack-size cross-check, XML with DTDs prohibited (no XXE), parameterised SQL only |
| Personal data | None processed: baskets are anonymous, identified by a device-generated UUID |
| Input limits | At most 100 basket lines, quantities 1–99, 200 watched products per SignalR connection |
| Containers | Chiseled .NET runtime images and unprivileged nginx, no shell or package manager in the .NET images, non-root |
| Secrets | None in the repository; compose defaults are for local development and can be overridden via `.env` |
| Supply chain | Centrally pinned package versions (NuGet and the app's `package-lock.json`), updated by hand; CI fails on known-vulnerable packages (`dotnet list package --vulnerable --include-transitive`, `npm audit --omit=dev --audit-level=high`); CodeQL `security-and-quality` on every push |

**Not included:** authentication and authorisation, rate limiting, TLS termination. The Hangfire dashboard and the simulator's admin endpoints are for the local demo only. This repository is a
demonstration with synthetic data and must not be exposed as-is.

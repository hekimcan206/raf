# Contributing

Thanks for taking the time to improve raf.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Node 22 or later for the Expo app in `mobile/`
- Docker (integration tests start PostgreSQL and Redis through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                          # must be warning-free: warnings are errors
   dotnet test                           # unit + integration tests
   dotnet format --verify-no-changes
   cd mobile && npm ci && npm run typecheck && npm test
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history
readable, make the hand-written CHANGELOG easy to assemble at release time, and show which version
bump a change needs:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one. Do not
edit the old ADR in place. Use the existing files as the format (MADR: context, options considered,
decision, consequences).

## Database migrations

Migrations are numbered SQL files in `src/Raf.Api/Persistence/Migrations/` (`NNNN_name.sql`), embedded in the
assembly and applied by `--migrate-only`. Never edit a migration that has been applied anywhere: the runner compares
checksums and refuses to start. Add a new, higher-numbered file instead.

## Feed formats

A market format is an `IFeedFormat` in `src/Raf.Core/Feeds`. Add a golden sample with its edge cases to
`tests/Raf.UnitTests/Feeds/Samples` and a writer in `src/Raf.Simulation` so the round-trip test covers it.

## Screenshots

After a visible UI change, regenerate the README images against a running stack:

```bash
docker compose up -d --build
sh tools/screenshots/run.sh
```

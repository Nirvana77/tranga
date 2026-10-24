# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Tranga is a single ASP.NET Core process (`API/`) that is both a REST API and a background worker host. It scrapes manga sites ("MangaConnectors"), downloads chapters as `.cbz` archives (optional ComicInfo.xml, jpeg compression / greyscale), fetches metadata (MyAnimeList via Jikan), triggers Komga/Kavita library scans, and sends notifications (Gotify, Ntfy, Pushover, generic webhooks). There is no frontend in this repo (see tranga-website).

## Commands

Requires the **.NET 10 SDK** (`API` and `Tests` target `net10.0`).

```sh
dotnet build Tranga.sln
dotnet test Tests/Tests.csproj
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UtilsTest.BuildUri_BuildsCorrectUri"   # single test
```

Tests use xunit v3 and currently only cover pure helpers (`Utils`, `RuntimeConfiguration`). There is no linter config.

Running locally: start Postgres with `docker compose -f docker-compose.local.yaml up -d`, then `dotnet run --project API` (the `http` profile in `API/Properties/launchSettings.json` sets `POSTGRES_HOST=127.0.0.1:5432`, `DOWNLOAD_LOCATION=./Manga`). Swagger is at `/swagger`. Full container build: `docker compose up --build` (the image bundles ungoogled-chromium for `ChromiumDownloadClient`).

Building regenerates `API/openapi/API_v2.json` (`OpenApiGenerateDocumentsOnBuild`); commit it when endpoints change.

### Migrations

Code-first EF Core with **four separate DbContexts sharing one Postgres DB**, each with its own migration folder. Migrations are applied automatically at startup in `Program.cs`. When changing any schema, create the migration for all four contexts with the same name:

```sh
./CreateMigrations.sh <MigrationName>   # runs `dotnet ef migrations add` for Manga, Library, Notifications, Actions
```

## Architecture

- **`Program.cs`**: DI setup, API versioning (everything is v2, routes are `v{v:apiVersion}/[controller]`), Swagger, CORS (`TRANGA_CORS__ALLOWED_ORIGIN`, `TRANGA_SWAGGER__ENABLED` read via `RuntimeConfiguration`), SignalR hub `DownloadProgressHub` at `/hubs/download-progress`, migrations, then hands off to `Tranga`.
- **`Tranga.cs`**: a static class that holds global state and the worker scheduler: the registered `MangaConnectors` and `MetadataFetchers` arrays, `TrangaSettings`, the singleton periodic workers, and `AddWorker`/`StartWorker`/`StopWorker`. `Tranga.ServiceProvider` is set from `Program.cs` so workers can create DI scopes.
- **Schema / DbContexts** (`Schema/`): `MangaContext` (manga, chapters, authors, tags, connector ids, file libraries, metadata), `LibraryContext` (Komga/Kavita connectors), `NotificationsContext`, `ActionsContext` (audit log of `ActionRecord` subtypes). All inherit `TrangaBaseContext<T>`; save via `context.Sync(token, reason: "...")`, which returns `(success, exceptionMessage)` rather than throwing. `DB-Layout.png` shows the schema.
- **Workers** (`Workers/`): subclass `BaseWorker` and implement `DoWorkInternal()`, which returns *follow-up workers* that the scheduler enqueues (e.g. retrieve chapters → download chapter workers). Workers can declare `dependsOn`. Workers that need a DB subclass `BaseWorkerWithContexts` and grab contexts in `SetContexts(IServiceScope)`; each run gets a fresh scope. Periodic workers implement `IPeriodic` (`Interval`) and are re-scheduled after each run unless they failed. Concurrency is capped by `TrangaSettings.MaxConcurrentWorkers`, and each run has a timeout (`WORKER_TIMEOUT`). Download progress is pushed to SignalR via `DownloadProgressReporter`.
- **MangaConnectors** (`MangaConnectors/`): one class per site, inheriting `MangaConnector` (search, get manga by URL/id, get chapters, get chapter image URLs). Each connector picks an `IDownloadClient` (`MangaDownloadClients/`: `HttpDownloadClient`, `FlareSolverrDownloadClient`, `ChromiumDownloadClient` via PuppeteerSharp), all going through the shared `RateLimitHandler`. `Global` is a meta-connector that fans out searches to all enabled connectors. Connectors are persisted in `MangaContext` with `Name` as the EF discriminator.
- **Controllers** (`Controllers/`): return DTOs from `Controllers/DTOs` and take request records from `Controllers/Requests`; they don't expose EF entities directly.
- **Config**: env vars are read in `Constants.cs` (see the README table). User-editable settings live in `TrangaSettings` and are persisted as JSON at `/usr/share/tranga-api/settings.json` on Linux (`%appdata%/tranga-api` on Windows), alongside the cover image cache and logs (log4net, `Log4Net.config.xml`).

### Adding a new site connector

All three steps are required:
1. Add a class in `API/MangaConnectors/` inheriting `MangaConnector`. Copy an existing one, e.g. `WeebCentral.cs`.
2. Add an instance to `Tranga.MangaConnectors` in `Tranga.cs`.
3. Add `.HasValue<NewConnector>("NewConnector")` to the `MangaConnector` discriminator in `MangaContext.OnModelCreating`, then run `./CreateMigrations.sh Add<NewConnector>`. (Removing a connector also needs a migration, as with `Remove-Mangapark`.)

## Code style (from CONTRIBUTING.md)

- Use explicit types, not `var`.
- Put the body of a braceless `if`/`for` on its own indented line, never on the same line.
- Break long ternaries / `??` chains across lines with leading operators.

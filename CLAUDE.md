# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Tranga is a single ASP.NET Core process (`API/`) that is both a REST API and a background worker host. It scrapes manga sites ("MangaConnectors"), downloads chapters as `.cbz` archives (optional ComicInfo.xml, jpeg compression / greyscale), fetches metadata (MyAnimeList via Jikan), triggers Komga/Kavita library scans, and sends notifications (Gotify, Ntfy, Pushover, generic webhooks). There is no frontend in this repo (see tranga-website).

## Branches

This checkout is the fork Nirvana77/tranga. `main` mirrors upstream C9Glax/tranga and must stay free of our own commits. All our work goes on `develop`, the fork's default branch. The fork's GitHub workflow `.github/workflows/sync-develop.yml` merges `main` into `develop` after every push to `main`. A daily CronJob in the k8s repo (`k8s/Tranga/prod/50-fork-sync.yaml`) fast-forwards `main` from upstream. Deploying to the cluster: `k8s/Tranga/build.sh` (builds `develop` by default).

### Running in the cluster

Namespace `tranga` on k3s-worker-1; the k8s repo's `Tranga/README.md` has the details.
- `k8s/Tranga/build.sh` saves a `pg_dumpall` to `~/tranga-backups`, then builds `develop` and swaps only the Deployment's image. Changes to env vars in `prod/30-tranga-api.yaml` need `kubectl set env` (or `kubectl apply`, which resets the image to upstream).
- FlareSolverr runs as `prod/33-flaresolverr.yaml` at `http://flaresolverr:8191`. Manhuaus needs it.
- To reach the API: `kubectl -n tranga port-forward svc/tranga-api 16531:6531`, then `http://127.0.0.1:16531/v2/...`.
- Useful endpoints:
  - `PATCH /v2/Settings/FlareSolverr/Url` with body `"http://flaresolverr:8191"`; `POST /v2/Settings/FlareSolverr/Test`.
  - Switch a manga to another source: `GET /v2/Search/<Connector>/<name>` (adds the link to the existing manga when the name matches), then `PATCH /v2/Manga/<key>/DownloadFrom/<Connector>/true` and `.../DownloadFrom/<OldConnector>/false`. `StartNewChapterDownloadsWorker` picks the chapters up within a minute.
- A manga that finds 0 chapters on MangaDex usually had them removed there (check `https://api.mangadex.org/manga/<id>/feed`), not a tranga bug. Link it to another connector.

## Commands

Requires the **.NET 10 SDK** (`API` and `Tests` target `net10.0`). With a local SDK:

```sh
dotnet build Tranga.sln
dotnet test Tests/Tests.csproj
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~UtilsTest.BuildUri_BuildsCorrectUri"   # single test
```

Tests use xunit v3 and currently only cover pure helpers (`Utils`, `RuntimeConfiguration`). There is no linter config.

Running locally: start Postgres with `docker compose -f docker-compose.local.yaml up -d`, then `dotnet run --project API` (the `http` profile in `API/Properties/launchSettings.json` sets `POSTGRES_HOST=127.0.0.1:5432`, `DOWNLOAD_LOCATION=./Manga`). Swagger is at `/swagger`. Full container build: `docker compose up --build` (the image bundles ungoogled-chromium for `ChromiumDownloadClient`).

Building regenerates `API/openapi/API_v2.json` (`OpenApiGenerateDocumentsOnBuild`); commit it when endpoints change.

### Docker-only workflow (the dev machine has no .NET 10 SDK)

The host only has the .NET 8 SDK, and the user wants the toolchain kept in Docker rather than installed. Run every `dotnet` command in `mcr.microsoft.com/dotnet/sdk:10.0` like this:

```sh
dn() {  # usage: dn <dotnet args...>   (run from the repo root)
  docker run --rm --network host -u "$(id -u):$(id -g)" \
    -e HOME=/home/dev -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    -e PATH=/usr/share/dotnet:/usr/bin:/bin:/home/dev/.dotnet/tools \
    -v ~/.cache/tranga-dotnet:/home/dev -v "$PWD":/src -v "${TRANGA_SETTINGS:-$HOME/.cache/tranga-settings}":/usr/share/tranga-api \
    ${DN_ENV:-} -w "${DN_WORKDIR:-/src}" mcr.microsoft.com/dotnet/sdk:10.0 "$@"
}
mkdir -p ~/.cache/tranga-dotnet ~/.cache/tranga-settings
dn dotnet build Tranga.sln
dn dotnet test Tests/Tests.csproj -p:OpenApiGenerateDocumentsOnBuild=false
dn sh -c 'dotnet tool list -g | grep -q dotnet-ef || dotnet tool install -g dotnet-ef --version 10.0.10; sh ./CreateMigrations.sh <Name>'
```

What each part is for:
- **Your user, not root:** `bin/`, `obj/` and migration files stay owned by the user.
- **`~/.cache/tranga-dotnet` as HOME:** keeps the NuGet cache and the `dotnet-ef` tool between runs.
- **Mount the repo at `/src`, always:** `obj/project.assets.json` records absolute paths, so mixing mount points forces restores and breaks builds.
- **Settings mount at `/usr/share/tranga-api`:** `TrangaSettings` hard-codes that directory on Linux and writes `settings.json` there on first use. Anything that touches `Tranga.*` (EF design-time, connector tests) fails without a writable directory there. It reads `FLARESOLVERR_URL` only when it first creates `settings.json`, so use a fresh directory when that value changes.
- **`--network host`:** lets the container reach a local FlareSolverr or Postgres on `127.0.0.1`.

Known noise:
- Every build prints an EF error that it can't connect to `tranga-pg:5432`. That's the OpenAPI document generation starting the app; the build still succeeds.
- A build on Linux rewrites the escaped `\r\n` inside description strings of `API/openapi/API_v2.json` as `\n` (the file itself uses LF). Run `git checkout API/openapi/API_v2.json` before committing unless endpoints or DTOs changed. If they did, keep the regenerated file but restore the escapes: `python3 -c 'p="API/openapi/API_v2.json";s=open(p).read().replace("\\n","\\r\\n").replace("\\r\\r\\n","\\r\\n");open(p,"w").write(s)'`, then check `git diff` only shows your change.

#### Testing a connector against the live site

There are no automated connector tests. To exercise one, write a throwaway console project in the scratchpad (never in the repo) that references the API project, then run it with `dn` and `DN_WORKDIR` pointing at it:

```xml
<!-- <scratch>/e2e/e2e.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <OpenApiGenerateDocumentsOnBuild>false</OpenApiGenerateDocumentsOnBuild>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="/src/API/API.csproj" /></ItemGroup>
</Project>
```

`Program.cs` creates the connector (`new Manhuaus()`) and calls `SearchManga`, `GetMangaFromUrl`, `GetChapters` and `DownloadImage`. `GetChapterImageUrls` is `internal` and there is no `InternalsVisibleTo`, so call it through reflection. To see connector logs, call `log4net.Config.BasicConfigurator.Configure()` and set the root level to `Warn`. Run:

```sh
DN_ENV="-v <scratch>/e2e:/e2e -e FLARESOLVERR_URL=http://127.0.0.1:18191" DN_WORKDIR=/e2e \
  TRANGA_SETTINGS=<scratch>/e2e-settings dn dotnet run
```

For Cloudflare-protected sites, start a local FlareSolverr first. Leave it running while you work and stop it with `docker stop fs-test` when done:

```sh
docker run -d --rm --name fs-test -p 127.0.0.1:18191:8191 ghcr.io/flaresolverr/flaresolverr:latest
```

To check whether a site is challenged: `curl -A "<browser UA>" -o /dev/null -w '%{http_code}' <url>` returns 403 with `cf-mitigated`/`server: cloudflare` headers. You can also POST `{"cmd":"request.get","url":"<url>"}` to `http://127.0.0.1:18191/v1` and inspect `solution.response`, `solution.cookies` and `solution.userAgent`.

### Migrations

Code-first EF Core with **four separate DbContexts sharing one Postgres DB**, each with its own migration folder. Migrations are applied automatically at startup in `Program.cs`. When changing any schema, create the migration for all four contexts with the same name:

```sh
./CreateMigrations.sh <MigrationName>   # runs `dotnet ef migrations add` for Manga, Library, Notifications, Actions
```

Upstream's `.gitignore` has an unanchored `Manga/` rule (meant for the download folder) that also ignores `API/Migrations/Manga/`. On `develop` it is anchored as `/Manga/`. If an upstream merge brings the unanchored rule back, new Manga migrations silently drop out of commits. After creating a migration, check `git status` shows all four `*_<Name>.cs` files.

## Architecture

- **`Program.cs`**: DI setup, API versioning (everything is v2, routes are `v{v:apiVersion}/[controller]`), Swagger, CORS (`TRANGA_CORS__ALLOWED_ORIGIN`, `TRANGA_SWAGGER__ENABLED` read via `RuntimeConfiguration`), SignalR hub `DownloadProgressHub` at `/hubs/download-progress`, migrations, then hands off to `Tranga`.
- **`Tranga.cs`**: a static class that holds global state and the worker scheduler: the registered `MangaConnectors` and `MetadataFetchers` arrays, `TrangaSettings`, the singleton periodic workers, and `AddWorker`/`StartWorker`/`StopWorker`. `Tranga.ServiceProvider` is set from `Program.cs` so workers can create DI scopes.
- **Schema / DbContexts** (`Schema/`): `MangaContext` (manga, chapters, authors, tags, connector ids, file libraries, metadata), `LibraryContext` (Komga/Kavita connectors), `NotificationsContext`, `ActionsContext` (audit log of `ActionRecord` subtypes). All inherit `TrangaBaseContext<T>`; save via `context.Sync(token, reason: "...")`, which returns `(success, exceptionMessage)` rather than throwing. `DB-Layout.png` shows the schema.
- **Workers** (`Workers/`): subclass `BaseWorker` and implement `DoWorkInternal()`, which returns *follow-up workers* that the scheduler enqueues (e.g. retrieve chapters → download chapter workers). Workers can declare `dependsOn`. Workers that need a DB subclass `BaseWorkerWithContexts` and grab contexts in `SetContexts(IServiceScope)`; each run gets a fresh scope. Periodic workers implement `IPeriodic` (`Interval`) and are re-scheduled after each run unless they failed. Concurrency is capped by `TrangaSettings.MaxConcurrentWorkers`, and each run has a timeout (`WORKER_TIMEOUT`). On `develop`, `AddWorker` doesn't block its caller when all slots are busy: one-off workers wait for a slot in the background (upstream's caller slept `WorkCycleTimeoutMs`, 20 s, per wait, which made a search with 43 results take 5 minutes while downloads ran). So a queued worker is in `GetKnownWorkers()` but not yet in `GetRunningWorkers()`; code that checks "is this already being done" (e.g. `StartNewChapterDownloadsWorker`) must look at the known workers. Download progress is pushed to SignalR via `DownloadProgressReporter`.
- **MangaConnectors** (`MangaConnectors/`): one class per site, inheriting `MangaConnector` (search, get manga by URL/id, get chapters, get chapter image URLs). Each connector picks an `IDownloadClient` from `MangaDownloadClients/`, all going through the shared `RateLimitHandler`:
  - `HttpDownloadClient` falls back to FlareSolverr on a Cloudflare 403. That fallback returns HTML as text, so it breaks images.
  - `FlareSolverrClearanceDownloadClient` solves the challenge once per site, then sends plain requests with the `cf_clearance` cookie and FlareSolverr's user agent, so images work too. It needs `FLARESOLVERR_URL`. Use it for sites that challenge everything (e.g. Manhuaus).
  - `FlareSolverrDownloadClient` sends every request through FlareSolverr.
  - `ChromiumDownloadClient` uses PuppeteerSharp. `Global` is a meta-connector that fans out searches to all enabled connectors; on `develop` only to those whose `SupportedLanguages` contain `TrangaSettings.DownloadLanguage` (so Italian Mangaworld is skipped when downloading English). Search results are saved as manga in the DB, and chapters are only fetched once a source is set to download. The connector-id DTO has a `Languages` field taken from the connector. Connectors are persisted in `MangaContext` with `Name` as the EF discriminator.
- **Controllers** (`Controllers/`): return DTOs from `Controllers/DTOs` and take request records from `Controllers/Requests`; they don't expose EF entities directly. A controller that creates another controller with `new` must pass `ControllerContext = ControllerContext`; otherwise `HttpContext` is null there (this broke `GET /v2/Manga/{id}/OnMangaConnector/{name}` upstream; fixed on `develop`).
- **Config**: env vars are read in `Constants.cs` (see the README table). User-editable settings live in `TrangaSettings` and are persisted as JSON at `/usr/share/tranga-api/settings.json` on Linux (`%appdata%/tranga-api` on Windows), alongside the cover image cache and logs (log4net, `Log4Net.config.xml`). `TrangaSettings` is a class on `develop`. Upstream it is a struct in a `static readonly` field, so its setters only changed a copy: `settings.json` was written but the running app kept the old values until a restart. Keep it a class if a merge from upstream conflicts there.

### Adding a new site connector

All three steps are required:
1. Add a class in `API/MangaConnectors/` inheriting `MangaConnector`. Copy an existing one, e.g. `WeebCentral.cs`.
2. Add an instance to `Tranga.MangaConnectors` in `Tranga.cs`.
3. Add `.HasValue<NewConnector>("NewConnector")` to the `MangaConnector` discriminator in `MangaContext.OnModelCreating`, then run `./CreateMigrations.sh Add<NewConnector>`. (Removing a connector also needs a migration, as with `Remove-Mangapark`.)

## Code style (from CONTRIBUTING.md)

- Use explicit types, not `var`.
- Put the body of a braceless `if`/`for` on its own indented line, never on the same line.
- Break long ternaries / `??` chains across lines with leading operators.

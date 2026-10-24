# Nirvana77/tranga: what this fork adds

This is a fork of [C9Glax/tranga](https://github.com/C9Glax/tranga). Everything in upstream's [README](README.md) still applies. This page lists what's different. The web UI has a matching fork, [Nirvana77/tranga-website](https://github.com/Nirvana77/tranga-website) (see its `FORK.md`).

## Branches

| Branch | Contains |
|---|---|
| `main` | Upstream, untouched. Never commit here. |
| `develop` | Upstream plus our changes. Default branch; build and deploy from here. |

Upstream changes flow in automatically:
1. A daily CronJob in the k8s repo (`Tranga/prod/50-fork-sync.yaml`) fast-forwards `main` from upstream. "Sync fork" on GitHub does the same.
2. Every push to `main` triggers the workflow `.github/workflows/sync-develop.yml`, which merges `main` into `develop`.
3. If the merge conflicts, the workflow fails and the CronJob sends a high-priority ntfy the next day. Merge by hand then.

Nothing is rebuilt automatically. Run `k8s/Tranga/build.sh` to deploy `develop`.

## Features

### Manhuaus connector

[manhuaus.com](https://manhuaus.com) as a new website (English).
- **Cloudflare:** the site puts every request, images included, behind a Cloudflare challenge. The new download client `FlareSolverrClearanceDownloadClient` gets a clearance cookie from FlareSolverr once per site, then downloads directly with that cookie and FlareSolverr's user agent.
- **Setup:** needs a FlareSolverr instance. Set its URL with `PATCH /v2/Settings/FlareSolverr/Url` (body `"http://…"`; the web UI has no field for it), or with `FLARESOLVERR_URL`, which is only read when `settings.json` is first created. In the cluster it runs as `http://flaresolverr:8191`.

### Languages
- **Chapter language:** every chapter records the language it was fetched in (the new `language` field on chapter connector IDs, migration `AddChapterLanguage`). A single-language website gives its own language; MangaDex gives the requested download language. The web UI shows it as a badge.
- **Website languages:** each website's languages are included as `languages` on connector IDs, so the UI can show that Mangaworld is Italian.
- **Global search** only searches websites that offer the download language. Searching one website directly still works for any language.
- **Download language** can be picked in the web UI's settings. The setting already existed in the API.

### Status of running work

`/v2/Worker` entries have a readable `description` (e.g. "Download Frozen Warrior Ch.12 from Manhuaus") and a `periodic` flag. The web UI's status bar is built on these.

## Fixes to upstream behaviour

| Problem upstream | Fix here |
|---|---|
| Settings changed through the API or UI (FlareSolverr URL, download language…) were saved but ignored until a restart. `TrangaSettings` was a struct in a `static readonly` field, so setters changed a copy. | `TrangaSettings` is a class. |
| "Search on another website" (`GET /v2/Manga/{id}/OnMangaConnector/{name}`) always returned 500. | The inner controller gets the request context. |
| Searches took minutes while chapters downloaded. Each result queues a cover download, and queuing blocked the request for 20 s whenever all worker slots were busy. | Queued workers wait in the background. Measured: MangaDex search for "The Reaper" went from 308 s to 5 s. |
| Upstream's `.gitignore` rule `Manga/` also ignored `API/Migrations/Manga/`. | Anchored as `/Manga/`. |

## Known behaviour (unchanged from upstream)

- **Search results are saved:** every search result is stored as a manga, even if you never download it. Settings → Maintenance → "Clean database" removes manga that aren't set to download.
- **0 chapters on MangaDex:** the site lists the series but its chapters were removed (often a takedown). Link the manga to another website instead.
- **Chapters appear late:** chapters are only fetched after a website is set to download for a manga, so a fresh search result shows 0 chapters.

## Development

See [CLAUDE.md](CLAUDE.md) for:
- the Docker-only toolchain (the dev machine has no .NET 10 SDK)
- testing connectors against live sites
- migrations
- deploying to the cluster

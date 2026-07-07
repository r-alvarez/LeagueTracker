# LeagueTracker

Personal League of Legends tracker: an ASP.NET Core (.NET 10) API + background
capture service with a React front end. Replaces the PowerShell tooling in
`D:\ScratchPad\League` with feature parity and adds timeline analytics.

LeagueTracker isn't endorsed by Riot Games. Built against the documented Riot
API only, for personal use.

## Running it

```powershell
cd src\LeagueTracker.Api
dotnet run                      # http://localhost:5170 (API + built SPA)
```

Front-end development (hot reload, proxies /api to the running API):

```powershell
cd src\leaguetracker-web
npm run dev                     # http://localhost:5173
npm run build                   # emits into LeagueTracker.Api\wwwroot
```

### API key

Resolution order: `Riot:ApiKey` (user-secrets) → `RIOT_API_KEY` env var →
first line of the file at `Riot:ApiKeyFile` (defaults to the PowerShell
tooling's `riot-api-key.txt`, re-read whenever the file changes, so a swapped
key needs no restart). Use a **personal** key from developer.riotgames.com —
dev keys expire daily. The key never reaches the browser; the SPA only talks
to this API.

### Install as a Windows service (always-on capture)

```powershell
dotnet publish src\LeagueTracker.Api -c Release -o C:\Services\LeagueTracker
sc.exe create LeagueTracker binPath= "C:\Services\LeagueTracker\LeagueTracker.Api.exe" start= auto
sc.exe start LeagueTracker
```

When publishing, set `Riot:DataDir` to an **absolute** path in the published
appsettings.json (the default `../../data` is relative to the source tree).

Once the service runs, remove the old scheduled-task watcher:
`D:\ScratchPad\League\Install-LeagueWatchTask.ps1 -Uninstall`.

## What it captures

- **Live poller** (background service): every `Riot:PollSeconds` it checks for
  finished games; each new one gets match + timeline + all 10 players' ranks
  (minutes after the game = ranks *at game time*) + your exact LP delta
  (trusted only when Riot's win/loss counter moved by exactly one).
- **History backfill** (`POST /api/sync/history?rankedTarget=N`): the
  exporter's bulk mode. Ranks here are *current*, not at-game-time.
- **Import** (`POST /api/import?path=...`): ingests the PowerShell tooling's
  export/live folders, the LP ledger and per-game LP. Works without an API key.
- **Timeline analytics**, recomputable any time from the raw files
  (`POST /api/analytics/reprocess`):
  - per-death convergence: enemies/allies within 2000 units at the death
    timestamp, positions interpolated between the 60s frames (estimates by
    nature) — the *true* collapse count, not the credited-kill list
  - full `victimDamageReceived` per death (source/spell/type/amount) +
    burst-vs-whittled classification (top-source share)
  - full position track for all 10 players, per 60s frame; per-game time in
    enemy half and average nearest-ally distance
  - kill/objective event timelines; deaths flagged when they fall within 90s
    of a friendly dragon/baron/herald/grubs (overstay signal)
  - loadouts (summs, keystone, items), item purchase/sell/undo timeline
  - Riot's per-game skillshot counters (`skillshotsHit`/`skillshotsDodged` —
    totals only; the API has no per-event skillshot data)

## Storage model

SQLite (`data/leaguetracker.db` at the repo root; never committed) is an
**index, not the truth**. The truth is
the raw `{ matchId, match, timeline }` files in `data/games` (same format as
the PowerShell exporter, interchangeable). Any schema/derivation change: delete
the db, restart, re-import — or hit the reprocess endpoint. The one exception
is LP snapshots, which only exist at capture time; they're mirrored to
`data/lp-history.csv` so a rebuild restores them via import.

## Endpoints

`GET /api/status` · `GET /api/matches` · `GET /api/matches/{id}` ·
`GET /api/lp/history` · `GET /api/lp/per-game` · `GET /api/analytics/summary` ·
`POST /api/sync/history` · `POST /api/import` · `POST /api/analytics/reprocess` ·
`GET /api/jobs/status` · CSV exports (PowerShell-tooling-compatible shapes) at
`GET /api/export/{matches,deaths,ranks,lp-history}.csv`

## Riot policy compliance

API key server-side only · documented endpoints only, paced by a limiter driven
by Riot's own rate-limit headers · displays official ranks/LP only (team
averages are labelled averages of official ranks — no MMR estimation) ·
analytics point at the tracked player's own play; other players appear as
neutral facts · free, personal, no Riot branding.

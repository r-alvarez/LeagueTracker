# LeagueTracker

A personal League of Legends tracker: an ASP.NET Core (.NET 10) API with a
background capture service and a React front end. It watches one player's
account (mine), records every finished game, and turns the data into analysis
of my own play — LP progression, team rank context, and death/positioning
analytics derived from match timelines.

LeagueTracker is a personal, non-commercial tool. It isn't endorsed by Riot
Games, uses only the documented Riot API, and tracks only my own account.

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
first line of the file at `Riot:ApiKeyFile`. The key file is re-read whenever
it changes on disk, so a refreshed key needs no restart. The key stays
server-side only — the SPA talks exclusively to this API, never to Riot.

Configure the tracked player and routing in `appsettings.json` under `Riot`
(`GameName`, `TagLine`, `Region`, `Platform`).

### Install as a Windows service (always-on capture)

```powershell
dotnet publish src\LeagueTracker.Api -c Release -o C:\Services\LeagueTracker
sc.exe create LeagueTracker binPath= "C:\Services\LeagueTracker\LeagueTracker.Api.exe" start= auto
sc.exe start LeagueTracker
```

When publishing, set `Riot:DataDir` and `Riot:ApiKeyFile` to **absolute**
paths in the published appsettings.json (the defaults are relative to the
source tree).

## What it captures

- **Live poller** (background service): every `Riot:PollSeconds` it checks for
  newly finished games; each one gets the full match + timeline, all 10
  players' League entries (captured minutes after the game = ranks *at game
  time*), and my exact LP delta — trusted only when Riot's own win/loss
  counter moved by exactly one between snapshots, otherwise left blank rather
  than guessed.
- **History backfill** (`POST /api/sync/history?rankedTarget=N`): bulk pull of
  recent ranked games. Ranks attached here are *current* ranks — the API has
  no rank-at-game-time endpoint, so only live capture gets that exactly.
- **Import** (`POST /api/import?path=...`): ingests folders of previously
  exported raw game files (`{ matchId, match, timeline }` JSON) plus an LP
  ledger CSV, so history collected by earlier tooling carries over. Works
  without an API key.
- **Timeline analytics**, recomputable any time from the raw files
  (`POST /api/analytics/reprocess`):
  - per-death convergence: enemies/allies within 2000 units at the death
    timestamp, positions interpolated between the 60s timeline frames
    (estimates by nature) — the *true* collapse count, not just who got kill
    credit
  - full `victimDamageReceived` per death (source/spell/type/amount) with
    burst-vs-whittled classification (top-source damage share)
  - the full position track for all 10 players per frame; per-game time in
    enemy half and average nearest-ally distance
  - kill and objective event timelines; deaths flagged when they fall within
    90s of a friendly dragon/baron/herald/grubs (overstay signal)
  - loadouts (summoner spells, keystone, items) and my item
    purchase/sell/undo timeline
  - Riot's per-game skillshot counters (`skillshotsHit`/`skillshotsDodged` —
    totals only; the API has no per-event skillshot data)

## Storage model

SQLite (`data/leaguetracker.db`; never committed) is an **index, not the
truth**. The truth is the raw `{ matchId, match, timeline }` files in
`data/games`. Any schema or derivation change: delete the db and re-import, or
hit the reprocess endpoint — no Riot API calls needed. The one exception is LP
snapshots, which only exist at capture time; they're mirrored to
`data/lp-history.csv` so a rebuild restores them via import.

## Endpoints

`GET /api/status` · `GET /api/matches` · `GET /api/matches/{id}` ·
`GET /api/lp/history` · `GET /api/lp/per-game` · `GET /api/analytics/summary` ·
`POST /api/sync/history` · `POST /api/import` · `POST /api/analytics/reprocess` ·
`GET /api/jobs/status` · CSV exports at
`GET /api/export/{matches,deaths,ranks,lp-history}.csv` and an
everything-bundle at `GET /api/export/all.zip`

## Riot policy compliance

API key server-side only · documented endpoints only, paced by a limiter
driven by Riot's own rate-limit response headers · displays official
ranks/LP only (team averages are labelled averages of official ranks — no MMR
estimation) · analytics point at the tracked player's own play; other players
appear as neutral facts · free, personal, no Riot branding.

# LeagueTracker

A League of Legends tracker for a small circle of players: an ASP.NET Core
(.NET 10) API with background capture, a React front end, and an optional
Windows agent that records games on the player's PC. It follows every tracked
account, records each finished game, and turns the data into analysis of that
player's own play - LP progression, team rank context, and death/positioning
analytics derived from match timelines.

LeagueTracker is a personal, non-commercial tool. It isn't endorsed by Riot
Games and uses only the documented Riot API.

## Who sees what

One process serves many tracked accounts at `/{region}/{Name-TAG}/...`
(`/api/a/{region}/{Name-TAG}/...` underneath). Identity is the app's own:

- **Users** sign in through an OIDC provider (Auth0 today; the app keeps its
  own user rows and can link a second provider later). Sign-ups are
  invite-only; anonymous visitors see a sign-in wall until `Auth:PublicReads`
  is turned on.
- **Owners** are users who proved a Riot account is theirs (Data & sync →
  *Is this your Riot account?*: set the profile icon the page names, press
  Verify) or were assigned it by an admin. Only the owner runs syncs and
  imports, manages machines, downloads exports, and decides what visitors see
  (recordings shared or not, rank/LP shown or hidden).
- **Agents** are machines with a key bound to one user: a *recorder* on the
  owner's gaming PC uploads that owner's VODs; a *renderer* (admin-admitted)
  serves everyone's replay-render queue. A new machine joins with a 15-minute
  join code from the owner's Data page and waits for one Approve click.
- **Admins** (`Auth:Admins`, comma-separated emails) see and manage every
  account, machine and user - the *People* card on any Data page.

Every endpoint names its policy (Read, MediaRead, Owner, AgentRecorder,
AgentRender, RenderRead); the SPA only hides what the server would refuse.
Users, accounts and agent keys live in `data/registry.db`; each account keeps
its own folder and SQLite. The full rationale is in
`decisions/auth-identity-model.md`.

## Running it

```powershell
cd src\leaguetracker-web && npm install && npm run build   # emits into LeagueTracker.Api\wwwroot
cd ..\LeagueTracker.Api
dotnet run --no-launch-profile -- --urls http://localhost:5399 --environment Development
```

Development enables `/api/auth/dev-login?email=you@example.com&name=You&admin=true&returnUrl=/`
(a persistent cookie session with no provider) so the owner and visitor views
can be reviewed side by side in two browser profiles. A real provider needs
`Auth:Oidc:Authority`, `ClientId` and `ClientSecret` (user-secrets locally,
stack env in Docker) and `http://localhost:5399/auth/callback` allowed on the
provider's application; then `/auth/login`.

Or the whole stack in Docker (app + a Caddy reverse proxy for a friendly
hostname):

```powershell
docker compose up -d            # http://leaguetracker.localhost (and :5170)
```

Add `127.0.0.1  leaguetracker.localhost` to your hosts file so the name
resolves for every client (browsers already resolve `*.localhost` to loopback).
The production stack (`deploy/truenas/compose.yml`) maps `AUTH0_*`,
`AUTH_ADMINS` and `OWNER_*` environment variables onto the settings above;
none of them live in the repo.

Front-end development (hot reload, proxies /api to the running API):

```powershell
cd src\leaguetracker-web
npm run dev                     # http://localhost:5173
```

### API key

Resolution order: `Riot:ApiKey` (user-secrets) → `RIOT_API_KEY` env var →
first line of the file at `Riot:ApiKeyFile` (`data/riot-api-key.txt` by
default). The key file is re-read whenever it changes on disk, so a refreshed
key needs no restart. The key stays server-side only - the SPA talks
exclusively to this API, never to Riot.

Tracked accounts come from `Accounts:List` in configuration (seeded into the
registry on boot; `Owner` sets the owner's email) or from the *Add account*
box on the site (any signed-in user, rate-limited; the account starts
unowned until claimed).

## What it captures

- **Live poller** (background service): every `Riot:PollSeconds` it checks for
  newly finished games; each one gets the full match + timeline, all 10
  players' League entries (captured minutes after the game = ranks *at game
  time*), and the player's exact LP delta — trusted only when Riot's own win/loss
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
  - loadouts (summoner spells, keystone, items) and the player's item
    purchase/sell/undo timeline
  - Riot's per-game skillshot counters (`skillshotsHit`/`skillshotsDodged` —
    totals only; the API has no per-event skillshot data)
- **Gameplans** (`/gameplans`): per-champion reference points — the sheet a
  coach hands you, one sentence per point, grouped by lane / mid / late — that
  every game of that champion is scored against on its match page. Every
  point carries one of thirteen rules read from the timeline (a fight with the
  jungler in the window after a level, early arrival at contested neutrals,
  isolated picks, an item or level by a clock, share of fights beside the
  jungler, fights joined with numbers after moving, duels taken, farm rate
  between checkpoints, wards before 10:00, not getting caught alone,
  outnumbered deaths in early skirmishes);
  what the timeline cannot see is not on the sheet. Each rule declines with
  *n/a* when the game gave no chance and *pending* until a reprocess fills
  the level clock; thresholds were calibrated on the local history
  (`decisions/feat-gameplans.md`). Plans are files under `data/gameplans`
  (irreplaceable, so never db-only); the rules run at read time, so editing a
  plan never needs a reprocess. The tab's Export / Import move plans between
  instances as JSON, and `gameplans.json` rides in `export/all.zip` and is
  restored by the folder import.

## CI / CD

- **`ci.yml`** - every push and PR: API build + xUnit tests (`tests/`), web
  lint + build, agent build on Windows, and the agent's ScreenRecorderLib
  (upstream + HDR tone-map patch, `deploy/screenrecorderlib`) built on
  windows-2022, cached on its inputs' hash and uploaded as an artifact. On
  main it also publishes the API image to `ghcr.io/r-alvarez/leaguetracker`
  (`sha-<commit>`, `main`, `latest`) with a build-provenance attestation and
  a Trivy scan into the Security tab. Nothing in it deploys.
- **Deploy** is GitOps: Portainer polls the repo and rebuilds `main` from
  `deploy/truenas/compose.yml`; the GHCR image is the rollback path (see the
  comment on the `leaguetracker` service).
- **`agent-release.yml`** - runs when `ci` is green on main, builds the exact
  commit ci tested, and releases only if agent files changed since the last
  `agent-*` tag: GitHub Release `agent-<version>` (zip + Setup.exe +
  SHA256SUMS + attestation), which the trackers mirror and every agent
  self-updates from. The zip must carry the ScreenRecorderLib.dll ci built for
  that commit (hash-checked) and `THIRD-PARTY-NOTICES.md`. Inputs are pinned:
  actions by commit, ffmpeg by version + SHA256, Inno Setup by version,
  ScreenRecorderLib by upstream commit. `workflow_dispatch` re-releases the
  latest green main.
- **`codeql.yml`** weekly + on main; **Dependabot** bumps actions, NuGet, npm
  and base images weekly in grouped PRs. `SECURITY.md` says how to report.

## Storage model

PostgreSQL (one database; the registry in its own schema, every tracked
account in an `acct_<id>` schema) is an **index, not the truth**. The truth is
the raw `{ matchId, match, timeline }` files in `data/games`. Any derivation
change: hit the reprocess endpoint, or drop the account's schema and re-import
the folder — no Riot API calls needed. Schema changes are EF migrations
(`dotnet ef migrations add <Name> --context LeagueDbContext --output-dir
Data/Migrations`, or `RegistryDbContext` / `Registry/Migrations`), applied to
every schema at boot. The exceptions are what only exists because someone was
there: LP snapshots (mirrored to `data/lp-history.csv` so a rebuild restores
them via import), the ranks captured at game time, and gameplans
(`data/gameplans/*.json`), which are files from the start and never in the db.

A host run needs the database from `docker compose up -d postgres`
(localhost:5432, credentials in `appsettings.json`); the tests start their own
in Testcontainers. A data folder from the SQLite era is brought across on the
first boot: each `leaguetracker.db` and `registry.db` is copied row for row
into its schema, verified against the file, and kept as `*.imported` — see
`docs/operate.md`.

## Endpoints

Per account, under `/api/a/{region}/{Name-TAG}`: `GET status` · `GET matches` ·
`GET matches/{id}` · `GET lp/history` · `GET lp/per-game` ·
`GET analytics/summary` · `POST sync/history` · `POST import` ·
`POST analytics/reprocess` · `GET jobs/status` · CSV exports at
`GET export/{matches,deaths,ranks,lp-history}.csv` and an everything-bundle at
`GET export/all.zip`. Reads need a signed-in session (or `Auth:PublicReads`);
the POSTs and exports need the owner; cookie-authenticated writes must carry
`X-Requested-With: LeagueTracker`.

Global: `GET /api/accounts` · `/api/auth/*` (login, logout, me) · `/api/me/*`
(your accounts, machines, join codes, claims) · `/api/admin/*` ·
`/api/agent/*` (enrol, heartbeat, releases; agent-key scheme).

## Riot policy compliance

API key server-side only · documented endpoints only, paced by a limiter
driven by Riot's own rate-limit response headers · displays official
ranks/LP only (team averages are labelled averages of official ranks — no MMR
estimation) · analytics point at the tracked player's own play; other players
appear as neutral facts · free, personal, no Riot branding.

# Operating the tracker

Where every piece of state lives, what can and cannot be rebuilt, how to back
it up and put it back, and what a push actually does. Written 2026-08-26 for
the TrueNAS deployment (`deploy/truenas/compose.yml`); a host run keeps the
same layout under the repo's `data/` folder.

## 1. Where the state is

The container sees one volume, `/data` = `/mnt/MediaPool/apps/leaguetracker`.

| Path | What it holds | Rebuildable? |
| --- | --- | --- |
| `postgres/` | The PostgreSQL cluster (the `postgres` service's volume, `/var/lib/postgresql` inside). One database, `leaguetracker`: the `registry` schema holds users and their Auth0 logins, tracked accounts (surrogate id, puuid, owner, settings, folder), agent keys, join codes, ownership claims; one `acct_<id>` schema per account holds that account's index (below) | The registry: **no**, it must never be lost. The account schemas: mostly — see §3 |
| `backups/` | `leaguetracker-<utc stamp>.dump`: a nightly `pg_dump` of the whole database by the `pg-backup` service, 14 kept | Yes, every night; the copy that restores on any host |
| `registry.db.imported`, `<account>/leaguetracker.db.imported` (+ `-wal`, `-shm`) | The SQLite era's files, kept after their verified one-time import into the schemas (§6) | Disposable once a dump exists |
| `keys/` | ASP.NET Data Protection keys: session cookies are signed with them | No, but losing them only signs everyone out. As sensitive as a session cookie |
| `main/riot-api-key.txt` | The Riot API key (`Riot__ApiKeyFile`) | Re-issue at developer.riotgames.com |
| `agent-releases/` | Agent builds mirrored from GitHub Releases (`Agent__SyncReleases`) | Yes, automatically |
| `agent-logs/<key id>/` | Logs agents shipped on "sendlog" | Disposable |
| `accounts.json.imported`, `agents.json.imported` | The pre-registry files, kept after their one-time import (bd99853) | Disposable once `registry.db` is backed up |
| `<account>/` | One folder per tracked account: `main`, `alt`, `ben` for the configured three, `<12-hex id>` for accounts added on the site | |
| `<account>/games/<matchId>.json` | The raw match + timeline as Riot returned it — the source of every analytical number | Only while Riot still serves the match (about two years): `POST .../sync/history` re-fetches |
| `<account>/lp-history.csv` | Every LP snapshot ever taken, mirrored as it is taken | **No.** A snapshot exists only at capture time |
| schema `acct_<id>` (in the cluster) | The index over `games/`: matches, participants, deaths, positions, LP snapshots, per-game LP attribution, rank-at-game-time context, `KnownMatches` (the poller's baseline), `KeyValues` (puuid cache). `<id>` is the account's registry id (`GET /api/accounts`) | Mostly — see §3 |
| `<account>/replays/<matchId>.rofl` | Riot replay files, downloadable only for the last ~5 games | **No** |
| `<account>/vods/<matchId>/` | The agent's upload: `youtube.txt` (the only pointer to the channel video), review sidecar, the mp4 when `UploadVods` is on | No — the agent prunes its copy once published |
| `<account>/clips/<matchId>/` | Rendered clips and their plan | Yes, by the renderer, while the `.rofl` exists |
| `<account>/fullgames/` | Full-game renders (kept ones; the rest expire after `FullGameRetentionDays`) | Same |

Nothing else is persistent. Render leases, live-game state, agent
heartbeats and one-shot agent commands are in memory and rebuild
themselves within a poll.

## 2. Backing up

Snapshot the ZFS dataset (`/mnt/MediaPool/apps/leaguetracker`) on a
schedule and replicate it off the pool. The snapshot is atomic, so the
`postgres/` folder in it is what the cluster would look like after a power
cut: PostgreSQL replays its own WAL on the next start and comes up
consistent. That is a whole-folder restore only (same major version, same
paths); the portable copy is the nightly dump in `backups/`, which
`pg_restore` puts back on any host. Never copy `postgres/` file by file
while the cluster runs.

What matters most, in order: the newest `backups/*.dump` (or `postgres/`),
every `lp-history.csv`, every `games/`, `vods/` (the YouTube links),
`replays/`, `keys/`. What can be left out of an off-site copy:
`agent-releases/`, `agent-logs/`, `clips/`, `fullgames/`, the `*.imported`
files once a dump exists.

Also not on the NAS: the Portainer stack environment (Auth0 client and
management secrets, `YT_*` tokens, `OWNER_*` and `ACCOUNT_*` values,
`PC_MAC`/`UNIFI_*`), the Auth0 tenant itself, and the shared YouTube
channel's OAuth grant (`deploy/youtube-auth.ps1` mints a new one).

## 3. Restoring

**The whole folder from a snapshot.** Roll the dataset back or copy it
into place, redeploy. Ids, folders, schemas and keys all match; nothing
else to do.

**The database from a dump.** Stop the `leaguetracker` service (the
cluster keeps running), then from the NAS:

    docker exec -i leaguetracker-postgres pg_restore -U leaguetracker -d leaguetracker --clean --if-exists < /mnt/MediaPool/apps/leaguetracker/backups/leaguetracker-<stamp>.dump

Start the service again. Every schema comes back as it was at the dump;
games ingested since are in `games/` and `POST .../sync/history` (or the
poller's next pass) re-indexes the newest ones.

**One account's schema, when `games/` and `lp-history.csv` survive.**
Exercised 2026-08-26 on a scratch instance (SQLite then; the import path
is the same) with a copy of the alt account holding only those two things:

1. Stop the stack, drop the schema (`docker exec -it leaguetracker-postgres
   psql -U leaguetracker -c 'DROP SCHEMA "acct_<id>" CASCADE'`), start it.
   The schema is recreated by the migrations; the poller's first pass only
   baselines the 20 newest match ids.
2. As the account's owner (or an admin), `POST
   /api/a/{region}/{slug}/import?path=/data/<account>` with the SPA's
   `X-Requested-With: LeagueTracker` header — the Data page's Import box
   does exactly this. The folder must be inside the account's own folder
   (or an `/imports` mount, which no compose defines); a folder holding a
   `games/` subfolder or the game files directly both work. Watch
   `GET .../jobs/status`.
3. Result on 70 games: `done - 68 games imported, 2 already present, 0
   failed, 39 LP snapshots, 0 LP deltas applied`; the 2 were the newest
   games the poller had re-fetched from Riot meanwhile. Every game, every
   LP snapshot and the ranked page came back; per-game LP deltas were
   re-attributed from the ledger for 11 ranked games (one game per
   snapshot interval is the rule, the rest stay unattributed). **Not**
   rebuilt: the lobby-rank context per game (the import runs without rank
   lookups, and historic ranks would be today's anyway), and any
   `KnownMatches` baseline older than the games on disk.
4. Expect a few `UNIQUE constraint failed: KnownMatches.Id` errors in the
   log while the import runs: the poller and the import race for the same
   newest games; the loser logs and retries next pass, the game is there
   once. Harmless, but do the import right after the restart rather than
   during a busy evening.

**The registry schema lost, no dump.** Nothing rebuilds it. On boot the
configured accounts (`Accounts__List__N`) are re-registered under **new
ids**, so they get new, empty `acct_<new id>` schemas while their data sits
in the old ones: adopt each with `ALTER SCHEMA "acct_<old id>" RENAME TO
"acct_<new id>"` (drop the empty one first) and restart. Their folders
(`DataDir`) are found as before. Site-added accounts must be added again
(their folders are named by the old id — set `Accounts__List__N__DataDir`
to point at one to adopt it, then rename its schema the same way). People
must be invited again (Auth0 still knows them; the invite just recreates
the row), every machine re-enrols (its `agent.key` is still on the PC, but
the server record is gone, so the owner mints a join code and approves
again), and the per-agent `Agent__Profiles__<key id>__*` blocks need the
new ids.

## 4. What a push does

- Portainer polls this repository every two minutes and **builds the
  image from source on the NAS** (`build`, `pull_policy: build`). Pushing
  to `main` is the deploy, whether or not CI is green. The stack now has
  three built or pulled services (`leaguetracker`, `postgres`,
  `pg-backup`) plus the waker; the app waits for the database's
  healthcheck before it starts.
- CI (`.github/workflows/ci.yml`) runs the API tests, the web lint and
  build, the agent build and ScreenRecorderLib, and on a green `main`
  publishes `ghcr.io/r-alvarez/leaguetracker:<sha>` with a build
  attestation — the immutable copy. To roll back without a rebuild:
  comment out `build`/`pull_policy`, set `image:
  ghcr.io/r-alvarez/leaguetracker:sha-<full sha>`, push; undo when `main`
  is fixed (Portainer's "re-pull image" must be on for that path).
- `GET /api/version` identifies what is running: informational version,
  image build time, process start.
- The GitHub ruleset "Main" (id 20981187, created 2026-08-18: no
  deletion, no force-push, the four CI checks, PR required) **targets no
  branch** as of 2026-08-26 — its include list is empty — so nothing is
  enforced on `main` today. Adding `~DEFAULT_BRANCH` to it turns every
  merge into a PR with green checks, for everyone; there are no bypass
  actors.
- Agent builds: `deploy/publish-agent.ps1` publishes a GitHub Release
  tagged `agent-<version>` (`agent-release.yml` gates and attests it); the
  tracker mirrors it into `/data/agent-releases`, and every agent updates
  itself from the tracker it talks to. A recorder stuck at "finalizing"
  needs one manual restart to pick up an update.

## 5. Stack environment (Portainer, never committed)

| Variable | Used for |
| --- | --- |
| `AUTH0_ISSUER`, `AUTH0_CLIENT_ID`, `AUTH0_CLIENT_SECRET` | The OIDC client |
| `AUTH0_MGMT_CLIENT_ID`, `AUTH0_MGMT_CLIENT_SECRET` | Invites (Management API) |
| `AUTH_ADMINS` | Admin emails, rows made at boot |
| `OWNER_RUBEN`, `OWNER_BEN` | Owner email per configured account |
| `ACCOUNT_2_GAMENAME`, `ACCOUNT_2_TAGLINE`, `ACCOUNT_2_DISPLAYNAME` | A friend's Riot ID and name — theirs, not the public repo's. All three unset: the entry is skipped and the registry's copy is used |
| `YT_CLIENT_ID`, `YT_CLIENT_SECRET`, `YT_REFRESH_TOKEN` | The shared YouTube channel grant handed to agents |
| `YT_BEN_CLIENT_ID`, `YT_BEN_CLIENT_SECRET`, `YT_BEN_REFRESH_TOKEN` | One agent's own Google project (keyed by its key id in the compose) |
| `POSTGRES_PASSWORD` | The database password: the `postgres` service sets it, the app and `pg-backup` connect with it. Must exist before the first deploy of the PostgreSQL build - the compose refuses to start without it |
| `PC_MAC`, `WOL_BROADCAST`, `UNIFI_URL`, `UNIFI_USER`, `UNIFI_PASS` | The waker |

## 6. Moving off SQLite (the first boot of the PostgreSQL build)

Before the push: set `POSTGRES_PASSWORD` in the stack environment, take a
ZFS snapshot of the dataset (the rollback point), and create the dump
folder with the owner the `pg-backup` service runs as - Docker would create
it as root, which 568 cannot write to:

    mkdir -p /mnt/MediaPool/apps/leaguetracker/backups
    chown 568:568 /mnt/MediaPool/apps/leaguetracker/backups

(`postgres/` needs nothing: that image starts as root and takes ownership
of its own folder.)

Then nothing to do by hand. On boot the app migrates the `registry` schema and
every account's `acct_<id>` schema, then for each schema that is still
empty and has the SQLite era's file beside it (`/data/registry.db`,
`<account>/leaguetracker.db`) it copies that file row for row — ids
included — and proves it: every table in the file must be one the build
knows and every column a mapped one (an unknown one fails the import
rather than being dropped), and both sides are re-read, canonicalised and
hashed per table. Counts and hashes equal means the copy commits and the
file is renamed `*.imported` (with its `-wal`/`-shm`); anything else rolls
the whole schema back to empty and leaves the file untouched. The log says
which:

    registry.db: imported and verified - 11 rows: Accounts 1, ... Users 4, ...
    /data/main/leaguetracker.db: imported and verified - 252419 rows: Matches 527, ...

A registry import that fails stops the boot (an empty registry would mint
new ids and orphan every schema); an account import that fails leaves that
account 503 with the reason, retried every 60 s, the others serving. Fix
the cause and restart; the file has not changed. The one accepted
difference: timestamps keep microseconds (PostgreSQL's precision) where
SQLite kept 100 ns ticks - Riot's are millisecond anyway.

A SQLite file next to a schema that already holds data is left alone and
logged (an earlier import whose rename failed, or a hand copy): rename it
`.imported` yourself once you are sure. The `*.imported` files are the
rollback until the first dump exists: the previous image reads them as
they were.

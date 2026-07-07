# Decisions — LeagueTracker (.NET + React)

## 2026-07-06 — Initial build

**SQLite as a rebuildable index; raw game JSON on disk as the source of truth.**
Every derivation (deaths, positions, objectives, damage, loadouts) recomputes from
`Data/games/*.json` via `POST /api/analytics/reprocess` or delete-db + re-import.
Chosen over EF migrations for a single-user tool: schema churn is free and new
metrics apply to history without touching the Riot API. The exception is LP
snapshots (capture-time-only data) — mirrored to `Data/lp-history.csv` so a
rebuild restores them. Trade-off accepted: db deletes are routine, so nothing
irreplaceable may ever live only in the db.

**Raw per-game file format kept identical to the PowerShell exporter**
(`{matchId, match, timeline}`) so both tools' game folders interchange, and the
existing 300+ exported games imported without re-downloading.

**Rate limiting as a DelegatingHandler + singleton limiter state** — a port of the
PowerShell header-driven sliding-window limiter. Handler clones requests (an
HttpRequestMessage can't be resent) and owns the 429 retry loop. Key resolution
(config → env → key file with change detection) lives in a provider so an expired
key can be swapped on disk without restarting the service.

**Import works keyless:** the tracked player's puuid is inferred as the single
puuid present in every exported game (metadata.participants intersection),
falling back to account-v1 when a key exists. Ambiguity (a permanent duo) aborts
with a clear message rather than guessing.

**LP attribution rules identical to the watcher:** delta trusted only when Riot's
own win+loss counter moved by exactly one between snapshots; unattributable gaps
stay blank rather than guessed. Poller skips the settle-wait when the previous
snapshot already postdates the game.

**Timeline analytics (per the coaching spec):** true collapse count = enemies
within 2000 units of the death position, all positions linearly interpolated
between the two bounding 60s frames (the kill event's own position is exact);
full victimDamageReceived stored per instance with top-source share separating
burst (≥0.7) from whittled; full 10-player position track persisted per frame;
BUILDING_KILL/ELITE_MONSTER_KILL sequence stored, deaths flagged within 90s of a
friendly epic objective (overstay). Skillshots: Riot's challenges counters only —
the API has no per-event skillshot data. Vision: WARD_PLACED has no coordinates,
so "did I have vision" is not reconstructable; the position track is the
documented workaround. Dashboards deliberately lead with collapse/contest
metrics, not KDA cosmetics (explicit request).

**BUILDING_KILL.teamId is the team that LOST the building** — inverted for
`ByMyTeam`. Easy to get wrong; verified against real games.

**`True` avoided as a column name** (DeathDamage.TrueDamage): EF Core 10 + SQLite
generated a query with an unquoted/broken reference (`no such column: s.True`).

## 2026-07-06 — UI parity with League Coach + repo hygiene

**Adopted League Coach's dark theme verbatim** (bg #0e1116 / panel #161b22 /
accent #4f9cf9 / win #3fb950 / loss #f0556a) as a dark-only theme, replacing the
light/dark dual palette — the user preferred the coach colours. The accent failed
the chart lightness band on the panel surface, so chart marks use #3d8ef3 (same
hue, one step darker, validator-passing); the lighter accent is UI chrome only.
Champion icons: coach's DataDragon hook copied as-is (versions.json → latest,
name+id keys, monogram fallback so a dead CDN never breaks rows).

**Export all = /api/export/all.zip** (four CSVs + summary.json, built in-memory
via ZipArchive). CSV builders extracted to `Reports` so the zip and individual
endpoints share one code path.

**Runtime data moved to repo-root `data/`** (DataDir `../../data`, gitignored) —
it previously landed in `src/LeagueTracker.Api/Data` next to the entity classes
and would have been committed. Published-service installs must set an absolute
DataDir. Repo-local git identity uses the GitHub noreply address for the user's
personal account (r-alvarez), keeping work email off personal commits.

## 2026-07-07 — Docker hosting + coach-parity analytics

**Docker replaces the Windows-service plan.** Multi-stage image; compose mounts
repo-root `data/` at `/data` (db, raw games, LP csv, key file at
`/data/riot-api-key.txt`), `restart: unless-stopped`. Two gotchas burned in:
appsettings.json `Urls` outranks ASPNETCORE_URLS (fixed with an unprefixed
`Urls` env var), and Windows-generated package-lock.json lacks linux/wasm
optional deps so the image uses `npm install`, not `npm ci`. A gitignored
docker-compose.override.yml mounts `../League` read-only at `/imports` for
in-container imports (re-pointing RawPath to container paths — Windows paths in
the db are unreadable from Linux).

**Coach metric definitions ported verbatim, validated to exact equality** against
the League Coach dashboard on the same 302 games (record, CS@10 69.3, lane
gold@10 −100, lane-state buckets 59/148/83, top death zone Mid-blue 336 = 23%):
MapZones classifier copied as-is; follow-in = most recent ally death ≤15s before
mine within 2500 units of THEIR death spot, pure-loss when no enemy fell from
trigger to +10s; lane diffs read from the minute-frames vs the same-role enemy;
lane-state buckets ±500 gold. `/api/stats?days|lastGames` is the single dashboard
aggregate (tiles, observations, follow-in context, series, champion/role splits,
LP deltas). Phase DPM (0-10/10-20/20+) from timeline damageStats cumulative.

**No composite 0-100 scores** (the DPM-Lens style radar was considered and
rejected): arbitrary weightings invite score-chasing and sit near Riot's
prohibition on alternate skill-rating systems — the dashboard shows the real
underlying metrics instead. Chart greens/blues re-validated per surface
(#2ea043 rolling-WR line; accent #4f9cf9 is UI-only, too light for marks).

**React + Vite SPA served from wwwroot by the API** (one process on the work
machine); dev uses the Vite proxy. Chart palette (blue/red diverging for LP
gain/loss, single blue series for LP-over-time) validated with the dataviz
skill's CVD/contrast validator in both light and dark modes.

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

## 2026-07-09 — Personal API key features (spectator, challenges context, replays)

**Spectator polling lives inside MatchPollerService, not a second background
service.** One pass = one spectator call + the match-list check, sharing the
scope, error handling, and the rate-limiter budget. A separate service was
rejected: two independent cadences would race the "game just ended → fast
capture" transition that this feature exists for. Shared state is a tiny
`LiveGameState` singleton (poller writes, `/api/live` reads); the end-of-game
transition arms a 6-minute fast-capture window (15s cadence) that disarms as
soon as any new match is ingested.

**Live banner shows champions only, no lobby ranks.** Enriching 9 opponents with
league-v4 would cost 9 calls per game start for scouting data the tool's
philosophy (review your own play) doesn't need. Revisit only if lobby scouting
becomes a real goal.

**Replay archiving uses the official match-v5 `/replays` endpoint — probed live
on 2026-07-09, returns pre-signed S3 URLs for the last ~5 games (1h expiry),
verified to serve real .rofl files (RIOT magic).** Chosen over the two
alternatives: LCU-driven client downloads (works, but needs a host-side bridge
into the container and an open client) and op.gg-style spectator chunk
recording (undocumented endpoints; the app registration explicitly promised not
to use those). Downloads go through a plain HttpClient — the pre-signed URL is
the auth; sending X-Riot-Token to S3 would leak the key off Riot's hosts, and
the rate limiter must not throttle S3 transfers. Sweeps run at poller startup
and after every ingest; the ~5-game window means an offline stretch loses its
replays, accepted for a tool whose PC is always on. Trade-off accepted: .rofl
playback is patch-locked by the client, so the archive is "review this patch",
not a permanent library.

**Challenges ladder context ships as `levelShare`/`nextLevel`/`nextLevelShare`
on the existing percentiles payload** (one extra cached-7d call to
`challenges/percentiles`), rendered as "GOLD = top 9% · next: PLAT = top 3%".
A separate leaderboards-per-challenge endpoint exists but costs a call per
challenge; rejected as 200+ calls for context the aggregate distribution
already gives. A missing distribution degrades the row, never hides it.

## 2026-07-09 — Clip pipeline (server side)

**No RenderJobs table.** Job state derives from files, consistent with the
db-as-disposable-index rule: pending = replay archived + kill/death windows
plannable + no mp4s; done = mp4s exist; failed = render-failed.json marker;
rendering = in-memory lease (RenderLeaseService, 30-min expiry, deliberately
not persisted — a restart re-offers the job and uploads are idempotent by
window index). The plan manifest (plan.json) is written into the clip folder
at claim time so the clip list survives db rebuilds.

**Windows come from the KillEvents table, not timeline re-parsing** — kills
and deaths of the tracked player, [t-20s, t+10s], overlapping windows merged
so a kill followed by a death is one "fight" clip. Assists deliberately
excluded from v1 (would clip every teamfight; revisit if wanted).

**Agent protocol is pull-based over plain HTTP** (POST /api/render/next,
PUT clips, complete/fail) because the agent sits on the gaming PC behind NAT
and the tracker moves to TrueNAS — outbound-only from the PC, no inbound
holes. Upload body cap lifted per-endpoint (512MB), not globally.

## 2026-07-09 — Render agent

**The render agent is a separate always-interactive Windows exe, not a
service** - the game must render to a real desktop for window capture, so it
runs at logon in the user session (Task Scheduler), never as a session-0
service. Discovered during build: League is NOT installed on the tracker dev
box, so the agent/server split is required today, not just after the TrueNAS
move.

**Mock render mode (LT_MOCK_RENDER) is a first-class feature**, not test
scaffolding: it exercised claim → rofl download → mp4 upload → complete on a
machine with no League install, and stays as the smoke test for any future
protocol change. Gotcha kept out of the mock: ffmpeg's drawtext filter
crashes on Windows builds (fontconfig missing) - plain testsrc2 only.

**Capture is ffmpeg gdigrab by window title at 30fps/CRF23** - chosen over
OBS automation (heavier dependency, needs obs-websocket config) since the
user's OBS is busy recording live play anyway. Revisit if gdigrab frame
pacing disappoints; the seam is one method (CaptureAsync).

**The agent trusts the server for camera identity** (/api/render/next carries
MyName/MyChampion from the at-game-time participant row) because current
Riot ID can drift from the name recorded in the replay.

## 2026-07-09 — Full-game renders

**Full-game renders are opt-in per match, never automatic** — at ~6 games/day,
auto-rendering would cost ~1TB/year and ~3h/day of gaming-PC render time for
games mostly never rewatched; clips stay the automatic tier. The storage
policy IS the button. Guardrails: retention sweep deletes unkept renders
after FullGameRetentionDays (default 60, poller-driven every 6h; clips are
exempt - small enough to keep forever), and /api/storage keeps the per-family
disk usage visible on the Data page.

**Same files-as-truth pattern as clips**: {matchId}.requested queues,
.mp4 is the result, .keep exempts from retention, .failed.json blocks
retries. Lease keys are kind-prefixed (clips:X / full:X) so the two job
kinds never block each other. A full-game job reuses the agent's window
machinery verbatim: it is one window from 0 to game end - the agent needed
only kind-aware upload/complete routing.

**Interactive replayit-style live streaming (camera switching mid-watch) was
evaluated and parked**: an mp4 in <video> natively covers pause/seek/speed;
the only capability lost is changing the camera target after render, which
does not justify HLS streaming + a control relay for a single-user tool.

## 2026-07-09 — The Lens (Phase C, rescoped from per-match curves)

**Per-match metric curves were demoted mid-phase** on user direction — dpm.lol's
Lens (fight-level coaching scores) is the model, not u.gg's line charts. The
/api/matches/{id}/series endpoint was kept (built and cheap) but has no UI.

**Fight detection is ours, from stored data**: kill events chain into a fight
while within 15s and 3500 units of the cluster centroid; headcount = killers +
victims + anyone interpolated within 2500 units at mid-fight; duel = 1v1,
teamfight = 3+ both sides, else skirmish. Result counts victims per side (so
executions count), gold swing is the team-gold-diff change across the fight
(60s-frame coarse — honest ceiling), conversion = winner takes an objective
within 45s. Persisted per game as FightsJson (schema-free, like ChallengesJson).

**Lens scores are self-percentiles, not cohort estimates**: score 73 = the
recent window's mean sits at the player's own 73rd percentile across all
stored games. We deliberately do NOT fake a "vs Gold" cohort baseline (no
population data); Riot's Challenges percentiles remain the external anchor
and are cross-linked in the UI copy. dpm's NEW vs OLD compare is the model
for the tile detail (recent window vs everything before it).

## 2026-07-16 — Fundamentals ladder (rank-tier skill map)

**The "no composite scores" rule is refined, not repealed.** The 2026-07-05
decision rejected a single weighted rating; that stands. What the Fundamentals
feature adds is PER-SKILL levels, and the Riot-policy line was re-checked
before building: the prohibition is "products cannot create alternatives for
official skill ranking systems... MMR or ELO calculators" (developer.riotgames.com/policies/general).
Per-skill-area assessment in a personal post-game coaching tool is the
explicitly-encouraged use case, so long as (a) no overall "your real rank is X"
number is ever derived, and (b) tier labels are anchored in Riot's own data.
Both are structural here: areas never aggregate, and the only tier chip an
area shows is the MEDIAN of its mapped Riot Challenge levels (Riot's own
Iron→Challenger grading), never a home-grown estimate.

**Curriculum rows are fixed, evidence is ours**: each of the eight skills sits
at the tier where coaching curricula say it starts gating games (Gold: macro,
information gathering; Plat: matchup understanding, win-condition; Emerald:
trading, teamfighting; Diamond: jungle tracking, warding) — the boxes never
move with performance. Per-area evidence = Lens-style self-percentile over the
player's own games + the challenge anchor. Jungle tracking has NO honest
challenge mapping and therefore shows no ladder chip at all — a deliberate gap
rather than a stretched proxy.

**New timeline derivations** (reprocess-backfillable like everything else):
Death.EnemyJunglerNear (enemy jungler interpolated within 2000u at my death;
pre-14:00 deaths with it = gank deaths, the jungle-tracking signal) and
Match.TeamGoldDiff15/20 (whole-team gold at the milestone frames; win-condition
conversion = win rate conditioned on being 1k+ ahead/behind at 15). Metric-row
computation was extracted from LensService into MatchMetricRows so the Lens
and Fundamentals score from one implementation.

**Known caveat, shown in UI copy**: Riot challenge levels are lifetime-
cumulative, so they partially reflect playtime, not just skill — acceptable
for an anchor because Riot computes them, we don't.

## 2026-07-16 — The three questions (process review, result-blind)

**Purpose is mindset, not analytics**: the user judges games by win/loss; the
review answers three process questions per game and never mentions the result:
(1) did I out-duel my lane - whole game, not just laning; (2) did my fights buy
the map; (3) did I account for the enemy before stepping. Computed on the fly
by ReviewService from stored rows (no new Match columns): kill events (now
carrying assist ids - new column, reprocess-backfilled), fights, deaths,
objectives, position samples.

**The absence ledger is the novel part**: for every fight the player skipped
where the lane opponent got kills/assists, classify where the player was
(dead / elsewhere / nearby-uninvolved via interpolated positions) and whether
the absence PAID (a structure the player personally took within the window).
Same for enemy epics conceded while far away - the Baron-while-splitting
pattern. Split-pushing is never flagged as inherently wrong; only unpaid
absences count against the verdict.

**Verdicts are transparent sums of named +/-1 components** (thresholds as
consts: 300g lane swing, 500g late, 45s respawn window, 4000u "elsewhere",
60s paid window) - deliberately tunable against real games rather than
pretending precision. Surfaced as three L/F/D dots per match row and a
"three questions" card at the top of the match detail, above the scoreboard.

**2026-07-16 addendum — four questions, symmetric ledger.** The lane-duel
audit was one-sided (only their gains from my absences); it now runs the same
ledger both ways - my cash-ins while the opponent was dead/away count for me,
their unpaid absences count against them - so two split-pushers judge equally.
Opponent fight-participation is inferred from kill involvement or proximity to
the kill centroid (mirroring the analyzer headcount). Fourth question added:
lead stewardship - lane gold @10 vs the last checkpoint at 20/25/30, verdict
by state transition (grew/held/flipped, recovered/reduced/grew), team gold
15->20 shown as context. Dots are L/F/D/S.

**2026-07-16 addendum 2 — the contest verdict (fifth verdict, five tiers).**
Four dots answered four questions but never the one the mindset work
actually needs answered: did I win the CONTEST, overall — definitively,
regardless of the game's result. Added a derived fifth verdict, a pure fold
of the four question verdicts (mixed and unanswerable questions excluded):
dominated (3+ won, none lost) / won / split / lost / run over (the mirror).
Result-blind by construction — winnable in a Defeat, losable in a Victory.
Both ends are deliberately harsh: the exposure only works if the tool can
say "you got run over" as plainly as "you dominated"; a sanitized bottom
tier would teach the brain the tool lies.

This does not reopen the 2026-07-05 "no composite scores" decision: that
rejected cross-game skill ratings, and the refinement stands — the contest
verdict is per-game only, folds nothing across games, and estimates no
rank. Guardrail extended deliberately: no contest win-rates, streaks, or
aggregation of verdicts anywhere in the product, ever — that would rebuild
the LP gauge out of new material.

UI hierarchy now states the values: on Matches the contest verdict is the
primary row label and tints the row (green/amber/red), while Victory/Defeat
demotes to the muted sub-line — visible, never hidden (hiding it would be
avoidance, not exposure), just no longer the headline. Rows without a
review (no timeline) keep the old result-primary look. The review card
opens with the full verdict chip plus a one-sentence summary composed from
the decisive questions ("You out-dueled your lane, but your fights bought
nothing — that's the game") — the post-game "one honest sentence" rep,
automated.

**2026-07-16 addendum 3 — first real-game tune: dead-time cash-ins leave
the duel ledger.** Motivating game: 43m Viktor vs Syndra, kill exchange
6-1, +727g @15, +1754g @30 — and the lane duel still came out "mixed"
(net +1), folding the contest to "split". Audit showed the two negative
components were counting the same failures the other questions already
judge: the opponent's "cash-ins" at 35:19/42:14 happened while the player
was DEAD in late teamfights (Fights had already said no, Discipline had
already counted the deaths), and the 18:41/18:47 moments hit both the
cash-in comparison and the unpaid-absence count at once. Fix: kills
cashed in while the other laner was dead are excluded from the duel
cash-in comparison, both ways (dying to a gank/fight is not losing the
1v1; the ledger list still shows the moments). Unpaid absences unchanged
- being cross-map for nothing is still the split-push audit. The game
re-verdicts to lane yes / contest won on a Defeat, which matches the
honest story: lane smashed, fights bought nothing, that's where the game
went. Also: the review card now prints the lane verdict math (each ±1
component and the net, with the ±2 thresholds) so a surprising verdict
can always be audited at a glance instead of trusted.

**2026-07-16 addendum 4 — the absence ledger starts when laning ends.**
Motivating game: 27m Viktor vs Lux, 0-2 into -2183g @15 - a lost duel by
every direct measure - yet the lane verdict floated at "mixed" and the
contest folded to "won". Cause: a 12:08 roam fight (2 kills while Lux was
"cross-map") earned +1 twice, as a cash-in and as her "unpaid absence".
During laning the absent laner is usually just farming - payment the
ledger cannot see, since it only recognizes structures - and the roam
fight itself is already credited by the Fights question (the dead-time
lesson of addendum 3 again, in a different costume). Fix: ledger moments
before 14:00 are dropped entirely, using the same LaneEndSec boundary
gank deaths already use; post-laning moments are unchanged, because
that's the genuine split-push economy the ledger was built for. The game
re-verdicts to lane no / contest split - "lost the lane, saved the game
by leaving it", the player's own account of it. Note for future tunes:
the player's stated rationale ("I was behind most of the game, so it
shouldn't say won") was scoreboard reasoning and was NOT honored as
such; the tune stands on the double-count mechanism alone. Verdicts must
never be bent toward the gold graph - that's the LP gauge in disguise.

**2026-07-18 — follow-ins need a "was I already there?" check, and the
two count-based questions get denominators.** Motivating games: 29m Garen
vs Gwen (EUW1_7922058605) and 37m Ahri (EUW1_7921448852), both 2-death
wins with heavy fight participation, both stuck at Discipline "mixed".
Audit of the first: the flagged "follow-in" was the grubs collapse - the
trigger teammate (Amumu) fell 3 seconds before and 161 units away. They
were standing together taking grubs; whoever dies second in a shared
fight was being tagged as if they walked into a grave they watched open.
And the grub secured seconds before the death was invisible to the trade
check, which only counted enemy kills. Three fixes in the analyzer and
verdict fold:

1. Not a follow-in if, at the last raw frame before the teammate fell, I
was already within 2500 units of them (or of the spot that became the
fight). Raw frame, not interpolation - interpolating across my own death
smears me toward the fight and would hide real walk-ins. Dying second in
a shared fight belongs to the Fights question, not Discipline.

2. Payment now includes objectives: a friendly epic/structure within
3500 units of my death, taken from 30s before the trigger to 10s after
me, flips FollowPureLoss to false (the grub banked mid-collapse, the
turret we died completing). Traded follow-ins leave the Discipline
"bad" count and show as their own line; pure losses stay fully punished.

3. Denominators. Discipline's yes-bar was literal perfection: one
flagged death in any game killed it, while the 22 fights stepped into
correctly were invisible. The question is phrased as a habit and now
scores like one: one flagged death with no unpaid concessions stays
"yes" when the game had 12+ fights stepped into. The no-thresholds are
untouched. Same disease in Fights: converted*2 >= won demands 10
conversions of 20 won fights, but conversion opportunity does not scale
linearly with wins - added a volume path (won >= 3x lost, 5+ converted,
0 conceded -> yes). Card now prints the denominator ("stepped into N
fights") so the evidence FOR the habit is visible, not just the lapses.

Verified against the last 15 ranked games (live data, old vs new fold):
Q2 flips exactly two mixed->yes (the 20-4/9conv Ahri and a 16-2/5conv
Viktor - both the volume pattern); every "no" stays "no". Q3 flips three
mixed->yes (both motivating games plus a 14-fight loss with one pure
follow-in) and one no->mixed (a traded follow-in leaving the bad count);
the run-over games all keep their verdicts. Analyzer retag verified by
synthetic timeline through the real Analyze: co-death not tagged,
walk-in still tagged pure-loss, walk-in with epic banked tagged traded.
Note: the player's account of both games was checked against the data
before tuning (3s/161u co-location confirmed) - the tune stands on the
mislabel, not on the plea. Existing rows keep old tags until a
/api/analytics/reprocess; the verdict fold changes apply immediately.

## 2026-07-18 — Dashboard reads like a summary, not a spreadsheet

**Stat tiles collapsed into one KPI band with progressive disclosure.** Nine
cards of dense numbers became six headline figures (record, KDA, DPM, CS@10,
lane gold@10, deaths/game) with one context line each, and everything
second-order (phase splits, vision, multikills, skillshots) behind a "More
detail" expander. Modeled on how dpm.lol/tracker.gg front a summary strip:
the reader gets the state of the account in one glance and digs only on
intent. LP deltas left off the tiles — the profile header already owns them.

**Strengths & weaknesses leads with the story, not the data.** The 26-bar
wall answered "what are my numbers" but not "what wins my games". Now the
card opens with up to four win-lever cards (|separation| >= 8pp) drawn as
paired win/loss bars on a shared scale, a quiet higher-in-losses strip with
an explicit game-length caveat, and the full explorer collapsed behind
"Explore all metrics". The separation floor keeps noise metrics from
masquerading as insight.

**"Vs the ladder" (Riot Challenges percentiles) removed from the dashboard.**
Challenges are lifetime achievement grinds: account-scoped, playtime-
confounded, never windowed. "Master Tank: Iron" measures champion-pool
choice, not skill; percentile deltas there produce "train this" advice that
is actively misleading. Fundamentals keeps its per-area ladder context,
which is scoped to real skill areas. Component, endpoint call, and CSS all
deleted rather than hidden — nothing consumes challengePercentiles now.

**Mobile: grid tracks that host tables are minmax(0,1fr), never bare 1fr.**
A bare 1fr track floors at the table's min-content width, forcing page-wide
horizontal scroll on phones. Found measuring real layout width via headless
dump (screenshot right-edge clipping turned out to be a capture artifact —
Edge lays out wider than it captures).

**Fundamentals ladder judged by own form, not Challenge levels.** The box
chips and colors were anchored on the median of Riot's lifetime Challenge
levels — cumulative grind counters. Cross-account data made the flaw
undeniable: a Bronze 2 account with ~6x the lifetime volume (20,535 wards
placed vs 3,070; 522 Baron powerplays vs 79) holds Master chips on every
skill and a board that glows "ready" for Platinum. Chips removed from the
ladder; colors now read the player's own recent-form percentile on the
rows at/below the goal (60+ strong, 40+ train, below priority), each box
carries a net trend arrow vs baseline, and the detail card auto-opens the
weakest goal-relevant skill — the training map the page was meant to be.
Challenges stay only as the detail card's labeled lifetime-context strip.
The footnote now states the box numbers are self-relative and never
comparable between accounts.

**LP history back-fill: one-time import from dpm.lol, maintenance endpoint
only.** Riot's API serves current LP and never history, so the months before
this tracker existed can only come from a tracker that was already watching.
dpm.lol's rank-history widget serves one closing tier/division/LP per active
day back to 2026-05-17; POST /api/lp/backfill (no UI, same pattern as
/api/ranks/backfill) imports those days strictly before our earliest real
snapshot, mirrors them to lp-history.csv, and is idempotent. Imported rows
carry Wins=0/Losses=0 deliberately: per-game attribution requires the
win+loss counter to move by exactly one across a bracket, so back-filled
rows can extend the chart but can never mint a per-game LP delta. Per-game
LP does not exist anywhere in dpm's API (lp field null account-wide,
verified on two accounts) - day-level resolution is the honest ceiling.

**Missing per-game LP is shown, not hidden.** The LP-per-game chart used to
filter unattributed games out entirely; a capture gap read as a gap in play.
They now hold their slot as a small neutral stub ("? LP" tooltip) with a
coverage footnote, and the champion/role tables print the coverage fraction
("-30 · 11/18g") - a partial LP sum over a biased subsample (Ahri: known
games 5W-6L, unknown 4W-3L) otherwise reads as a verdict it isn't.

**KDA color is a monotone single-hue ramp.** The old green->blue->amber
steps ranked a 10 KDA below a 3 to anyone reading amber as a warning
(which is what amber means everywhere else in this app). Better KDA is now
simply brighter green.

**Stop-loss banner: the tilt guard argues from the player's own history.**
Motivating incident: 2026-07-13 went 0W-4L for -86 LP - more than three
good days earn back. GET /api/stoploss computes, over all ranked games,
the winrate of the NEXT game after N straight same-session losses
(sessions chain games ending <3h apart; buckets 0/1/2/3+), plus the
current tail streak. A global banner appears only while a losing session
is live (2+ straight losses, last game <3h ago): amber at 2 ("one more
loss and the math says stop"), red at 3+ ("the math says stop for
today"), always citing the measured next-game winrate vs fresh. No LP
involved, so it works on hide-LP instances and needs no attribution;
evidence is suppressed below 5 bucket games. Generic break-reminders were
rejected - the banner only says what the player's own record supports.

## 2026-07-19 — Render agent: no more postpone-loops

**Three identical postponements hard-fail the job** (agent-side counter,
in-memory per session; the server-side fail is what persists). Postpone
exists for transient conditions, but a reason that repeats identically is
deterministic: two jobs from 17-18/07 recycled on every lease expiry for a
day-plus without surfacing anywhere. Failing puts them on the Data page
where retry is a click.

**A sim-hung window is retried once on a fresh game process, then skipped.**
A hung replay only looks alive (the Replay API answers, seeks settle) and
never recovers within the process, so the old postpone-and-relaunch-next-
lease cycle re-rendered the same early windows forever and froze at the same
timestamp. Now: relaunch + retry once; a second freeze skips the window,
the remaining windows render, and the job fails naming what was skipped -
partial coverage with a visible reason instead of an invisible loop.

**Camera-lock verification measures distance from a parked reference that
rotates per attempt** (12600,12600 / 1800,12800 / 12800,1800) instead of the
world-reload corner. The old check read "camera still near the default
corner" as "lock failed", which is wrong whenever the fight is near blue
fountain and the champion stands still (stealth included) - the most
plausible reading of the Shaco job that failed "camera did not engage" on
six consecutive idle-time attempts. The camera is parked via the Replay
API's cameraPosition (safe before the clicks; render writes reset the
dropdown state, which is also why the park re-asserts the selection), and
the check measures from the read-back position, so an ignored write
degrades to the old behaviour rather than a new failure mode.

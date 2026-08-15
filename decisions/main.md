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

**Camera-lock verification is death-aware.** The engage-failure frame from
the looping Shaco job proved the lock WAS engaged (dropdown: "Shaco
(TheCosmicPeach) in 6...") - the player was dead during the pre-roll, and a
locked camera parks a dead champion's view at their fountain, which for a
blue-side player is exactly the world-reload corner the check used as its
"unlocked" reference. Locked-on-a-corpse and never-locked were
indistinguishable, and every retry re-seeked into the same death - a
deterministic loop. Now: when the check fails, ask liveclientdata for
isDead; wait out respawns up to 25s (playback keeps running) and re-verify -
the respawned champion walking out of the fountain moves a locked camera.
Longer respawns fall through to the postpone cap, the backstop for every
deterministic verify failure. (A rotating parked reference was tried first
and stays as a harmless fallback: the Replay API ignores cameraPosition
writes, so the park read-back just returns the world-reload corner.)

## 2026-07-21 — Live-game recording (auto-VOD)

**The render agent doubles as a game recorder.** A second loop beside the
render loop: when the local LCU gameflow phase turns `InProgress` (a real
game - replay renders report `WatchInProgress`, so the two can never
confuse each other), capture the game window and stop when the game ends.
Chosen over a separate recorder app (Ascent et al) because the agent
already owns every needed ingredient: LCU polling, window geometry,
ffmpeg, and residency on the gaming PC. Inspecting Ascent's local install
showed its recorder is a bundled headless OBS driven the same way - there
is no secret in the capture, only in being resident and phase-aware.

**Capture is ddagrab straight into NVENC, no CPU round-trip.** ffmpeg's
ddagrab hands out D3D11 frames; h264_nvenc consumes them on the GPU, so
recording while playing costs a hardware encode session and nothing else.
Verified vs Ascent's own settings (1080p30 @ 5 Mbps CBR); ours records the
native window at vbr cq26 (~1.5-3 GB per game at 1440p60), 60fps default
because mechanics review reads better at 60. One x264 fallback attempt if
NVENC refuses to init, then the game is sat out (a broken encoder is
deterministic - retrying every pass would spam ffmpeg all game).

**Fragmented mp4 while recording, faststart on finalize.** A live game
cannot be re-captured, so the on-disk format must survive a crash: every
moof fragment is self-contained, and orphaned `.part.mp4`s are finalized
at next agent start. The finalize remux is a stream copy (ms per GB).

**Sidecar json carries identity and the clock map.** Match id comes from
the gameflow session (known from the loading screen, before the game
serves anything); video-time -> game-clock pairs are sampled every 30s
from liveclientdata/gamestats so the review UI can place Match-V5 timeline
events on the video without guessing the loading-screen offset. Recording
metadata is files-next-to-video, no db - same rebuildable-index philosophy
as the tracker: anything derivable must be derivable again.

**Agent deploys drain, never kill; orphaned replays are idle-gated kills.**
Two deploys tonight hard-killed the agent seconds after it claimed a clip
job (a claim can land between checking the log and killing), leaving the
replay process orphaned - which blocks every later pass as "Game client
running" until it happens to exit. Now `stop.requested` next to the exe
makes the agent postpone between windows (job re-leases), finalize any
live recording, and exit; the deploy script waits for the exit. Backstop
for hard kills: a game process while gameflow reports None for 3
consecutive polls AND the user is input-idle is an orphan and gets
killed. Idle matters because API-launched replays leave gameflow at None
(verified live) - so a replay watched via the tracker's links is
indistinguishable from an orphan except by someone being at the keyboard.

**Champion drill-down: every matchup, shown as widgets not a nested table.**
The expanded champion row used to hard-filter lane matchups to `Count() >= 2`
and cap at 10 (Reports.cs), so Ahri's 15 games collapsed to 3 visible
opponents behind a "(2+ games)" label. It now returns every opponent faced
(Take(50), a per-champ-per-window list is naturally bounded by games played),
ordered games-desc so repeated laners still lead. Single-game rows are safe to
show because WinrateBar only tints win rate at 5+ games - a 0/100% singleton
never reads as a verdict. The drill body was re-skinned to the app's own
tile/widget vocabulary: the six stat pills became a `mini-tile` band (the
KPI-band eyebrow+hero-number treatment at drill scale), and the matchup table
became a scrollable column of `matchup-row` widgets (icon + games + the same
WinrateBar the champion rows use + G@10/KDA), max-height 236px so a champ with
many distinct laners scrolls in place instead of blowing out the card. Empty
state is now a dashed-border widget message. Rejected a responsive card grid
for the matchups - the vertical scroll-list matches the "infinite scroll bar"
ask and stays denser. The window filter already drives this data, so "All"
works for free once the 2+ threshold is gone.

**Trend charts: adaptive smoothing, date axis, verdict sentence.** A
last-10 rolling win rate over 330 games is sample jitter (0-100%
sawtooth), and "game 143" on the x-axis anchors nothing. The rolling
window now scales with the data (10 games <=80, 20 <=200, 30 beyond;
floored at half the window so tiny scopes still draw), partial ramp-in
windows are dropped (the old opening 100%-after-one-game spike), and
the axis speaks dates (~6 evenly spaced ticks; marks stay equally
spaced per game). Both charts open with a computed verdict sentence -
recent half vs earlier half of the selected window ("Trending up - 57%
over your last 165 vs 49% the 165 before") - dead zones +-5 WR points /
+-75 gold so noise never gets narrated as a trend. Lane gold keeps the
per-game diverging bars (disasters stay visible) but fades them past 80
games under a bold rolling-average line in neutral ink - deliberately
not blue/red, it summarizes the bars rather than reading as a second
series. Rejected trend-line-only at large windows (hides outliers) and
weekly bucketing (uneven buckets, gaps for non-play days).

## 2026-07-22 — Q2 rebuilt as a personal question: "Did I leave my fights alive?"

**The Fights verdict no longer folds team-state facts.** The old fold (kill-count
record + conversion ratio + `conceded > converted`) graded the TEAM's fight war:
a 12-game audit adjudicated by the player found 5/12 verdicts contradicting the
personal record (7-6, 6-4, 7-6, 17-8 graded "no"; a perfect 9-0 graded "mixed"),
and 90% of all losses graded "no" — unusable for the system's purpose of
separating personal play from outcome.

**New verdict = overstays only**: deaths post-8:00 with >=2 allies near and
allies >= enemies, EXCUSED inside a participated fight with >=3 enemies
committed (you can't solo-exit a committed teamfight — rule adjudicated from
kill-log review of real games). 0/1/2+ overstays → yes/mixed/no. Validated over
336 games: 52.5%/35.8%/0% win rate, 77% of losses score zero (earnable in
defeat). Won/lost/converted/conceded stay in the detail payload as context.
Paid absences (skipped fight + own building kill within ±90s) are listed as
credit — the split-push case — but never drive the verdict (outcome-conditioned:
20%→66% WR, same trap as bounty metrics).

**Rejected:** deaths-inside-won/drawn-fights as the overstay definition (fight
Result is a cluster-level team fact; no WR gradient — 44.8/51.8/51.5); paid
absences as a verdict input; keeping any conceded/converted branch.

**Discipline picks up what Q2 dropped:** fog picks (0 enemies near, post-laning,
outside committed fights) and outnumbered steps (enemies >= allies+2, replacing
the narrower `isolated` which required exactly 0 allies). Without this, a 0-9
disaster game grades Q2 "yes" with nothing charging the repeated outnumbered
deaths. Trade-off accepted: genuine run-over games now rely on
Lane/Discipline/Stewardship + the context record to carry the "no"; frame
proximity counts are known-unreliable inside fight clusters, so cluster context
always wins over frame counts.


## 2026-07-29 — One game, one file: the recorder survives capture death

**Diagnosis of a week of split/lost VODs** (agent.log, 23-28 Jul): every
failure was ffmpeg ending on its own 6-38s into the game, then the restarted
capture minting "Game N+1" for the tail of game N. Two mechanisms:

1. `-shortest` made ffmpeg stop when EITHER input ended - so the audio pipe
   dying ended the whole video recording with exit code 0, which the
   "failed early" check (exit != 0) waved through as a complete 0-minute
   game. Desktop Duplication (ddagrab) also cannot survive the display mode
   switch of alt-tabbing an exclusive-fullscreen game, which is exactly what
   happens in the first minute of most games.
2. Game numbers were counted from the mp4s in the folder, so recycling or
   renaming files between sessions made a later game reuse - and OVERWRITE -
   an earlier number (happened 25 and 27 Jul).

**The fix is supervision plus segments, not a sturdier capture**: ddagrab's
session loss is a Windows fact (OBS-class recorders own a capture engine to
get around it; not worth it for this). Instead each capture attempt is a
numbered segment ({name}.segNN.part.mp4) of ONE game whose name is allocated
once per LCU match id; when the game ends the segments are remuxed and
concatenated (stream copy, seconds) into a single mp4, with clock-map and
input-telemetry timestamps offset onto the joined timeline. A seam of a few
seconds replaces a lost half-game.

- `-shortest` is gone; a dead video stream is detected by ffmpeg's own
  `-progress` frame counter stalling (~12s, vs ~60s of the old file-growth
  heuristic), and a dying audio writer pads silence forever rather than
  EOF-ing the pipe (audio must never end a video recording).
- Game numbers come from max(folder scan, per-day ledger in
  metadata/game-numbers.json); a game that never produced footage hands its
  number back so days stay gapless.
- {name}.inflight.json persists per-game state after every segment: a crash,
  deploy or agent restart resumes the SAME game - even appending onto an
  already-finalized mp4 (it re-enters as segment 1 and re-concatenates, and
  the stale .uploaded stamp is dropped so trackers get the full version).
- Startup failures retry 4x with backoff before the recorder sits a game out
  (transient mode-switch windows heal; deterministic breakage still
  hard-fails per the no-postpone-loop rule).
- LT_RECORD_TEST=seg smoke-tests the segment/concat/merge path end to end
  without a game.

**Rejected:** appending to the open mp4 (no such thing mid-write without a
recorder-owned muxer); gdigrab as a mode-switch-proof fallback (BitBlt of a
D3D game is black); trimming frozen tails at the seam (complexity for
seconds of dead video the join already bounds).

## 2026-07-29 — DDragon variant sets; YouTube links for any game

**Champion lookups treat underscore DDragon ids ("Jade_Ezreal") as variants
that never shadow the real champion.** Patch 26.15's champion.json ships ~60
alternate-mode entries (Jade_*, keys 60xxx) whose display name equals the
real champion's; the name-keyed icon/id maps let whichever entry iterated
last win, so Ezreal and Garen rendered the Jade set's retro art everywhere.
Canonical ids never contain "_" and variant sets always do (Jade_*, the old
Swarm Strawberry_*), so variants may fill an empty name slot but never
overwrite a canonical entry — one rule in the shared loader (champions.ts),
no per-set denylist to maintain.

**Any game can hold a YouTube VOD link, not just agent-recorded ones.** The
VOD card hid itself without recording data, leaving nowhere to paste a link
for games played elsewhere or recorded by hand — while the API accepted
links for any known match. Unrecorded games now get a compact link box; once
linked, the full review card takes over. Without a recording clock map,
moment-jumps assume the video starts at game clock 0:00 and say so
(approximate jumps beat dead buttons).

## 2026-07-29 — Plan B capture engine: WGC behind a config flag

**Why a second engine exists**: ddagrab's fragility is structural - Desktop
Duplication sessions die on exclusive-fullscreen display switches, and ffmpeg
has no recovery. Ascent never breaks because it bundles OBS (ascent-obs.exe =
rebranded libobs), whose capture is composition/hook based. Segments make
ddagrab's deaths cosmetic, but Ruben's trust needs an engine where they don't
happen at all - available BEFORE the next failure, not engineered after it.

**`CaptureBackend` config ("ddagrab" default | "wgc")**: the wgc path records
through ScreenRecorderLib 6.6 (Windows Graphics Capture -> Media Foundation
hardware H264, fragmented mp4) - DWM-composited capture that mode switches and
alt-tab cannot interrupt. Video only: game-process-only audio stays ours
(ProcessAudioCapture), paced PCM written beside the segment and muxed to AAC
at finalize - whole-desktop loopback (Ascent included) can't promise
Discord-free audio. Everything downstream (segments, naming ledger, inflight
resume, telemetry merge, uploads) is engine-agnostic and unchanged; WGC
startup failure falls back to ddagrab per segment. MF quality = 96 - cq
(cq 26 -> 70; ~7Mbps static desktop 1440p60, bt709 tagged). Supervision stays
on for wgc too (growth watchdog) - engines can die quietly regardless.
LT_RECORD_TEST=wgc smoke-tests the whole path; LT_CAPTURE_BACKEND overrides
per run. Gotchas burned in: ScreenRecorderLib is C++/CLI, so the csproj pins
Platform x64 and the dll must ship LOOSE next to the single-file exe
(BadImageFormatException from inside the bundle) - deploys copy exe + pdb +
ScreenRecorderLib.dll.

**Rejected:** replacing ddagrab outright (a week-hardened path traded for an
unsoaked one); WGC frames piped raw into ffmpeg (~900MB/s memcpy tax at
1440p60 vs ScreenRecorderLib's all-GPU pipeline); bundling headless OBS like
Ascent (heaviest dependency for the same capture class WGC provides).

## 2026-08-04 — Recordings publish themselves to YouTube

**The agent uploads each finished recording to YouTube and registers the
link with the owning tracker** - the storage-free review mode existed end to
end except for a human uploading the mp4 in YouTube Studio and pasting the
link into the match page, daily. YouTube has no service accounts, so the
uploader acts as the channel via OAuth: `--youtube-auth` runs the one-time
browser consent (loopback + PKCE, hand-rolled - no Google SDK for two
endpoints) and stores the refresh token next to the exe; the OAuth app must
be "In production" or Google expires that token every 7 days. Uploads use
the resumable protocol with the session URI persisted per game
(`.ytsession.json` sidecar), so a game launch, deploy or dead wifi resumes
from the last acknowledged byte instead of re-sending gigabytes. Titles come
from the existing name ledger minus separators ("Road to Platinum 03 Aug
2026 Game 2") - byte-identical to the hand-made uploads they replace.

**Delivery is three independent files-as-truth stamps** (`.uploaded` =
tracker has the sidecars/VOD, `.youtube.txt` = the watch URL, `.linked` =
the owning tracker got the link), each retried by the same idle sweep that
already redelivered VODs; link routing reuses the try-every-tracker
ownership rule (404 = not yours). Failure taxonomy per the agent's rules:
quota exhaustion (1600 units/upload against a 10k/day default = ~6/day,
excess queues to tomorrow) and network are postponements; a revoked/expired
grant or a 4xx reject is deterministic - `.ytfailed.txt` stops the retry
loop and the log says so loudly.

**Uploads never fight a game for the machine**: they only start in idle
sweep windows, and the chunk loop (16MB pieces) aborts the moment a League
game process exists - worst case one chunk of overlap with a loading screen.
Known limitation accepted: unaudited Google API projects get uploads forced
private regardless of the requested visibility; the audit exception is a
console form, not code.

## 2026-08-04 — VOD-covered matches stop earning automatic clip renders

**A match that already has VOD review data (recorded mp4 or a YouTube link)
is skipped by the automatic clip planner** in /api/render/next. The 24 Jul
"clips still render as backup" stance was priced for a manual, fragile
YouTube step; with auto-publish live, every recorded game has the real
footage on YouTube plus a local archive copy, and the invisible third copy
(the UI already hides clips behind the VOD card) still cost idle-time
renders, replay downloads and disk per game. The gate is per-match and
late-bound (checked each time an agent asks for work, and sidecars reach the
tracker within a second of game end), so agentless trackers (Ben), queues
outside RecordQueues, and failed captures keep rendering exactly as before —
and unlinking a match's VOD makes its renders eligible again while its
replay is still archived. Explicit full-game render requests stay honored:
user intent outranks the heuristic, and the UI already hides that button
where a VOD exists.

## 2026-08-04 — Fights become VOD jump markers; VOD card sheds debug chrome

**The analyzer's fight clusters ride the match detail payload** (a FightsJson
pass-through - no analyzer change, no reprocess) **and the VOD card marks
every skirmish and teamfight as a jump point**, participated or not: the
review question "what was the team's 3v3 doing while I split?" is now one
click instead of scrubbing. Labels carry size/result/conversion ("teamfight
4v5 · lost · without you"); marker tone follows the fight's result (drawn
fights are neutral - a green glyph on a lost fight would lie). Duels are
deliberately excluded: my own duels already exist as kill/death markers, and
other lanes' solo trades are noise at review time. The card also drops the
resolution/encoder line and "played as" - single-account trackers state the
obvious, and the encoder is agent.log material.

## 2026-08-04 — Team fights the player skipped become automatic replay clips

**Non-participated skirmishes/teamfights auto-render as clips filmed from a
surviving fighter's POV** - Ruben's point: a "without you" fight marker on
his own VOD can only show his minimap; the replay is the only camera that
was there. The analyzer now keeps the fight's camera pick
(CameraParticipantId: involved ally alive through the fight, else surviving
enemy, else the last-dying fighter - a dead champion's camera parks at its
fountain), fresh clip plans append "fight" windows (significance gate:
teamfights always, skirmishes only at 2+ kills; duels never - solo trades
elsewhere are noise), and the agent resolves dropdown slot + verified
selection PER WINDOW instead of per job (an unknown fight target skips the
window, not the job). VOD-covered matches flip from "no automatic renders"
(this morning's rule) to "fight windows only"; agentless trackers render
everything, so Ben gains fight clips too. The match page shows a "Team
fights" card next to the VOD (kill/death clips stay hidden behind it).
Backfill needs one /api/analytics/reprocess per tracker (CameraParticipantId
defaults to 0 = unclippable on old rows). Replay patch-lock still applies:
fights only clip while the match's replay runs on the installed client.

## 2026-08-05 — VOD clock map: encoded position over wall clock, interpolation over median

**The ffmpeg path's clock-map pairs now use the encoder's out_time (already
parsed from -progress) as the video coordinate instead of wall-elapsed
seconds.** Wall time counts ffmpeg's startup latency plus any encoder lag,
which landed review markers seconds early against the finished video — the
YouTube timing drift Ruben reported. The WGC path deliberately KEEPS wall
clock: verified in ScreenRecorderLib/RecordingManager.cpp that Media
Foundation writes VFR frames whose durations are the real time between
captures, so wall elapsed since first frame IS the stream position there.
Its OnFrameRecorded.Timestamp was considered and rejected — it is
system_clock epoch millis, not stream position. Sampling densified 30s→15s
(localhost call, negligible cost).

**VodReview maps game↔video by piecewise-linear interpolation over the
sampled pairs instead of one median offset.** A capture restart leaves a gap
in the video while the game clock runs on, so the two sides of a seam sit at
different offsets — a single constant was wrong for every marker after the
first seam. Both coordinates are monotonic, so one sorted array serves both
directions (markers game→video, APM tooltip video→game); outside the sampled
range extrapolation uses slope 1 (the clocks tick at the same rate).
Alternative rejected: fixing timing via Riot's Spectator API — gameStartTime
reads 0 for minutes and spectator data runs ~3 min delayed; the local Live
Client API (already sampled) is strictly better.

## 2026-08-05 — WGC becomes the default capture engine; watchdog watches frames, not bytes

**CaptureBackend default (AgentConfig + shipped appsettings.json) flipped
ddagrab→wgc.** WGC was merged as an opt-in plan B and never enabled, so every
"broken video" seam to date was produced by ddagrab's known failure (Desktop
Duplication dies on exclusive-fullscreen mode switches) — the engine built to
fix it had not run. Fallback order unchanged: a WGC startup failure still
drops that segment to ddagrab and retries WGC on the next one.

**The WGC stall watchdog now keys on Recorder.CurrentFrameNumber, keeping
file growth only as a backstop (restart requires BOTH dead).** The old
<5 MB/min growth check false-positived on visually quiet stretches, where
quality-mode H264 writes almost nothing — a false restart WGC would get
blamed for. Verified in ScreenRecorderLib source that Record() wires the
frame-number callback unconditionally, so the counter advances for every
rendered frame. The AND with growth means the new check can only ever
restart less than the old one, even if a future library version stopped
reporting frames.

## 2026-08-05 — Q3 epic concessions get the same laning-phase gate as the Q1 ledger

**Discipline's conceded-epic loop now skips objectives before LaneEndSec
(14:00).** The Q1 absence ledger already excluded laning-phase fights because
the "absent" laner is holding their lane — payment the ledger can't see,
since it only recognizes structure kills — but the Q3 concession check had
no phase filter, so an early dragon/grubs taken while the player laned
cross-map (with 2+ allies contesting) charged an unpaid concession under
the exact rationale the ledger fix rejected. Same gate, same constant.
Alternative rejected: recognizing farming/CS as payment — CS-per-window
attribution from 60s frames is noise, and structures remain the only
payment signal the system can honestly verify. Verdicts recompute at read
time from stored objectives, so no reprocess is needed; historical
Discipline verdicts with pre-14:00 unpaid concessions may soften on next
view (20 of 448 audited verdicts change, all no→mixed or mixed→yes,
including the miscredited Ahri-vs-Lux split-push win EUW1_7925471410).

**Alternative rejected after audit: the "mirror rule" (charge a pre-14:00
concession only when the lane opponent rotated to the epic and you
didn't).** Audited 448 timeline games (May 10–Jul 24): 44 pre-14:00
concessions; the opponent had rotated in just 6 (all mid-lane games, 3W/3L
— no gradient; top's 16 were all cross-map farming, opponent rotated 0
times). The decisive number: ranked games charged ONLY by early
concessions ran 58% WR vs the 51% no-concession baseline — the early
charge was anti-signal, penalizing correct play — while post-14:00
charges (kept by the gate) run 36% WR, the real discipline signal. Vs the
gate the mirror rule changes 3 verdicts, 2 of which sit on interpolation
margins (me 4207u vs the 4000 threshold, opp 2384u vs 2500) in won games —
the same frame-proximity unreliability the Q2 adjudication flagged.
Adjudicated with Ruben 2026-08-05: blanket gate stays; no opponent
plumbing into Discipline. Audit script: scratchpad audit_q3.py against a
copy of data/leaguetracker.db.

## 2026-08-05 — Repo CLAUDE.md + style-only cleanup

**CLAUDE.md added and force-added to git.** Carries the Git Commits & PRs,
Comments & Documentation, and Code Style sections from the user-level global
instructions so future sessions apply them without user-level config. Force-add
needed because `~/.gitignore_global` excludes `claude*.md`; tracking overrides
the ignore from here on.

**Bare `///` prose comments left as-is, not converted to `//`.** The codebase
has zero `/// <summary>` blocks but ~849 plain `///` narrative comments across
59 files — a deliberate house convention, and their content is exactly the WHY
material the comment rule protects. Converting would have been a wholesale
reformat of protected content for zero information gain. Same ruling applied to
the frontend: JSDoc `/** */` blocks on exported types were kept (they also feed
IDE hover), reverting an agent pass that had downgraded them to `//`.

**Cleanup deliberately skipped:** null/pattern conversions inside EF Core
IQueryable lambdas (patterns don't compile in expression trees — e.g.
Reports.cs `.Where(p => p.Tier != null)`), `Count == 0` sites where a pattern
would also match null and flip a guard, and handle/`nint` comparisons in the
interop-heavy recorder code.

## 2026-08-05 — Replay engage order: fog first, camera last

**The fog side is clicked before the camera selection, not after** (Ruben's
ask). Rationale beyond preference: the camera lock is the only engage step
with a verification, so it should be the final UI interaction before the
recording rolls — previously the unverifiable fog clicks landed after the
verified lock, and a missed second click could leave the fog dropdown open
on screen for the whole clip. With fog first, the camera-box click closes
any stray fog list. The old order existed because the freshly-initialized
post-world-reload UI ate the session's first fog click and the ~5s camera
verification doubled as settle time; fog-first replaces that with an
explicit 1.5s settle before the first click. Watch the first fight-clip job
after deploy: a "Camera check failed ... selection=''" loop or an all-map
(fog-free) clip means the settle is too short.

## 2026-08-05 — Fight clips: the camera target must be alive for the fight

**Found via EUW1_7936338594 window 16:** a 39s "skirmish 2v4" clip contained
only aftermath - the designated POV (Gwen) had died in the preceding fight,
sat on a 25s respawn timer at engage, and the agent's respawn wait pushed
recording past the entire window. Two compounding causes, both fixed:

**Analyzer:** "surviving fighter" only meant no death in *this* cluster. A
corpse from the previous fight interpolates as "near the centroid" (dead
bodies don't move), counts as involved, and outranks everyone. Camera pick
now tiers alive-throughout fighters (no death within 60s before the fight -
a death timer at any game length) above recently-dead survivors, keeping
the old order inside each tier. Headcount still counts such corpses as
fighters ("2v4" may be inflated) - left alone deliberately; changing it
would retro-shift fight labels and the Q3 analytics adjudicated 2026-08-05.

**Agent:** the respawn wait (built for own-death windows, where the 20s
pre-roll absorbs it) now refuses to wait past a fight window's event time:
dead target + respawn landing after the fight moment = skip the window with
it named in the job failure, not a postpone (the replay's respawn state at
that timestamp is deterministic - a retry can never go differently) and not
an aftermath recording. Already-saved plan manifests keep their original
camera targets (plans are claim-time snapshots); the fix applies to matches
planned after the analyzer reprocess.

## 2026-08-05 — Re-rendering a match drops its plan snapshot

Plans are claim-time snapshots so rendered mp4s stay matched to the window
indices they were rendered against - but nothing ever deleted one, so a
match planned before an analyzer fix re-rendered against the same bad
camera target forever. Re-render now drops the plan with the clips: the
whole-match retry always, and a single-clip delete once it removes the last
mp4 (nothing left pinned to the old indices). Deleting one clip out of
several still keeps the plan - its siblings are named by it.

The alive-for-the-fight margin also grew from 60s to 80s to cover the clip's
20s pre-roll: a fighter who respawned 45s before the fight is alive at
engage but spends the pre-roll walking down a lane, which is not the shot.

## 2026-08-07 — The finished game plays itself back, in the client, before the next queue

**A game that has just ended opens its own replay, camera locked to the
player, and stops at each moment that decided something - unless the next game
is already being queued for.** Recording the game automated away the review
that used to happen by accident (glimpses of the capture while AFK at the PC),
and the between-games window is the only one where a review can still change
anything.

Not a video reel on the match page: that was the first build and it was wrong.
The match page already holds the full VOD with jump markers AND the rendered
clips for fights the player missed - a reel over the same footage is a fourth
way to watch what is already watchable. The client's replay is the thing the
website can't be: a real camera, in the actual game, that can be flown.

**The reel is scoped to moments the player was IN** (`/api/matches/{id}/reel`).
The session locks the camera once and never re-aims it, because the dropdown
click is the only route to a follow-cam and re-aiming per moment is precisely
the fragile machinery the clip pipeline already carries. A fight across the map
from a locked camera is thirty seconds of fog - and those fights already have
clips filmed from someone who was there.

Selection is dedup before ranking. A death inside a fight IS that fight; two
fights five seconds apart are one fight with a re-engage. Then fights rank
(gold swing, bodies, teamfight, converted) and cap at ten. A death's window
ENDS at the death: the replay parks a dead champion's camera at their own
fountain, so every second past it is an empty base.

**The reel rolls on by itself and the hotkeys are the override** (F9 skip, F8
back, F10 again) - `PostGameReviewAutoAdvance`, on by default. There is no quit
hotkey: closing the replay window ends the session, which alt+F4 already does
and needs no explaining. The
first build parked at every window and demanded a key; watching it run, the
stopping was friction rather than reflection, and going back is the rarer
action that deserves the keypress. The hook drains queued keys BEFORE each
seek, not after: with the reel advancing on its own, a key pressed while the
next moment loads is the player reacting to the one that just ended (usually
"wait, go back"), and eating it makes the hotkeys feel dead exactly when they
matter. It is a low-level hook because the game holds focus and the agent has
no window; it swallows nothing and lives only for the session.

**The camera must be re-aimed after EVERY seek** (found live 2026-08-07): a
seek reloads the world and drops the camera back to Manual, so the session
that aimed once at launch played every moment on the free camera. Each moment
now seeks ~9s early and re-clicks the dropdown during that lead-in, so the lock
is in place before the window proper starts - the same reason the clip pipeline
engages per window rather than per job. The aim is deliberately unverified,
unlike the render path's: that pipeline verifies because nobody is watching it,
while here the player is, and a camera that didn't take is obvious in a second.
Camera geometry and the directed-camera cfg write moved to `ReplayCameraUi` so
both callers share one set of coordinates.

**Two live-system hazards found while testing this, both invisible to a build.**
The render loop kills a game process when gameflow reads "None" and the user
has been idle three polls - and a review's replay is API-launched, so it reads
"None" while the player sits still to think. The review guard therefore runs
FIRST in the render gate, ahead of that orphan sweep. And the session used to
adopt whatever game process it found and kill it on the way out; it now refuses
to start when one is already running, and never kills while the client says a
game is live.

**The render loop stands down for the session** (`ReplayReview.SessionActive`).
Someone watching a replay looks exactly like an idle machine to the render
gate, which would otherwise launch a second replay over the top of the review.

Off by default (`PostGameReview`): it takes the screen, which is only welcome
if you asked for it. `LT_REVIEW_TEST=<matchId>` runs a session now instead of
waiting for a game to end - the only way to exercise launch, camera lock, seek
and hotkeys without playing first. Honest limits: the review needs the match
imported AND its replay archived, so it opens minutes after the game rather
than at the honor screen (the agent waits `PostGameReviewWaitMin`, 8 by
default, then gives up loudly); and replays are patch-locked, so this only ever
works for a game just played, which is exactly the use case.

## 2026-08-12 — The gaming PC sleeps; TrueNAS wakes it

**Replay rendering stays on real hardware — Vanguard refuses every VM** (VAN
138 on KVM/VMware/Hyper-V, no workaround as of 2026), so "move the replay side
to the NAS" is off the table. The energy problem is solved the other way
round: the PC sleeps (S3, ~2W instead of ~80W idle) and a `waker` service in
the TrueNAS stack polls every tracker's `/api/render/queue` each minute,
broadcasting a WoL magic packet while any job sits `pending` or `partial`.

**No "is it awake" probe.** Windows firewalls routinely eat ICMP, so a ping
check would misread an awake PC as asleep — instead the packet is simply sent
every poll while work waits; magic packets are no-ops on a running machine.
`rendering` doesn't wake: a lease means the PC already holds the job, and a
mid-render sleep (which the agent must prevent, not the waker) re-queues via
lease expiry anyway.

**Host networking, not the `frontend` bridge:** a magic packet is a subnet
broadcast that cannot cross the Docker bridge onto the LAN. Side effect worth
keeping: the tracker hostnames resolve through the LAN's split-horizon DNS
straight to Traefik, so the waker carries no Cloudflare Access token.

Still to do before the PC actually sleeps: the agent must hold
`ES_SYSTEM_REQUIRED` while rendering/recording/reviewing, then Windows gets an
idle-sleep timer. Until then the waker idles harmlessly alongside an always-on
PC.

**First live sleep test failed - the broadcast never left the NAS's subnet.**
The PC lives on 10.10.10.x, the NAS on 10.10.40.x; a directed broadcast to
another subnet is dropped by the UniFi gateway like by everything else. The
primary wake path is now the UniFi controller itself: the waker logs into the
console (`/api/auth/login`, then `cmd/stamgr` `wake-device`) and the gateway
emits the magic packet inside the PC's VLAN. Credentials are a local UniFi
admin set as Portainer stack env vars (UNIFI_USER/UNIFI_PASS) - never in the
repo. The raw broadcast stays as a free secondary for a same-L2 future. The
auto-launch chain proved itself the same evening: claim, hub Play press,
render, upload, all unattended.

**The UniFi wake path passed its live test, so the PC gets to sleep - guarded.**
KeepAwake pulses ES_SYSTEM_REQUIRED (no ES_CONTINUOUS: await hops threads, and
a continuous assertion belongs to the thread that made it) while anything
unattended runs: a render job, a recording (its finalize/upload outlives the
player), the catch-up upload sweep, a review session. The launch chain gets a
five-minute timed hold because a WoL-woken machine re-sleeps on the UNATTENDED
idle timeout - two minutes by default, shorter than the client boots. With the
guards in, the Windows sleep timer finally comes off "never".

## 2026-08-15 — The agent becomes something you can hand to a friend

**One tracker per player stays; the agent splits into roles and stops being
one machine's tool.** Ben's and Vanessa's PCs will run it unattended, and the
replay work moves to a dedicated old PC, so the agent grew what an unattended
install needs: `RenderReplays` next to `RecordGames` (recorder-only on a
player's PC never touches the replay client; renderer-only on the render box
serves every tracker over the URL list it already had), a tray icon with
Pause/Resume/Quit (pause is a file, so it survives reboots - the "off switch"
the system never had), `--install`/`--uninstall` on a per-user Run key, and a
heartbeat every poll that the Data page lists under Agents.

**Secrets live on the NAS, not on friends' PCs.** Each tracker serves an
agent profile (`Agent:Profile` → `/api/agent/profile`): the shared YouTube
channel's OAuth client + refresh token, title prefix, queues. The agent
applies it under anything the local `appsettings.json` sets, at start and
hourly - a rotated token reaches every machine without anyone touching them.
The uploader's "auth broken" latch is tied to the credential values, so a new
token un-breaks it without a restart. Consequence for templates: the shipped
`appsettings.template.json` no longer writes the YouTube keys at all (a
written key wins over the profile).

**Builds install themselves through the deploy handshake that already
existed.** `deploy/publish-agent.ps1` stamps `yyyy.M.d.HHmm`, zips agent +
launcher + ffmpeg, drops it in the shared `agent-releases` folder every
tracker mounts; the agent downloads when idle (no game process, nothing
recording/uploading), verifies sha256, stages next to the exe, writes
`stop.requested` and lets a detached cmd swap files after it exits (previous
build kept as `*.prev`, settings and tokens never overwritten). Dev builds
(0.0.0.0) never self-update. Smoke test: a 1.0.0.0 install updated to a
published build and relaunched in three seconds.

**Multi-account in one process was considered and deferred.** The process
*is* the account today (`RiotOptions`, `Match` denormalised on "me",
`IsMe` bool, no account key anywhere); the compose-per-player model gives
isolation for free and only costs a hostname + Access app per friend. If
cross-account features ever matter (duo stats, rendering a shared fight
once), the path is N `DataDir`s/DbContexts behind an account route prefix in
one process - not a single-DB tenant column.

## 2026-08-15 — Every page audited on phones; nothing important hides behind a hidden scrollbar

**Context.** All six routes of the three trackers were driven at 360/390/412
px with real data (headless Chromium, every button clicked, every tab
opened) and screenshots read segment by segment. Nothing pushed the page
sideways - the earlier `overflow-x: hidden` + scroll-strip work held - but
"fits" and "usable" had drifted apart in a few places.

**What was actually broken.**
- Match history cards: the K/D/A block sat beside the duel portraits, so at
  360px the champion names collapsed to zero width and the KDA overlapped the
  opponent portrait. The KDA now takes its own row under the duel.
- Match-detail scoreboard: a 920px table in a scroller with a hidden
  scrollbar - only ring + name were visible, and nothing hinted the rest
  existed. It restacks into one card per player (portrait/name/KDA on the
  first line, runes + spells + items on the second, KP/CS/dmg/vision on the
  third) using grid areas on the same `<tr>`/`<td>` markup.
- Dashboard champion/role tables: 650px wide, so a champion drill-down
  (tiles + matchup rows) spanned the whole table and was cut at the card
  edge. KP, CS/m, G@10 and Deaths are hidden on phones (`col-extra`); the
  drill-down carries them anyway.
- Item race: seven slots on two half-width rails were ~10px each. My rail
  now sits over the opponent's rail at a readable size.

**What changed for discoverability.** The primary nav wraps to a second row
on phones instead of swiping ("Data & sync" was invisible unless you knew to
scroll). Segmented chip strips and table scrollers paint an edge shadow on
whichever side still has content (`background-attachment: local` covers over
`scroll` shadows - no JS). Wide tables that stay wide (deaths, lane
checkpoints, objectives) pin their first column so a row never loses its
label while scrolling.

**Not changed, on purpose.** The 11-chip window selector still scrolls
rather than wraps (two rows of chips would push every page's content
further down). The VOD moment strip stays proportional to video time even
though close moments overlap on a 300px strip - the strip *is* the timeline.

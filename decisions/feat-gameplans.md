# feat/gameplans — per-champion reference points

## 2026-09-02 — where ability casts can and cannot come from

**Decision:** phase 1 scores reference points from the match timeline only;
timestamped ability casts come later from the agent (Live Client polling
during the game, then an ability-bar read of the live VOD / full-game
render), never from a third-party runtime.

**Researched, not remembered** (developer.riotgames.com/docs/lol, the Live
Client events sample, League Director's api.py, developer-relations issues):
- match-v5 exposes casts only as per-game totals (`spellXCasts`), and #569
  (Katarina E always 0) is still open. The timeline has no cast event.
- Live Client Data (`127.0.0.1:2999`) has no cooldown/ready flag on
  `activeplayerabilities` and no cast event; it does expose the live
  `abilityHaste` curve, which is the one thing the R-usage rate could not
  otherwise know.
- Replay API render flags include `interfaceTarget` (the locked champion's
  ability bar with cooldowns - already on in `ReplayApiClient.FollowPlayerAsync`)
  and `interfaceFrames`; the agent's full-game render therefore already films
  the cooldown state for the whole game.

**Alternatives rejected:**
- Overwolf GEP / ow-electron (`usedAbility`, `abilityReady`, ally ult timers
  in `team_frames`) - the only sanctioned cast stream, but a per-app enablement
  with Overwolf DevRel and a second runtime on every friend's PC. Parked, not
  refused.
- Decoding `.rofl` packets (maknee's 2025 write-up) - needs hooks into the
  game binary's decryption, breaks per patch, no released code, ToS grey zone.

## 2026-09-02 — gameplans are files; auto-scores are read-time

**Decision:** `{DataDir}/gameplans/{Champion}.json` holds the authored plan
and never touches the db. The auto-rules run at request time over the stored rows
(kills, objectives, positions, items, `FightsJson`), the way `ReviewService`
does, and are never persisted.

**Why not the FightsJson pattern (compute in the analyzer, store a column)?**
Rules are parameterised by the player (which level, which item, how wide a
window); persisting their verdicts would tie every edit of the plan to a
reprocess of all history. The only new fact the rules need that the db lacked
is my absolute level timing, so that alone becomes a column (`LevelSecs`,
"0,45,98,..." for levels 1..n) and gets backfilled by the normal reprocess.

**Trade-off accepted:** the per-champion adherence view evaluates rules for
up to N matches per request. Rows per match are small (~300 positions, ~50
kills) and N is capped, so it is the same cost class as `/reviews?ids=`.

## 2026-09-02 — no manual points: scored by the tracker or not on the sheet

**Decision (reverses the self-rating design from earlier the same day):**
every reference point carries a rule; there is no self-rating, no note, no
"unrated" state, and `data/gameplans/checks` is gone. Ruben: "I don't see
myself updating the manual fields consistently" - a checklist nobody ticks
is noise on every match page.

**What that dropped from the two sheets, and why each stays out:**
- Ahri: trade off last hits, charm vs divers, strictly play off R CD - need
  ability casts; return with agent phases 2/3. W to dodge skillshots, keep
  wave neutral - no data source exists.
- Viktor: small-wins mindset, poke not wave, pressure sponge, lull states,
  call for hover - no honest observable. "Plan resets in advance" via
  unspent gold was measured and rejected: max unspent gold <= 2k runs 39%
  wins vs 68% above on Ahri (40% vs 77% Viktor) - sitting on gold is what
  stomping looks like, so the rule would scold the games he won.

**What was automated instead** (scratch `candidates.py`, games >= 16:40):
- `numbers_fights` for "shove & move to create man advantages": fights
  joined after 14:00 with allies > enemies, having moved >= 2.5k from the
  last frame to the fight. 2+ ran 58% / 59% wins vs 35% / 33% below (Ahri /
  Viktor). Default 2.
- `duels_taken` for "don't be afraid to 1v1 in side": participated duels
  after 14:00. No win signal either way (52/52, 48/55), and Viktor's side
  duel record is 59 won of 134 - the sheet's premise is doubtful; the point
  stays because it is honestly measurable and Ruben can delete it.
- `jungler_fights` for "play off jungler": share of my fights after 14:00
  with the jungler on the ledger or beside it, n/a under 3 fights. Replaces
  the proximity proxy on Ahri (which ran against winning). At 50% it was met
  in 89 of 100 Ahri games; default 60% (73 met / 21 missed, 55% vs 43%).

## 2026-09-02 — rule vocabulary is closed and each rule can decline to judge

Twelve kinds: `level_window_fight`, `objective_arrival`, `picks`, `item_by`,
`level_by`, `jungler_proximity`, `jungler_fights`, `numbers_fights`,
`duels_taken`, `early_wards`, `caught_out`, `early_skirmish_deaths`.
The last two arrived with the Viktor sheet ("ward & lean", "everyone wants
to kill you"): both read rows the Discipline verdict already computes
(`WardsFirst10`/`FirstWardSec`, the fog-pick death test), so they cost a
rule each and no new column. Viktor over 30 games: wards 16/14, neutrals
2/20/8, caught out 20 met / 9 missed. Every rule returns `na` when
the game gave no opportunity (never hit the level, jungler never came, game
ended before the window) and `pending` when the row predates `LevelSecs`
(reprocess fills it) - so a short game never reads as a missed point, and
an unreprocessed one never reads as anything. Free-form rules were rejected:
the UI would have to explain arbitrary expressions, and the honest ceiling
of the data (positions interpolated between minute frames) is easier to
state per kind than per formula.

## 2026-09-02 — calibration against the local 523 games (Ahri, last 30)

After a reprocess (523 matches, LevelSecs filled): level-6 fight with the
jungler 7 met / 22 missed / 1 n/a, with evidence sentences that read as
intended ("Hit 6 at 5:41 — skirmish 3v2 with Kayn at 8:40, draw"); Lost
Chapter by 9:00 24/6; picks 27/3; jungler proximity ≥40% 8/22.

**Changed:** `objective_arrival` default lead 30s → 60s and `fromSec` 0 →
14:00. With 60-second frames a 30s lead judges the interpolation between the
frame before and the take, not the player; one minute is the finest honest
"early". The rule stayed harsh afterwards (4 met / 19 missed / 7 n/a) - a
mid laner who shoves and rotates rarely stands at the pit a minute early -
which is the coaching point, not a bug; the knobs are on the point.

**Kept:** the adherence table hides the percentage under three judged games.

## 2026-09-02 — the level-6 window was a guess; it is now measured

Ruben caught EUW1_7969770641 reading *missed*: level 6 at 5:38, grouped with
Talon at 7:00, skirmish 3v2 won with Talon at 8:46 - eight seconds after my
3:00 window closed. Scratch `level6_window.py` over the local history: the
median gap from 6 to the first fight beside the jungler is 4:53 (Ahri, 216
games) / 4:54 (Viktor, 163); a 3:00 window catches 29% / 25% of them, 5:00
about half. **Default window is 5:00.**

Also: standing within 1.5k of the jungler in the window now reads *met*
("grouped ... no kill came of it"). A 2v2 the enemy walks away from leaves
no kill to see, and the looking is what the point coaches; it is a minority
path (7 of 41 met in the last 60 Ahri games).
The 3:43 invade kill in that game stays outside this rule on purpose - it
is a level-4 play, and the sheet says "at 6".

## 2026-09-02 — "careful in early skirmishes" is defined from the data, not guessed

**Decision:** `early_skirmish_deaths` = my deaths before 14:00 where the
fight's kill ledger names 2+ enemies or the enemy jungler was on me (a gank
is a 1v2); 1v1 deaths excluded; default allows one.

**Why those numbers** (scratch `early_skirmish.py` over the local db,
Viktor 167 games / Ahri 253): win rate by early outnumbered deaths ran
61% / 55% / 27% for 0 / 1 / 2+ on Viktor - one is a trade, two is the habit.
Lost early skirmishes discriminated even harder (67% / 47% / 15%) but a
fight's result is the team's; the rule charges only what the player
controls, as the Fights verdict does. Noted and left alone: the sheet's
premise "you are better in the 1v1" does not show - 104 early 1v1 deaths on
Viktor, duels 92 won / 88 lost, and 1v1 deaths cost as much (58/47/33).
Confirmed on the last 100 Viktor games once wired: held 74, 58% wins when
held vs 27% when not - the widest split of any point.

## 2026-09-02 — the adherence table shows wins-when-met vs wins-when-missed

Same idea as the Coach page's win/loss split: does holding the point travel
with winning on *your* games? Shown per point over the last 100 games,
hidden until five games sit on each side, footnoted as outcome-conditioned.
It already earned its place: on Ahri, "play off jungler" as jungler
proximity ≥40% runs 43% wins when met vs 56% when missed over 100 games -
the proxy is wrong for a roaming mid, and the table says so where the
player will see it.

## 2026-09-02 — two bugs the 100-game sweep found

- `objective_arrival` used one radius for "was it contested" and "was I
  near early", so loosening the arrival knob changed which objectives got
  judged (n/a went 28 → 7 → 4). Contest radius is now the analyzer's fixed
  2500; the knob only governs arrival. With that split, Viktor at 60s lead:
  2500 → held 8%, 4000 → 43%, 5500 → 64%. Default moved to 4000 - a lane
  away and moving.
- A game that ends exactly on a minute boundary files its final frame at
  the same second as the regular one (EUW1_7908271418 at 29:00; two local
  games), so any `ToDictionary(p => p.TimeSec)` over position samples
  throws. The proximity rule keys first-wins now; anything new over
  `PositionSample` must assume duplicates.

## 2026-09-02 — UI idiom: action pills, not text buttons

The first cut used borderless accent text buttons (`+ add point`, `edit
note`, row arrows). The system's inline-action idiom is `button.action`
with `.sm-action` for compact rows, so the cards use that; `.sm-action`
became a general modifier (it was scoped to `table.data`).

## 2026-09-02 — duplicate SKILL_LEVEL_UP events are collapsed

developer-relations #1100 (since patch 15.17): exact duplicate
`SKILL_LEVEL_UP` events, intermittently, up to 30+ per participant. The
analyzer now skips an event identical (slot + timestamp) to the previous one
for me. Risk accepted: two legitimate rank-ups of the same slot in the same
millisecond do not happen in play (a double level-up still ranks in two
clicks).

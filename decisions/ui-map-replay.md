# Decisions — ui/map-replay

From the 2026-09-02 product review (step 3 of its path; findings S1 and
P2): the fights a player was not in should be watchable for every account
without a replay file, a render box or a client on the right patch.

## 2026-09-02 — A map replay from the samples the tracker already keeps

**Decision.** Every moment of a match - a fight cluster, a kill, a death,
an epic objective - can be scrubbed on Riot's minimap from data already in
the account database: `PositionSamples` (ten players per timeline frame),
`KillEvents` (exact position, killer, victim, assists, damage ledger),
`ObjectiveEvents` (position, kind, side) and `FightsJson` (windows,
headcount, result). One read endpoint, `GET /matches/{id}/track`, serves
frames, kills, objectives and the participant map; the browser draws it.

**Why this before clips-first delivery** (review step 2). Clips-first
changes the agent's delivery loop, needs a recording PC to test and an
agent release that friends' machines then run unattended. This is API and
web only, was verified here against the real data, and lands for every
account on the next deploy - including accounts with no agent, whose
"fights without you" today exist only when the household render box
filmed them inside Riot's five-game replay window on the current patch.
It is also what lets the website-only tier stand on its own, which is the
argument for never forcing the agent on anyone.

**Shape.** The endpoint is separate from the match detail payload because
that one loads on every match page and the map is opened on demand.
Frames are `int[]?[]` indexed by participant id minus one, kills and
objectives carry their exact position, `DurationSec` is rounded from the
stored double. `MatchTrackService.Build` is static and tested without a
database, like `ClipService.FightWindows`.

**Client.** `mapTrack.ts` holds the maths: Riot's map-11 bounds
(x -120..14870, y -120..14980, y northward so it flips onto the image),
straight-line interpolation between samples, fight windows of -20/+10 s
and instant windows of -15/+8 s, and the dead-champion rule. `MapReplay`
is SVG over Data Dragon's `map11.png` at the version the icons already
use: ten champion icons with side rings and a gold ring on the player,
kill rings that fade over six seconds, objective diamonds that fade over
ten, a scrubber at 4x or 8x, and a filtered moment list (fights without
you first when any exist; your fights; objectives; all). The match page
mounts it in a card under the VOD card, and the Deaths & objectives rows
get a "map" button that jumps to that moment.

**Trade-offs accepted.**
- Between samples a marker moves in a straight line: a 20-second fight
  shows the approach and the exact kill points, not the footwork. The
  caption says so in the death table's words.
- No fog, wards or vision; Riot's timeline has none.
- Riot exposes no respawn timer. A champion killed at k is held, faded,
  at the kill spot until the first later sample more than 1500 units
  away - respawn is a teleport to the fountain, so that sample is the
  first proof of life. Within one frame of the respawn the marker can be
  late.
- Towers and inhibitors are not chips (a dozen "tower" rows would bury
  the fights) but still draw on the map when taken.
- Canvas was considered and rejected: ten markers at animation rate is
  trivial in SVG, which gives crisp scaling and hover titles for free.

**Verified** on a worktree instance (port 5398, real data): the endpoint
for a 31-minute game returns 33 frames, 58 kills, 27 objectives, first
frame at the fountain; the match page shows the card with 46 moments (21
fights without the player) defaulting to the first missed fight, the
dead champion faded with a cross at the kill spot inside the killer's
ring, and the player's gold ring. 142 API tests green (4 new), lint and
a warnings-as-errors build green.

**Not in this branch.** Fog-of-war estimation from ward events (Riot
gives no ward positions), a per-fight "who was where when it started"
summary sentence, and demoting the renderer to an owner-only feature -
that is a profile/config change for Ruben once the map has been used.

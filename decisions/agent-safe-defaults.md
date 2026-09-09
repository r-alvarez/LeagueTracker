# Decisions — agent/safe-defaults

Three cards from the 2026-08-25 re-audit about what the agent does on a
PC that is not the owner's: G-N1, G-N2, G-N3. Plus the agent side of the
enrolment refusal introduced on `api/enrol-hardening`. Needs an agent
release; build-only verification here (work PC, no capture).

## 2026-08-26 — The ledger decides what is ours (G-N1)

The disk budget listed every mp4 in the recordings folder and deleted the
oldest until under 20 GB; orphan adoption ffmpeg-probed every sidecar-less
mp4, asked the tracker which game overlapped it and published it under the
owner's Riot ID. Browse… in setup lets a person point the folder at their
own Videos, so an OBS recording, a holiday video, anything, was the
agent's to delete or upload.

`RecordingLedger.IsOurs`: a video is the agent's when `metadata/` holds any
sidecar under its name — `.inflight.json` while recording, the telemetry,
the finalized `.json`, the delivery marks. The one exception is `.orphan`,
which older builds wrote for any mp4 they could not place, ours or not.
Both passes filter on it; the budget's total counts only ours. Files the
ledger does not know are said once in the log and never touched. Browse…
to a folder that already holds videos and no ledger appends `LeagueTracker`
so the two never meet.

Rejected: matching the agent's own naming pattern (`<prefix> - <day> -
Game N`). The prefix is the tracker's to change and the blank-prefix
scheme is a bare timestamp; a sidecar is the only thing every recording
has left behind since telemetry existed. Recordings from before sidecars
existed fall on the "not ours" side, which is the safe side.

## 2026-08-26 — Keep means keep (G-N2)

`KeepRecordingsAfterPublish=true` — the owner's own profile line, the
mechanism the retention decision leans on — was honoured by the idle prune
but not by the budget pass, so the first delivery pass after a build
landed evicted published games from an archive over 20 GB with an Info
line. Now Keep switches the GB ceiling off; the free-space floor
(`MinFreeGb`) still applies to both passes because a full disk kills the
next recording, which is the one failure the owner would not choose.

Rejected: pushing `MaxRecordingsGb=0` in the profile for keepers. It works
today and breaks the day someone sets one without the other.

Updated by the desktop review library: keep-all now disables every automatic
eviction, including the free-space pass. The next recording is refused below
the full `MinFreeGb` floor on either the recordings or scratch drive; admission
previously used half that floor. Users can remove games from the library or
choose bounded retention to make room. The settings UI describes this behaviour.

## 2026-08-26 — Post-game review is opt-in (G-N3)

703b4b7 turned the review on for every recording agent. Thirty seconds
after a game the agent launches the replay through the person's client,
takes focus from whatever they switched to, drives the camera with
synthesized clicks calibrated at 2560×1440 (G-20, unverified on this
path) and rewrites their `game.cfg`; the install page and the setup window
said none of it. Default off; a checkbox in the setup window under "This
machine" that says what it does; a sentence on the install card. The
owner's machines get it back with `Agent__Profiles__<key id>__PostGameReview:
"true"` — the profile is a default layer, a written local key wins.

Not done here: "never take focus from a window that is not the game" and
"ask before touching game.cfg" while the review is on. Both are real and
both are ReplayReview behaviour changes with their own testing needs on a
real PC; the default-off closes the exposure for strangers today.

Superseded 2026-09-09, below. The profile route named above did not survive
`56919ea`: `PostGameReview` is not in the allow-list and
`AgentConfigProfileTests` pins that. From then on the checkbox was the only
switch, while the setup window went on naming the tracker's owner as a
fallback that could never fire.

## 2026-08-26 — A refusal is worth more than "no answer"

The tracker now answers a codeless enrolment with 403 and a reason. The
agent treated every non-2xx as unreachable, so a person without a join
code would read "is this a LeagueTracker server?". `EnrollAsync` returns
`refused:<why>`; the agent loop and the setup window's Test connection
print it.

## 2026-09-09 — Post-game review is opt-out (reverses G-N3)

Ben played a week of games (3-8 September, build 2026.903.1647.50) and never
saw a review. His log prints the recorder, renderer and YouTube banners at
every start and never ReplayReview's, so `PostGameReview` was false on that
machine; the log cannot say whether the key was absent or written false, only
that nothing turned it on. Nor did the tracker: pre-allow-list `ApplyProfile`
set any writable property and logged what changed, and `PostGameReview` never
appears in his "Profile from tracker applied" lines.

So opt-in was the whole cause, and `56919ea` (2026-09-09 09:31, after that
week) then closed the profile route the setup window still advertised - the
escape hatch G-N3 leaned on is gone going forward, not retroactively. Either
way the switch nobody reaches is the switch nobody uses, and the person
opt-in was protecting was the person who wanted the feature.

Default true. The `RecordGames` gate still keeps it off a renderer-only
box. `Save()` now writes the key on both branches: against a true default
an omitted key reads as a yes, so unticking had to become an explicit false
to mean anything at all. `PostGameReview` stays out of `ProfileKeys` - the
answer belongs to the machine that owns the screen.

Rejected: adding it to `ProfileKeys` so the setup window's promise came
true. That makes a screen-taking feature remotely switchable on someone
else's PC, which is the exposure G-N3 was written about. Also rejected:
fixing only the copy, which leaves every friend's review off until they
find the checkbox.

Accepted trade-off: the next agent update starts opening replays on
recording machines whose owners never said yes - the G-N3 exposure, taken
knowingly this time, and now stated on the install card and in the setup
window instead of contradicted by them. Worse, the old `Save()` deleted the
key on untick, so a deliberate opt-out is byte-identical to a machine that
was never asked: those people get the review back and cannot be told apart
from those who never chose. Only settings written from this build forward
carry an answer that survives a default change. The two mitigations G-N3
deferred are the ones worth building next: never take focus from a window
that is not the game, and ask before rewriting `game.cfg`.

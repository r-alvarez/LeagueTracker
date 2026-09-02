# Decisions — ui/match-stage

Ruben, 2026-09-02: "the matches page is becoming a bit too full" once the
four questions, the reference points, the VOD card, the map card and the
clips all stack above the scoreboard. Agreed on a mockup built from this
game's real data before any code:
https://claude.ai/code/artifact/f2f072bd-33a4-40dd-99d9-184d59f34985

## 2026-09-02 — Three layers, one spine

**The match page is a verdict strip, a stage, and detail tabs.**
- The strip holds the contest chip and its sentence, one chip per question
  (Lane / Fights / Discipline / Stewardship with the verdict word) and one
  for the plan (its met / missed / n.a. tally). A chip opens its full
  ledger in place under the strip; several may be open at once; the plan
  chip opens all the reference points the same way. There is no Review
  tab - Ruben: "if it's up do we need the down?".
- The stage is one moment list (fights without you first when any exist,
  your fights, objectives, all) beside a viewer with Map, Footage and Clip
  tabs for the SAME moment. Map is drawn from the timeline and is always
  there; Footage is the tracker's mp4, a hand-linked YouTube upload or the
  replay-rendered full game, seeking to the moment through the clock map;
  Clip is the rendered clip covering the moment when the render box made
  one. This folds the VOD card, the map card, the clips grid and the
  full-game tile into one component with one list.
- The tabs are pure detail: Scoreboard, Build & runes (the old Details and
  Runes tabs, which are read together), Deaths & objectives. Old `?tab=`
  values still land somewhere.
- The spine is the clock: every mm:ss in a ledger, in a reference point's
  evidence, in the deaths table or the objective timeline is a link that
  opens the stage on that moment (`TimeLink`; `linkClocks` for server
  prose, where "1:24 after" and "within 5:00" are durations and stay
  plain). `?t=` in the URL remembers the moment, so a shared link opens
  where the sender was looking.

**What moved, not what changed.** `ReviewCard` is now `QuestionPanel` +
`useMatchReview`; `GameplanCard` is `GameplanPoints` + `GameplanTally` +
`useMatchGameplan`; `VodReview` became `FootageView` (its marker strip
and private moment list are gone - the stage's list replaces both; the APM
line stays); `MapReplay` split into `MapCanvas` (the drawing) and the
stage's transport. The verdict text, the reference-point evidence and the
scoreboards are untouched.

**Rejected.** A Review tab as a "read everything" view - the same text
twice. Auto-playing footage on page load - the stage opens parked on the
moment. Showing kill/death clips only when no VOD exists (the old
"VOD-covered" rule in the SPA) - the Clip tab simply shows whichever clip
covers the selected moment; the rule lives on server-side in what gets
rendered.

**Trade-offs.** The full-game render has no clock map, so its jumps assume
the render starts at 0:00 (said under the video). A moment with no clip
says so and lists the clips that do exist as clocks. The stage renders
nothing only when a game has no timeline, no footage and no clips.

**Verified** on a worktree instance (port 5397, real data, driven over
CDP): at rest the strip shows the contest, the four chips and the plan
tally with the stage parked on the first fight without the player; the
Lane chip and the plan chip open together in place; the first clock link
in the lane ledger (18:55) opens the stage on that skirmish; the Footage
tab shows the no-recording state; "open" on the first death row opens
12:04 on the map, switches the list filter to All because a death is not
a missed fight, and writes `?t=724` to the URL. Lint and a
warnings-as-errors build green; the API is untouched.

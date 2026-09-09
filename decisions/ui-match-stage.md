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

## 2026-09-08 — The map is the fallback viewer, not the lead

Ruben, seeing the map card stacked under a linked YouTube VOD on main:
"if I have the youtube video, there's no point adding the 2d image right?
same with the clips". The stage as built defaulted to Map whenever a
timeline existed, so even with footage the page opened on the 2D image.

**Decision.** Each moment picks its own viewer: the clip that covers it,
else the footage when the player was in the moment, else the map. A
"fight without you" keeps the map even with footage - a POV recording
never had that fight. A tab the player clicks holds until the next moment
opens; every new moment re-picks. Objectives count as "the player was
there" for this purpose: the POV at a dragon shows what the player was
doing instead, which is the review question anyway, and the track's
60-second samples are too coarse to say otherwise without false negatives
pushing the footage away.

**Mechanics.** The three viewers stay mounted and the inactive ones are
hidden, because the pick alternates between map and footage on almost
every click and a YouTube iframe that remounts each time loses a second
and the API handshake its seeks need. A hidden viewer neither seeks nor
plays (no sound behind the map); a seek is honoured once, by whichever
viewer is on screen when it arrives, so switching tabs on the same moment
resumes where the video was paused. The pick reads whatever has loaded
so far, so a VOD status arriving after the track flips a parked "your
fight" from map to footage without a click.

**Verified** on the 5397 worktree instance over CDP with a YouTube link on
the newest game (linked for the test, unlinked after): at rest the first
missed fight opens on the map; "died to Vi" opens the footage; back to a
missed fight, the map; pinning Footage there holds; the next objective
re-picks footage. Lint and tsc green. The clip branch of the pick has no
rendered clip on this PC to see; it is the same one-line rule.

## 2026-09-08 — Reviewed on 5397: the viewer leads, the strip is back

Ruben's review of the merged stage against the old VOD card ("our clean
version"): the current-moment line was out of place under the Footage tab
wherever it went; the viewer and the list split the card half and half;
and the marker strip under the video, the timeline of the
whole game, had gone with VodReview.

- The viewer column is two thirds (`2fr` / `1fr`, list at least 280px).
  The map is capped at 720px so it does not become a page-tall square.
- The moment line lives only in the map view, under the scrubber, as the
  caption of what the map is playing. The footage names itself and the
  list highlights the chip; the clip footer carries its own label.
- The marker strip is back under the video, drawn from the stage's
  moments (fights, kills, deaths; objectives left out as before),
  positioned in video time so it lines up with the APM chart, the
  current moment outlined. Clicking a marker opens that moment AND pins
  the Footage tab - from the video's own timeline the intent is "seek
  here", not "show me the best evidence", so a missed fight does not
  flip the page to the map.
- The video's reported length sizes the strip once it loads; a YouTube
  embed never reports one, so the game length stands in, the same
  assumption its jumps make.
- The owner's controls under the video sit under a small-caps heading
  (YouTube link / Link this game / Recording / Replay render) like the old
  card's side box; the clock-map caveat is a muted line above them.
- The APM line was never removed: it draws whenever the recording's APM
  buckets exist. The 5397 copy has a hand-pasted link and no recording,
  which is why Ruben did not see it there.

## 2026-09-08 — Seen live: footage leads, clips are a card again

Ruben on the deployed page: with a YouTube video the stage should open on
Footage, not the map; and "where did all my clips go?" - the Clip tab
showed "none" for a game that had none, and the clips of other games were
nowhere on their own pages. Reverted the clip fold: the stage has Map and
Footage only, and the clips card from before the fold sits under the stage
exactly as it was (with footage, only the fights the POV never saw; without
it, all clips; planned windows say so).

Until the first moment is opened the footage leads whenever there is any -
parked, not playing - so the page is the game as played first. The
per-moment pick still applies once a moment is chosen: a fight without you
opens the map, your own fight or death the footage. `?t=` links count as
opened, so a shared missed-fight link still lands on the map.

## 2026-09-08 — The questions stay open; Footage is the first tab

Ruben, seeing the chips live: "can we revert back that part, everything
else seems to be correct". The four question chips are gone; the strip
shows the four ledgers open in the old two-column grid under the contest
sentence. Only the plan keeps its chip and opens in place. Same day: the
stage's tabs read Footage, then Map - the viewer already led on footage
when there is any, so the order now says so; with no footage the Map tab
is the only one and nothing else changes.

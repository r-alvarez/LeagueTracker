# Spike: clips recorded by the game engine (`/replay/recording`)

Branch `spike/replay-api-recording`. Not for merging as is.

## Question

Today a clip is a screen capture of the replay window, encoded live by x264
on `veryfast` (the only preset that keeps up in real time), about 31 MB per
clip at 1440p30. Riot's local Replay API can instead have the engine render
the same window to a WebM file, frame-exact, without the window needing to
stay unobscured. Riot documents no quality knob for it. Does the engine
output, transcoded offline by x264 on `slow`, come out smaller at equal or
better picture, and does the pipeline hold up?

## How to run it (render PC only)

1. Build the agent from this branch and stop the installed one.
2. Set, for one session:

       LT_CLIP_CAPTURE=replay-api
       LT_CLIP_CAPTURE_HEIGHT=0        # or 1080 to test a downscale

3. Let it take one clip job, or force one from the render queue page.
4. In the agent log, one line per window:

       replay-api capture: 2560x1440@30 62s - engine 71s -> webm 180.2 MB; x264 slow 40s -> mp4 19.8 MB

5. Compare against the same match rendered on main (delete its clips from
   the match page; the queue re-renders them with `ddagrab`).

## What to record

| | ddagrab (main) | replay-api |
| --- | --- | --- |
| mp4 size per clip | | |
| wall time per clip (engine + encode) | | |
| picture at 100% (HUD text, minimap) | | |
| clip starts on the planned second? | | |
| freeze check (`SimFrozeDuringAsync`) still passes? | | |
| window minimised during capture: still records? | | |

## Known unknowns

- WebM codec and quality are the engine's choice; if the intermediate is
  VP8 at a low bitrate, the x264 pass cannot put detail back and the
  comparison ends there.
- `enforceFrameRate` makes the engine wait for each frame, so wall time may
  exceed the clip length on a slow scene. The agent allows 4x plus a minute.
- Audio desync is a known Riot bug on this endpoint
  (RiotGames/developer-relations#881); clips carry no audio, so it does
  not apply.
- The camera lock and `selectionName` are set before recording starts
  exactly as on main; whether the engine honours them during a recording
  is part of the test.

## If it wins

Fold into `CaptureAsync` behind `ClipCapture`, default it, drop the
wall-clock retry for this path, and keep `ddagrab` as the fallback for
clients without `EnableReplayApi`.

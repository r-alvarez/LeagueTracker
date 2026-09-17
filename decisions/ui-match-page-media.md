# Decisions — ui/match-page-media

Ruben, 2026-09-17, on the live Ahri game (EUW1_7985839900): no way to make
the YouTube player bigger; the YouTube upload should carry timestamps; the
clips card in a long game pushes the scoreboard screens down. "Maybe a
carousel? I'm open to suggestions." Reproduced locally first (scratch
instance on 5398, Riot backfill, the plan rebuilt by hand, placeholder
mp4s): with only 7 fight clips the page was 4641px tall at 1500 wide and
the clips card alone ~2000px of it.

## 2026-09-17 — Prototypes for review (not agreed yet)

- **Theater toggle** on the stage: drops the moment rail under the video so
  the footage takes the card's full width; the rail becomes a wrapped chip
  grid; the choice sticks in localStorage. Full-screen already exists via
  the YouTube player, so this is the middle size.
- **Clip reel** replaces the two-column grid of `<video>` tiles: one player,
  a filmstrip of tiles (camera champion's portrait, clock, label, tone),
  prev/next, arrow keys, a finished clip rolls into the next. Card height
  no longer grows with the fight count.
- **YouTube chapters**: not built. The description is set once by the
  render agent at upload (`Match {id}`) and never updated; adding chapters
  needs `videos.update` and the `youtube.force-ssl` scope (re-consent).
  Plan proposed: a delivery-pass step on the agent that, once the tracker
  has fights for the match, converts game clocks through the sidecar's
  clockMap and PUTs the description once.

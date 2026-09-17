# Decisions — agent/youtube-chapters

Ruben, 2026-09-17: "if we are uploading to youtube, shouldn't we have
timestamps on the youtube video as well?" Split from ui/match-page-media
(PR #22) on his question: the web change deploys itself on merge, this
one needs an agent release and a one-time re-consent.

## 2026-09-17 — Chapters are a later delivery-pass step on the agent

- **Where.** The agent, not the server: it holds the OAuth token and the
  sidecar's clockMap. A new `TryWriteChaptersAsync` runs after the upload
  and link steps of every delivery pass.
- **When.** Not at upload: the upload usually finishes before Riot's match
  data lands, so the reel would be empty. The pass retries every ten
  minutes until the tracker's `/reel` answers, writes once (`.ytchapters`
  stamp), and gives up after a week. A reel under three moments on a game
  older than a day is stamped as skipped; younger, it is retried (the
  timeline may still be on its way).
- **What.** The tracker's review reel (the moments the player was in, the
  same list the between-games review drives), each moment's approach time
  moved through the clockMap, plus a link to the match page. "0:00
  Loading screen" first, moments under ten seconds apart folded, because
  YouTube's player silently drops all chapters when a rule is broken.
- **Scope.** `videos.update` needs `youtube.force-ssl`; added to the
  consent scope. An old token keeps uploading; the agent warns once per
  run to re-run `--youtube-auth`. Two API calls per game (list 1 unit,
  update 50) against the 10k daily quota.
- **Rejected.** Writing chapters into the upload's initial description
  (no data yet); a server-side writer (the server has the credential
  trio only for handing to agents, and no clockMap).

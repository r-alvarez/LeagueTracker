# Decisions — api/youtube-chapters

Ruben, 2026-09-17: "if we are uploading to youtube, shouldn't we have
timestamps on the youtube video as well?" First built on the agent
(PR #23, closed unmerged, branch deleted); Ruben: the server has the
moments, the link and the YouTube details, "we probably don't need the
agent at all to do this". Then: no age limit - "a pooler handling
existing videos / links that haven't got timestamps ... similar to what
we do with the clips".

## 2026-09-17 — The tracker writes the chapters; a sweeper covers the backlog

- **Where.** `YouTubeChapterService` in the API: the review reel (the
  moments the player was in), each approach time moved through the
  sidecar's clockMap, a "Review on the tracker" link from `Site:Origin`,
  then `videos.update` with a token minted from the profile trio. The
  agent keeps nothing of this; its consent flow only asks for the extra
  scope so the token the script mints works for the server.
- **When.** The link endpoint tries once and pokes the sweeper; the
  sweeper runs every ten minutes over every linked game with no
  `chapters.txt` stamp, newest first, across all accounts. No age limit:
  the reel of an old game is final, so the backlog drains in one pass
  (about 51 quota units a video). A reel under three moments is skipped
  once the game is a day old; younger, it is retried because the timeline
  may still be on its way.
- **Which credentials.** The posting agent's block first (a friend's own
  channel), then the owner's approved machines, then the shared channel.
  A video not on that channel comes back rejected and is stamped.
- **Consent.** A refresh token carries the scopes it was consented with;
  the stack's tokens have upload + readonly, so `videos.update` answers
  403 until each is re-minted with `youtube.force-ssl`. The sweeper backs
  off an hour on that answer and logs what to do. Unavoidable, one click.
- **Rejected.** Chapters at upload time (no match data yet); an agent-side
  writer (an agent release and the burden on friends' PCs for something
  the server already has).

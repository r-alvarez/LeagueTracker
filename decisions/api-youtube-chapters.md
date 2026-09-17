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

## 2026-09-17 — Chapters go up with the upload; the sweep runs at night

The first day spent the shared channel's quota: the backlog pass burnt the
day's 10,000 units, and every video linked afterwards sat unchaptered with
nothing in the log (`quotaExceeded` read as a quiet retry). Ruben: "the agent
should not only upload the youtube video but upload the descriptions ...
whilst the sweeper should only work perhaps at midnight if we have space".

- **At upload.** The agent asks the tracker for the description
  (`GET /matches/{id}/youtube/description`) and sends it with
  `videos.insert` - no extra quota. The tracker still builds the text, so
  the upload and the sweep can never write different chapters. 204 when the
  timeline or the recording's sidecar is not in yet, or the reel is thin: the
  video goes up as `Match {id}`. The link post says `chapters: true` when the
  upload carried them and the tracker stamps `chapters.txt`; a resumed upload
  session keeps the description it started with, so that is what counts.
- **Reverses** "Rejected: chapters at upload time (no match data yet)": in
  practice the tracker holds the game before the upload starts (17 Sep: data
  at 16:34:55, upload at 16:38:24).
- **Sweep at 00:30 UK**, after the evening's games are uploaded, on what the
  quota day has left; it stops at the first `quotaExceeded`. YouTube's day
  resets at midnight Pacific, so a pass the quota cut short gets one more go
  at 00:15 Pacific rather than a day later. No immediate write from the link
  endpoint any more, and no poke: nothing spends quota mid-evening.
- **Per account.** A token without the scope, or no credentials, skips that
  account instead of ending the sweep for all of them.
- **Logged.** Every wait says why (not analysed, too thin, quota, YouTube
  hiccup), and a pass reports how many it wrote and how many still wait.

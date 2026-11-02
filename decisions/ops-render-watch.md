# Decisions — ops/render-watch

Ruben, 2026-09-19: "the renderbox wasn't processing the games, we need a
way to identify this and to let me know somehow that this was the case
otherwise I wouldn't noticed". The tracker had 40 render jobs waiting
when this was found.

## 2026-09-19 — The tracker watches its own render queue

- **Why the tracker.** A stalled renderer raises no error anywhere: it is
  asleep and not woken, off, signed out, its agent is dead or paused, or
  League will not launch. In every case the queue simply grows. Only the
  tracker can see both sides of it: the queue, and the renderers'
  heartbeats. The render box itself can't report this, because the usual
  cause is that it isn't running.
- **The signal.** Per account: work is waiting (`pending`/`partial`, the
  same count the waker reads) and nothing has moved for
  `RenderWatch:StallMinutes` (120). Moving means one of: a clip or full-game
  upload started, a job completed, failed or was marked unrenderable, or a
  renderer asked `render/next` and got nothing. The last one keeps a queue
  row that no renderer will ever claim from raising a false alarm, while a
  renderer that has gone quiet still raises one. Two hours because a
  full-game render only reports at the end, and a box woken by the waker
  needs a few minutes to launch League first.
- **The cause.** Taken from the renderers' last heartbeats: none reported
  since the tracker started, offline since X, paused, or online and saying
  what it is stuck on (state, detail, last error).
- **Where it shows.** A banner on every page for admins
  (`GET /api/admin/render-alert`). The heartbeat reply to the admins' own
  non-renderer machines, which their tray shows as a Windows notification.
  It pops once per stall, again every four hours while it lasts, and never
  during a game. An optional ntfy push (`RENDER_ALERT_NTFY_URL`) says when
  a stall starts, repeats every `RemindHours`, and says when the queue
  moves again. The tray is the main channel because it is the screen Ruben
  actually looks at. The push covers the times he isn't at the PC.
- **In memory.** Progress clocks and the alert live in the process. After
  a restart every clock starts afresh, so a stall is reported at most
  `StallMinutes` late. Nothing here is worth a table.
- **Rejected.** Email: the tracker sends none, and Auth0's mailer is for
  invites only. Having the waker alert: it sees the queue but not the
  heartbeats. An alert on the render box's own tray: nobody looks at it.

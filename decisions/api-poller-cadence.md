# Decisions — api/poller-cadence

## 2026-08-26 — The poll cadence is per account (R-N1, and the R-B4 fix)

The 2026-08-18 live banner made the loop delay 30 s whenever *any* account
had a live game — for every account. "Trivial against 100/2 min" was
computed for one account: with N tracked accounts one ranked game costs
~120 extra requests for each of the other N−1, and on the dev key the
poller alone saturates at 11 accounts. Past ~50 accounts someone is always
live and idle polling runs at four times the configured rate permanently.

Now each account carries its own `nextDueUtc`: 15 s while its own game's
match is awaited, 30 s while it is in a game, `PollSeconds` (floor 30 s)
otherwise. The loop runs every due account in turn — still sequential,
one key and one limiter — then sleeps until the earliest due time, capped
at 30 s so an account added from the admin page gets its first pass
without waiting out a whole idle interval. A missing or rejected key
reschedules every account on the idle cadence: the key is shared, so one
failure is everyone's.

Rejected: a timer per account. It reads nicer but turns "one Riot call at
a time" into a property of the limiter alone; the single loop keeps the
politeness structural.

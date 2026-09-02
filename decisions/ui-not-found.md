# Decisions — ui/not-found

From the 2026-09-02 product review (finding P1, audit C1): URLs that serve
no page must say so instead of rendering an empty shell around somebody
else's dashboard.

## 2026-09-02 — The URL is left as typed; the page says what it is

**What happened before.** `bootAccount` rewrote any path that named no
known account onto the default account's prefix with the rest of the path
kept (`/foo/bar` -> `/euw/ImRA-87166/foo/bar`; a typo like
`/euw/Nobody-1/matches` -> `/euw/ImRA-87166/euw/Nobody-1/matches`), and
the router had no catch-all, so the shell rendered with tabs and an empty
body. On the server, `MapFallbackToFile` answered every unmatched path,
including unknown `/api/...` routes and missing asset files, with
index.html and a 200.

**Now.** Account resolution has four outcomes and only one mounts the app:
`account` (canonical, legacy bare slug, or a slug from before a rename -
those two are still rewritten, like the API's 301), `index` for `/`,
`unknownAccount` (a region and a slug nobody here is tracked as, with the
same slug in another region offered when it exists), `unknownRoute` for
everything else. The URL is never rewritten for the last two. The
front page lists the tracked accounts (default first) and the sign-in
state; the two "nothing here" screens reuse the sign-in screen's frame and
offer the tracked accounts when the visitor may read them. A page missing
under a tracked account (`/euw/ImRA-87166/foo`) keeps the shell, because
the account is right and only the page is not.

**Server.** The blanket fallback is replaced: unknown `/api/*` answers a
404 problem document (an API client must never get HTML with a 200), a
path with a bundle-asset extension answers a plain 404 (an old hashed
chunk must not come back as the SPA shell and be parsed as a script),
everything else serves index.html with `no-cache` as before.

**Root is an index, not a 404** (Ruben's call): it is the most typed URL
on the site and this is the smallest honest front door. `Accounts:Default`
now only orders the index and names the account global API calls bind to.

**Rejected.** A server-side 404 status for SPA not-found pages: the server
cannot know the client's routes without duplicating them; revisit when a
prerender exists for OpenGraph cards. Reusing `AccountSwitch` on the index:
its select needs a current account and its "Add account…" lands on that
account's page, which the index does not have.

**Verified** on a worktree instance (port 5398, real data): `/`, `/foo`,
`/euw/Nobody-1/`, `/euw/ImRA-87166/foo` -> 200 text/html no-cache;
`/api/nope` -> 404 application/problem+json; `/api/a/euw/Nobody-1/status`
-> 404; `/missing.png`, `/assets/old-chunk.js` -> 404; `/favicon.svg` ->
200. Screenshots of the index and all three not-found shapes; lint and a
warnings-as-errors build green. No web test runner exists in the project.

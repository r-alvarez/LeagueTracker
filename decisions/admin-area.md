# Decisions — admin/area

## 2026-08-19 — Machines and People leave the Data page

**Decision:** the Data page keeps only what is about *this account* (sync,
import, reprocess, visibility, render queue, storage, exports). Machines get
their own page (`/machines`, any signed-in person) and People + Tracked
accounts move to `/admin` (admins only). Both are reached from a menu on the
identity pill in the header, GitHub-style, not from the tab strip.

**Why:** machines belong to a *person* and admin work spans *every* account,
so neither fits a page whose whole frame is "the account in the URL". The tab
strip is the account's pages; a person's own things hang off their name.

**Alternatives rejected:**
- Machines under `/admin` only — an ordinary owner has to install the agent
  and approve their own machine, so the page must be theirs too. Admins see
  everyone's machines on the same page (the endpoint already scopes by role);
  a second machines table on `/admin` would be the same data twice.
- Adding "Machines"/"Admin" as tabs — that strip is account navigation and
  is already six wide on phones.

**Assignment UI:** the always-visible "owner email + role + Save + Unown" row
under every machine (admin only) becomes a per-row *Assign…* action that opens
the form on demand; the owner is a select over the people who have signed
in, since those are exactly the assignable ones (the endpoint returns 404 for
anyone else). Same select on Tracked accounts. Free-text email is gone.

**Routes are account-scoped like everything else** (`/euw/ImRA-87166/machines`)
because the router mounts under the account basename; the pages ignore the
account. Cost: a bookmark carries an account it does not need. Not worth a
second router.

## 2026-08-19 — Signed out, you see the sign-in screen and nothing else

**Decision:** an anonymous visitor gets a full-page sign-in screen (brand,
one sentence, one button) instead of the app shell with a wall card in it,
and `GET /api/accounts` moves behind the `Read` policy.

**Why:** the shell leaked the shape of the site (tabs, footer) and the
accounts endpoint leaked its contents - every Riot ID, who owns it, media
flags - to anyone, because the SPA needed the list before it knew who was
asking. Now `bootAccount` treats 401/403 as "not yours to see", the router
mounts at `/`, and the sign-in screen's return URL brings the person back to
the same path where the boot runs again with a cookie.

**Left as is:** `/api/agent/release` stays anonymous (installer download,
no data) and `/api/render/pending` stays anonymous by design (the NAS waker
has no identity). PublicReads=true reopens both the list and the pages, as
before - the screen is keyed off the same flag.

**Auth0's own hosted page** is not styled from the repo; its logo/colours
are tenant branding in the Auth0 dashboard.

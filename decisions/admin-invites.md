# Decisions — admin/invites

## 2026-08-19 — Inviting people: our row first, Auth0 mails

**Decision:** *Add a person* on `/admin` creates the `User` row here at once,
then creates the identity at Auth0 (Management API, `POST /users` in the
Username-Password connection with a throw-away password) and triggers
Auth0's own *Change Password* mail (Authentication API
`POST /dbconnections/change_password`). The tracker sends no email itself.

**Why this order:** the row is what makes someone assignable (accounts,
machines) and what the invite gate checks; the provider identity and the mail
are delivery. A provider failure leaves the row in place and *Resend*
finishes the job - the admin is never stuck with half an invite they cannot
see.

**Why Auth0's mailer, not ours:** it is one fewer thing in the container (no
SMTP credentials, no MailKit, no deliverability to own), and the tenant needs
a real email provider anyway for forgot-password. Cost: the invite mail *is*
the Change Password template, reworded in the dashboard; and Auth0's built-in
sender is test-only (and cannot use custom templates at all), so a provider
must be configured on the tenant before the mail reads like an invite.
**Resend** is the one chosen - native Auth0 integration, free at 3,000
mails/month, sending from `send.rjav-tech.co.uk`. SendGrid was the obvious
pick until Twilio retired its free plan in July 2025. Both steps are in the
handover doc; neither is repo state.

**Rejected:**
- Auth0 Organizations invitations (Auth0 sends a true invite mail) - drags
  the organisations model into every login for a friends tracker.
- App-sent mail via SMTP with a Management-API password-change ticket as the
  link - more moving parts for the same outcome; kept only as the *Copy link*
  fallback, which mints that ticket for the admin to hand over when a mail
  bounces.
- Allow-list only, with tenant sign-ups re-enabled - strangers would create
  tenant accounts that we then refuse; noise and MAU for nothing.

**Identity join:** the created Auth0 `user_id` is stored on the row
(`ProviderUserId`) and `FromLogin` matches it as the id_token subject before
it tries the verified-email join. An invited person's first sign-in therefore
works whether or not Auth0 has marked their email verified (it does once
they set the password, but nothing here depends on it). A social sign-in
with the same verified address still joins by email as before.

**Gate:** `Auth:InviteOnly` (default `true`, compose `true`): a sign-in that
matches nothing - no login link, no provider id, no verified-email match -
is refused with a 403 page ("not on the list", with a link that also clears
the Auth0 session) instead of creating a user. Dev-login bypasses it by
making the row first, because "any email" is its whole point.

**Removal:** only a person who has never signed in can be removed (row and
Auth0 user). Removing active people means deciding what happens to what they
own; deliberately not here.

**Credentials:** a dedicated M2M application with the four scopes it needs
and nothing else; the login client stays without management scopes. Token
cached in a singleton until two minutes before expiry, clients from the
factory so handlers rotate. Invite endpoints are rate-limited per admin
(20/hour) - a stolen admin session cannot burn the tenant's quota.

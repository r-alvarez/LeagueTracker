# Decisions — admin/names-and-ops

## 2026-08-24 — A person can be renamed; the deployment can be identified

**Renaming.** `POST /api/admin/users/{id}/name` and a pencil on the People
row. The display name was only ever written when the row was created, and
Auth0 sets `name` to the email for database users, so most rows read as an
address forever. Blank clears back to the email's local part rather than to
nothing — one helper (`DefaultDisplayName`) now defines that value, and
`FromLogin` uses it as the test for "never named by hand", so a name typed by
an admin is not overwritten the next time Auth0 sends the email as the name.

Admin-only: a person editing their own name needs a profile page that does
not exist. Rejected — writing the name back to Auth0 too: it is what we call
them here, not their identity at the provider, and the Management API scope
to do it (`update:users`) is deliberately not granted.

## 2026-08-24 — `GET /api/version`

**Decision:** anonymous, returns informational version, build time, process
start and environment. Shown at the foot of `/admin`.

**Why anonymous:** the site is behind Cloudflare Access, so a signed-in-only
version endpoint cannot answer "did the deploy land?" without a browser and a
login — which is exactly the moment you want to ask. Nothing in the response
is worth hiding, and `/api` is already Access-bypassed for the agents.

**Build time, not a git sha:** `.git` is excluded from the Docker build
context, so nothing in the image knows the commit. The Dockerfile writes
`build-info.txt` in the *runtime* stage, after both COPYs: Docker re-runs
that layer exactly when either build stage produced something new, so an
all-cached rebuild keeps the old stamp — correct, because the image is the
same one. A host run has no file and reports `builtUtc: null`, but the SDK
puts the commit in `InformationalVersion` from the local `.git`, so a source
run identifies itself that way instead.

Cost: this deploy is the last one that has to be identified by reading page
text. The trigger was exactly that - checking which build was live meant
comparing an intro sentence on a phone.

## 2026-08-24 — Data Protection keys live with the data

**Decision:** `PersistKeysToFileSystem(<DataRoot>/keys)` with a fixed
application name.

**Why:** the default keeps keys inside the container, so every redeploy
minted new ones: session cookies became unreadable and everyone was signed
out, and an OIDC login in flight across a restart failed on a nonce that no
longer decrypted. Verified by signing in, restarting the process and finding
the session still live.

**Trade-off accepted:** on Linux with no certificate configured the keys are
written unencrypted (the `XmlKeyManager` warning stays). **That folder is as
sensitive as a session cookie** — anyone holding it can forge one. It sits
inside the existing `/data` volume, so it is already inside the tracker's
blast radius, and it now belongs in whatever backs that volume up. Rejected —
encrypting them with a certificate: another secret to mount and rotate for a
tracker whose data folder is the thing being protected anyway.

**Also:** one startup line says whether Auth0 management is configured and
names the missing variables. The symptom it replaces was a sentence on the
People page, seen only after someone had already been invited — which cost
about twenty minutes on 2026-08-24 to trace to an extra dot in a secret.

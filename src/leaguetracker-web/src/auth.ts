// Who is looking at the site, from /api/auth/me. Loaded once before render
// alongside the accounts; the pages read it to decide what to show, the
// server decides what to allow - the two must agree, and the server wins.
export interface AuthUser { id: string; email: string; displayName: string; isAdmin: boolean }
export interface AuthState {
  signedIn: boolean
  user: AuthUser | null
  ownedAccountIds: string[]
  publicReads: boolean
  loginConfigured: boolean
  devLogin: boolean
}

let state: AuthState = { signedIn: false, user: null, ownedAccountIds: [], publicReads: false, loginConfigured: false, devLogin: false }

export const auth = {
  get state() { return state },
  get signedIn() { return state.signedIn },
  get user() { return state.user },
  get isAdmin() { return state.user?.isAdmin === true },
  /// May this person manage the account (owner or admin)? Mirrors the
  /// server's Owner policy for showing/hiding controls.
  owns(accountId: string) { return this.isAdmin || state.ownedAccountIds.includes(accountId) },
  /// Anonymous visitors see nothing until PublicReads is on - the sign-in wall.
  get canRead() { return state.signedIn || state.publicReads },
  loginUrl(returnTo: string = window.location.pathname + window.location.search) {
    return `/auth/login?returnUrl=${encodeURIComponent(returnTo)}`
  },
  logoutUrl(returnTo: string = window.location.pathname) {
    return `/auth/logout?returnUrl=${encodeURIComponent(returnTo)}`
  },
}

export async function bootAuth(): Promise<void> {
  try {
    const resp = await fetch('/api/auth/me', { credentials: 'same-origin' })
    if (resp.ok) state = await resp.json()
  } catch {
    // Unreachable API: leave the anonymous state; the pages will show their own errors.
  }
}

/// Every write from the SPA carries this header; the server refuses
/// cookie-authenticated writes without it (CSRF guard).
export const csrfHeaders = { 'X-Requested-With': 'LeagueTracker' }

// Which tracked account this page is about. op.gg-style: the first URL
// segment names it (/ben/matches/...), the API lives under /api/a/{slug},
// and switching accounts is a full navigation - the whole app is scoped.
export interface AccountInfo { slug: string; label: string; riotId: string; hideLp: boolean }
export interface AccountsResponse { default: string; current: string; accounts: AccountInfo[] }

let current: AccountInfo = { slug: '', label: '', riotId: '', hideLp: false }
let all: AccountInfo[] = []

export const account = {
  get current() { return current },
  get all() { return all },
  /// Route prefix the router mounts under ('' when the site is single-account
  /// legacy mode without a slug in the URL - kept for the dev server).
  get basename() { return current.slug ? `/${encodeURIComponent(current.slug)}` : '' },
  apiUrl(path: string) {
    return current.slug && path.startsWith('/api/') && !path.startsWith('/api/a/') && !path.startsWith('/api/agent/') && !path.startsWith('/api/accounts')
      ? `/api/a/${encodeURIComponent(current.slug)}${path.slice(4)}`
      : path
  },
  switchTo(slug: string) { window.location.assign(`/${encodeURIComponent(slug)}/`) },
}

/// Resolves the account from the URL before the app renders. A path whose
/// first segment isn't an account (/, /matches/...) is redirected to the
/// server's current account (the Host header decides on legacy hostnames,
/// else the default) so every URL is canonical: /{slug}/....
export async function bootAccount(): Promise<void> {
  const resp = await fetch('/api/accounts')
  if (!resp.ok) throw new Error(`/api/accounts -> HTTP ${resp.status}`)
  const data: AccountsResponse = await resp.json()
  all = data.accounts
  const first = decodeURIComponent(window.location.pathname.split('/')[1] ?? '')
  const match = all.find(a => a.slug.toLowerCase() === first.toLowerCase())
  if (match) { current = match; return }
  const target = all.find(a => a.slug === data.current) ?? all.find(a => a.slug === data.default) ?? all[0]
  current = target
  const rest = window.location.pathname === '/' ? '/' : window.location.pathname
  window.history.replaceState(null, '', `/${encodeURIComponent(target.slug)}${rest}${window.location.search}${window.location.hash}`)
}

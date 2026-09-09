// Which tracked account this page is about. op.gg-style: the URL names it
// as /{region}/{RiotId}/... (/euw/ImRA-87166/matches), the API lives under
// /api/a/{region}/{RiotId}, and switching accounts is a full navigation -
// the whole app is scoped.
export interface AccountInfo {
  id: string; slug: string; label: string; riotId: string; gameName: string; tagLine: string
  hideLp: boolean; platform: string; region: string; path: string; fromConfig: boolean
  owned: boolean; mine: boolean; mediaPublic: boolean; available: boolean
  /// Only admins are told who owns an account.
  ownerUserId?: string | null
}
export interface RegionInfo { code: string; label: string; platform: string }
/// `accounts` is the caller's own (all of them for an admin); anyone else's
/// page is reached by URL, never by listing.
export interface AccountsResponse {
  default: string; current: string; canAdd: boolean; regions: RegionInfo[]; accounts: AccountInfo[]
}
interface ResolveResponse { account: AccountInfo; canonical: string }
interface ResolveMiss { error: string; suggestion: AccountInfo | null }

// What the URL turned out to name. Only 'account' mounts the app; the
// others are whole-page answers, because borrowing someone's dashboard for
// a path that never named them is how a typo used to land on the owner.
export type Resolution =
  | { kind: 'account' }
  | { kind: 'index' }
  | { kind: 'unknownAccount'; region: string; slug: string; suggestion: AccountInfo | null }
  | { kind: 'unknownRoute'; path: string }

let current: AccountInfo = { id: '', slug: '', label: '', riotId: '', gameName: '', tagLine: '', hideLp: false, platform: '', region: '', path: '', fromConfig: true, owned: false, mine: false, mediaPublic: false, available: true }
let all: AccountInfo[] = []
let regions: RegionInfo[] = []
let canAdd = false
let defaultSlug = ''
let resolution: Resolution = { kind: 'index' }

export const pathOf = (a: AccountInfo) => `/${a.region}/${encodeURIComponent(a.slug)}`
export const slugOf = (riotIdOrSlug: string) => riotIdOrSlug.trim().replace('#', '-')

export const account = {
  get current() { return current },
  get all() { return all },
  get regions() { return regions },
  get canAdd() { return canAdd },
  get defaultSlug() { return defaultSlug },
  get resolution() { return resolution },
  /// Route prefix the router mounts under.
  get basename() { return current.slug ? pathOf(current) : '' },
  /// Account-scoped calls (/api/status, /api/matches, ...) go under the
  /// current account's prefix; the global roots stay as they are.
  apiUrl(path: string) {
    const global = ['/api/a/', '/api/agent/', '/api/accounts', '/api/me', '/api/admin', '/api/auth', '/api/render/pending', '/api/version']
    return current.slug && path.startsWith('/api/') && !global.some(g => path.startsWith(g))
      ? `/api/a${pathOf(current)}${path.slice(4)}`
      : path
  },
  switchTo(slug: string) {
    const target = all.find(a => a.slug === slug) ?? (current.slug === slug ? current : undefined)
    if (target) window.location.assign(`${pathOf(target)}/`)
  },
  goTo(a: AccountInfo) { window.location.assign(`${pathOf(a)}/`) },
}

/// A bare /{slug}/... (the first one-site build) and a slug from before a
/// rename are rewritten to the canonical address, like the API's 301; a miss
/// leaves the URL exactly as typed. The server matches, so the page never
/// needs anyone's accounts but the caller's own.
export async function bootAccount(): Promise<void> {
  const resp = await fetch('/api/accounts', { credentials: 'same-origin' })
  // Signed out on a private tracker: the list is not ours to see. The app
  // shows the sign-in screen and this runs again, cookie in hand, on return.
  if (resp.status === 401 || resp.status === 403) return
  if (!resp.ok) throw new Error(`/api/accounts -> HTTP ${resp.status}`)
  const data: AccountsResponse = await resp.json()
  all = data.accounts
  regions = data.regions
  canAdd = data.canAdd
  defaultSlug = data.default

  const { pathname, search, hash } = window.location
  if (pathname === '/') { resolution = { kind: 'index' }; return }

  const segments = pathname.split('/').map(decodeURIComponent)
  const first = segments[1] ?? ''
  const region = regions.find(r => r.code === first.toLowerCase())?.code
  const slug = region ? segments[2] ?? '' : first
  const rest = segments.slice(region ? 3 : 2)
  if (!slug) { resolution = { kind: 'unknownRoute', path: pathname }; return }

  const query = new URLSearchParams({ slug })
  if (region) query.set('region', region)
  const resolved = await fetch(`/api/accounts/resolve?${query}`, { credentials: 'same-origin' })
  if (resolved.status === 401 || resolved.status === 403) return
  if (resolved.status === 404) {
    const miss: ResolveMiss = await resolved.json().catch(() => ({ error: '', suggestion: null }))
    resolution = region
      ? { kind: 'unknownAccount', region, slug, suggestion: miss.suggestion }
      : { kind: 'unknownRoute', path: pathname }
    return
  }
  if (!resolved.ok) throw new Error(`/api/accounts/resolve -> HTTP ${resolved.status}`)

  const hit: ResolveResponse = await resolved.json()
  current = hit.account
  resolution = { kind: 'account' }
  // pathname is percent-encoded by the browser; the server's prefix may not be.
  const prefix = hit.canonical.split('/').map(s => encodeURIComponent(decodeURIComponent(s))).join('/')
  const tail = '/' + rest.map(encodeURIComponent).join('/')
  const canonical = `${prefix}${tail}${search}${hash}`
  if (canonical !== `${pathname}${search}${hash}`) window.history.replaceState(null, '', canonical)
}

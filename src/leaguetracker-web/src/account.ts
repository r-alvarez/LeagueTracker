// Which tracked account this page is about. op.gg-style: the URL names it
// as /{region}/{RiotId}/... (/euw/ImRA-87166/matches), the API lives under
// /api/a/{region}/{RiotId}, and switching accounts is a full navigation -
// the whole app is scoped.
export interface AccountInfo {
  id: string; slug: string; label: string; riotId: string; gameName: string; tagLine: string
  hideLp: boolean; platform: string; region: string; path: string; fromConfig: boolean
  owned: boolean; ownerUserId: string | null; mediaPublic: boolean; previousSlugs: string[]
  available: boolean; unavailable: string | null
}
export interface RegionInfo { code: string; label: string; platform: string }
export interface AccountsResponse {
  default: string; current: string; canAdd: boolean; regions: RegionInfo[]; accounts: AccountInfo[]
}

let current: AccountInfo = { id: '', slug: '', label: '', riotId: '', gameName: '', tagLine: '', hideLp: false, platform: '', region: '', path: '', fromConfig: true, owned: false, ownerUserId: null, mediaPublic: false, previousSlugs: [], available: true, unavailable: null }
let all: AccountInfo[] = []
let regions: RegionInfo[] = []
let canAdd = false

const pathOf = (a: AccountInfo) => `/${a.region}/${encodeURIComponent(a.slug)}`

export const account = {
  get current() { return current },
  get all() { return all },
  get regions() { return regions },
  get canAdd() { return canAdd },
  /// Route prefix the router mounts under.
  get basename() { return current.slug ? pathOf(current) : '' },
  /// Account-scoped calls (/api/status, /api/matches, ...) go under the
  /// current account's prefix; the global roots stay as they are.
  apiUrl(path: string) {
    const global = ['/api/a/', '/api/agent/', '/api/accounts', '/api/me', '/api/admin', '/api/auth', '/api/render/pending']
    return current.slug && path.startsWith('/api/') && !global.some(g => path.startsWith(g))
      ? `/api/a${pathOf(current)}${path.slice(4)}`
      : path
  },
  switchTo(slug: string) {
    const target = all.find(a => a.slug === slug)
    if (target) window.location.assign(`${pathOf(target)}/`)
  },
  goTo(a: AccountInfo) { window.location.assign(`${pathOf(a)}/`) },
}

/// Resolves the account from the URL before the app renders. Canonical is
/// /{region}/{slug}/...; a bare /{slug}/... (the first one-site build) or a
/// path with no account (/, /matches/...) is rewritten to the canonical
/// address - the server's current account (Host header on a legacy
/// hostname, else the default) when the URL names none.
export async function bootAccount(): Promise<void> {
  const resp = await fetch('/api/accounts')
  if (!resp.ok) throw new Error(`/api/accounts -> HTTP ${resp.status}`)
  const data: AccountsResponse = await resp.json()
  all = data.accounts
  regions = data.regions
  canAdd = data.canAdd

  const segments = window.location.pathname.split('/').map(decodeURIComponent)
  const first = segments[1] ?? ''
  const second = segments[2] ?? ''
  const isRegion = regions.some(r => r.code === first.toLowerCase())
  // A slug the account answered to before a rename still finds it - and the
  // URL is rewritten to the current one, like the API's 301.
  const bySlug = (s: string) => all.find(a => a.slug.toLowerCase() === s.toLowerCase())
    ?? all.find(a => a.previousSlugs.some(p => p.toLowerCase() === s.toLowerCase()))

  const canonical = isRegion ? bySlug(second) : undefined
  if (canonical && canonical.region === first.toLowerCase() && canonical.slug.toLowerCase() === second.toLowerCase()) { current = canonical; return }
  if (canonical && canonical.region === first.toLowerCase()) {
    current = canonical
    const rest = '/' + segments.slice(3).map(encodeURIComponent).join('/')
    window.history.replaceState(null, '', `${pathOf(canonical)}${rest}${window.location.search}${window.location.hash}`)
    return
  }

  // Legacy /{slug}/... or nothing: pick the account and rewrite the URL.
  const legacy = bySlug(first)
  const target = legacy ?? all.find(a => a.slug === data.current) ?? all.find(a => a.slug === data.default) ?? all[0]
  current = target
  const rest = legacy ? '/' + segments.slice(2).map(encodeURIComponent).join('/') : (window.location.pathname === '/' ? '/' : window.location.pathname)
  window.history.replaceState(null, '', `${pathOf(target)}${rest.startsWith('/') ? rest : '/' + rest}${window.location.search}${window.location.hash}`)
}

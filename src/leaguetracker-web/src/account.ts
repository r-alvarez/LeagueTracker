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

// What the URL turned out to name. Only 'account' mounts the app; the
// others are whole-page answers, because borrowing someone's dashboard for
// a path that never named them is how a typo used to land on the owner.
export type Resolution =
  | { kind: 'account' }
  | { kind: 'index' }
  | { kind: 'unknownAccount'; region: string; slug: string; suggestion: AccountInfo | null }
  | { kind: 'unknownRoute'; path: string }

let current: AccountInfo = { id: '', slug: '', label: '', riotId: '', gameName: '', tagLine: '', hideLp: false, platform: '', region: '', path: '', fromConfig: true, owned: false, ownerUserId: null, mediaPublic: false, previousSlugs: [], available: true, unavailable: null }
let all: AccountInfo[] = []
let regions: RegionInfo[] = []
let canAdd = false
let defaultSlug = ''
let resolution: Resolution = { kind: 'index' }

export const pathOf = (a: AccountInfo) => `/${a.region}/${encodeURIComponent(a.slug)}`

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
    const target = all.find(a => a.slug === slug)
    if (target) window.location.assign(`${pathOf(target)}/`)
  },
  goTo(a: AccountInfo) { window.location.assign(`${pathOf(a)}/`) },
}

/// Resolves the account from the URL before the app renders. Canonical is
/// /{region}/{slug}/...; a bare /{slug}/... (the first one-site build) and a
/// slug from before a rename are rewritten to the canonical address, like
/// the API's 301. "/" is the front page. Anything else resolves to nothing
/// and says so - the URL is left exactly as typed.
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
  const second = segments[2] ?? ''
  const isRegion = regions.some(r => r.code === first.toLowerCase())
  const bySlug = (s: string) => all.find(a => a.slug.toLowerCase() === s.toLowerCase())
    ?? all.find(a => a.previousSlugs.some(p => p.toLowerCase() === s.toLowerCase()))
  const mount = (target: AccountInfo, rest: string[]) => {
    current = target
    resolution = { kind: 'account' }
    const tail = '/' + rest.map(encodeURIComponent).join('/')
    const canonical = `${pathOf(target)}${tail}${search}${hash}`
    if (canonical !== `${pathname}${search}${hash}`) window.history.replaceState(null, '', canonical)
  }

  if (isRegion) {
    const region = first.toLowerCase()
    const found = second ? bySlug(second) : undefined
    if (found && found.region === region) { mount(found, segments.slice(3)); return }
    resolution = second
      ? { kind: 'unknownAccount', region, slug: second, suggestion: found ?? null }
      : { kind: 'unknownRoute', path: pathname }
    return
  }

  const legacy = bySlug(first)
  if (legacy) { mount(legacy, segments.slice(2)); return }
  resolution = { kind: 'unknownRoute', path: pathname }
}

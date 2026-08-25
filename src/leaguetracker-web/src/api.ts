import { account } from './account'
import { csrfHeaders } from './auth'
import type { AdminUsers, AgentKey, AnalyticsSummary, BuildVersion, ClaimInfo, InviteResult, JoinCodeInfo, MyAgents, ClipInfo, FullGameStatus, FundamentalsResponse, JobStatus, LensResponse, LiveGame, LpPerGame, LpPoint, MatchDetail, MatchFacets, MatchFilters, MatchPage, MatchReview, RenderQueueRow, ReviewVerdicts, Stats, StopLoss, StorageInfo, Status, VodStatus } from './types'

/// Every API call goes through here: account-scoped URL rewriting, the
/// session cookie, and the CSRF header on writes. Bare fetch() elsewhere is
/// a bug waiting to 403.
export function apiFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const method = (init.method ?? 'GET').toUpperCase()
  const headers = new Headers(init.headers)
  if (method !== 'GET' && method !== 'HEAD') for (const [k, v] of Object.entries(csrfHeaders)) headers.set(k, v)
  return fetch(account.apiUrl(url), { ...init, headers, credentials: 'same-origin' })
}

async function get<T>(url: string): Promise<T> {
  const resp = await apiFetch(url)
  if (!resp.ok) throw new Error(`${url} -> HTTP ${resp.status}`)
  return resp.json()
}

async function post<T>(url: string): Promise<T> {
  const resp = await apiFetch(url, { method: 'POST' })
  if (!resp.ok && resp.status !== 409) throw new Error(`${url} -> HTTP ${resp.status}`)
  return resp.json()
}

async function postJson<T>(url: string, body: unknown): Promise<T> {
  const resp = await apiFetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
  if (!resp.ok) throw new Error(await errorText(resp))
  return resp.json()
}

/// The server's own sentence when it has one ({error} from our endpoints,
/// {detail}/{title} from Results.Problem), else the bare status.
async function errorText(resp: Response): Promise<string> {
  const body = await resp.json().catch(() => null) as { error?: string; detail?: string; title?: string } | null
  return body?.error ?? body?.detail ?? body?.title ?? `HTTP ${resp.status}`
}

export const api = {
  status: () => get<Status>('/api/status'),
  matches: (page: number, pageSize: number, filters: MatchFilters = {}) => {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
    for (const [key, value] of Object.entries(filters)) if (value) params.set(key, value)
    return get<MatchPage>(`/api/matches?${params}`)
  },
  matchFacets: () => get<MatchFacets>('/api/matches/facets'),
  match: (id: string) => get<MatchDetail>(`/api/matches/${id}`),
  review: async (id: string): Promise<MatchReview | null> => {
    const r = await apiFetch(`/api/matches/${id}/review`)
    if (r.status === 204) return null   // no timeline for this game
    if (!r.ok) throw new Error(`/api/matches/${id}/review -> HTTP ${r.status}`)
    return r.json()
  },
  reviews: (ids: string[]) => get<ReviewVerdicts>(`/api/reviews?ids=${ids.join(',')}`),
  clips: (id: string) => get<ClipInfo[]>(`/api/matches/${id}/clips`),
  deleteClip: async (id: string, index: number) => { await apiFetch(`/api/matches/${id}/clips/${index}`, { method: 'DELETE' }) },
  renderQueue: () => get<RenderQueueRow[]>('/api/render/queue'),
  vodStatus: (id: string) => get<VodStatus>(`/api/matches/${id}/vod/status`),
  setVodLink: async (id: string, url: string): Promise<VodStatus> => {
    const r = await apiFetch(`/api/matches/${id}/vod/link`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url }),
    })
    if (!r.ok) throw new Error(`/api/matches/${id}/vod/link -> HTTP ${r.status}`)
    return r.json()
  },
  deleteVod: async (id: string) => { await apiFetch(`/api/matches/${id}/vod`, { method: 'DELETE' }) },
  fullGameStatus: (id: string) => get<FullGameStatus>(`/api/matches/${id}/fullgame/status`),
  requestFullGame: (id: string) => post<FullGameStatus>(`/api/matches/${id}/fullgame`),
  toggleFullGameKeep: (id: string) => post<FullGameStatus>(`/api/matches/${id}/fullgame/keep`),
  deleteFullGame: async (id: string) => { await apiFetch(`/api/matches/${id}/fullgame`, { method: 'DELETE' }) },
  retryRender: async (id: string, kind: 'clips' | 'full', keep = false) => { await apiFetch(`/api/render/${id}/retry?kind=${kind}${keep ? '&keep=true' : ''}`, { method: 'POST' }) },
  dismissRender: async (id: string, kind: 'clips' | 'full') => { await apiFetch(`/api/render/${id}/dismiss?kind=${kind}`, { method: 'POST' }) },
  saveSettings: async (settings: { mediaPublic?: boolean; hideLp?: boolean; displayName?: string }) => {
    const r = await apiFetch('/api/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settings) })
    if (!r.ok) throw new Error(`settings -> HTTP ${r.status}`)
    return r.json() as Promise<{ id: string; mediaPublic: boolean; hideLp: boolean; displayName: string }>
  },
  // My machines (/api/me): keys, live heartbeats, open join codes.
  myAgents: () => get<MyAgents>('/api/me/agents'),
  mintJoinCode: async (role: 'recorder' | 'renderer'): Promise<JoinCodeInfo & { pretty: string }> => {
    const r = await apiFetch(`/api/me/agents/join-code?role=${role}`, { method: 'POST' })
    if (!r.ok) throw new Error(`join-code -> HTTP ${r.status}`)
    return r.json()
  },
  agentAction: async (id: string, verb: 'approve' | 'revoke' | 'delete' | 'restart' | 'dismiss-error') => {
    await apiFetch(verb === 'delete' ? `/api/me/agents/${id}` : `/api/me/agents/${id}/${verb}`, { method: verb === 'delete' ? 'DELETE' : 'POST' })
  },
  dismissAgentError: async (id: string) => { await apiFetch(`/api/me/agents/${id}/dismiss-error`, { method: 'POST' }) },
  restartAgent: async (id: string) => { await apiFetch(`/api/me/agents/${id}/restart`, { method: 'POST' }) },
  requestAgentLog: async (id: string) => { await apiFetch(`/api/me/agents/${id}/sendlog`, { method: 'POST' }) },
  // Claiming a Riot account (profile-icon proof)
  myClaims: () => get<ClaimInfo[]>('/api/me/claims'),
  startClaim: async (accountId: string): Promise<ClaimInfo> => {
    const r = await apiFetch('/api/me/claims', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ accountId }) })
    const body = await r.json().catch(() => ({}))
    if (!r.ok) throw new Error(body.error ?? `claim -> HTTP ${r.status}`)
    return body
  },
  verifyClaim: async (id: string): Promise<{ claim: ClaimInfo; verified: boolean; error: string | null }> => {
    const r = await apiFetch(`/api/me/claims/${id}/verify`, { method: 'POST' })
    const body = await r.json().catch(() => ({}))
    if (!r.ok) throw new Error(body.error ?? `verify -> HTTP ${r.status}`)
    return body
  },
  // Admin
  adminAgents: () => get<{ latestVersion: string | null; keys: AgentKey[] }>('/api/admin/agents'),
  adminUsers: () => get<AdminUsers>('/api/admin/users'),
  // Errors from these carry the server's sentence (error / detail / title),
  // which the People card shows as is.
  adminInvite: (email: string, displayName: string | null) =>
    postJson<InviteResult>('/api/admin/users', { email, displayName }),
  adminReinvite: (id: string) => postJson<InviteResult>(`/api/admin/users/${id}/invite`, {}),
  adminInviteLink: (id: string) => postJson<{ url: string; expiresUtc: string }>(`/api/admin/users/${id}/invite-link`, {}),
  adminRemoveInvited: async (id: string) => {
    const r = await apiFetch(`/api/admin/users/${id}`, { method: 'DELETE' })
    if (!r.ok) throw new Error(await errorText(r))
  },
  adminSetUserAdmin: async (id: string, admin: boolean) => {
    const r = await apiFetch(`/api/admin/users/${id}/admin?admin=${admin}`, { method: 'POST' })
    if (!r.ok) throw new Error(await r.text())
  },
  adminSetUserName: async (id: string, displayName: string) => {
    const r = await apiFetch(`/api/admin/users/${id}/name`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ displayName }) })
    if (!r.ok) throw new Error(await errorText(r))
  },
  version: () => get<BuildVersion>('/api/version'),
  // actsFor replaces the machine's extra-accounts grant wholesale; null
  // leaves whatever it has.
  adminAssignAgent: async (id: string, ownerEmail: string | null, role: string | null, actsFor: string[] | null = null) => {
    const r = await apiFetch(`/api/admin/agents/${id}/assign`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ownerEmail, role, actsFor }) })
    if (!r.ok) throw new Error((await r.json().catch(() => ({})))?.error ?? `assign -> HTTP ${r.status}`)
  },
  adminSetAccountOwner: async (accountId: string, ownerEmail: string | null) => {
    const r = await apiFetch(`/api/admin/accounts/${accountId}/owner`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ownerEmail }) })
    if (!r.ok) throw new Error((await r.json().catch(() => ({})))?.error ?? `owner -> HTTP ${r.status}`)
  },
  storage: () => get<StorageInfo>('/api/storage'),
  lens: async (opts: { window?: number; days?: number; role?: string }): Promise<LensResponse | null> => {
    const params = new URLSearchParams()
    if (opts.window) params.set('window', String(opts.window))
    if (opts.days) params.set('days', String(opts.days))
    if (opts.role) params.set('role', opts.role)
    const r = await apiFetch(`/api/lens?${params}`)
    if (r.status === 204) return null   // not enough games yet (for this role/window)
    if (!r.ok) throw new Error(`/api/lens -> HTTP ${r.status}`)
    return r.json()
  },
  fundamentals: async (opts: { window?: number; days?: number; role?: string }): Promise<FundamentalsResponse | null> => {
    const params = new URLSearchParams()
    if (opts.window) params.set('window', String(opts.window))
    if (opts.days) params.set('days', String(opts.days))
    if (opts.role) params.set('role', opts.role)
    const r = await apiFetch(`/api/fundamentals?${params}`)
    if (r.status === 204) return null   // not enough games yet (for this role/window)
    if (!r.ok) throw new Error(`/api/fundamentals -> HTTP ${r.status}`)
    return r.json()
  },
  lpHistory: (queue: string) => get<LpPoint[]>(`/api/lp/history?queue=${encodeURIComponent(queue)}`),
  lpPerGame: () => get<LpPerGame[]>('/api/lp/per-game'),
  stopLoss: () => get<StopLoss>('/api/stoploss'),
  jobStatus: () => get<JobStatus>('/api/jobs/status'),
  // No params = the whole thing: pages Riot's match list until it runs dry, all queues.
  syncHistory: () => post<JobStatus>('/api/sync/history'),
  importFolder: (path: string) => post<JobStatus>(`/api/import?path=${encodeURIComponent(path)}`),
  analytics: (lastN: number) => get<AnalyticsSummary>(`/api/analytics/summary?lastN=${lastN}`),
  live: async (): Promise<LiveGame | null> => {
    const r = await apiFetch('/api/live')
    if (r.status === 204) return null   // not in a game
    if (!r.ok) throw new Error(`/api/live -> HTTP ${r.status}`)
    return r.json()
  },
  stats: (opts: { days?: number; lastGames?: number }) => {
    const params = new URLSearchParams()
    if (opts.days) params.set('days', String(opts.days))
    if (opts.lastGames) params.set('lastGames', String(opts.lastGames))
    const qs = params.toString()
    return get<Stats>(`/api/stats${qs ? `?${qs}` : ''}`)
  },
  reprocess: () => post<JobStatus>('/api/analytics/reprocess'),
}

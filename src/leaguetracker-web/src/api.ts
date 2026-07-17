import type { AnalyticsSummary, ChallengeBenchmark, ClipInfo, FullGameStatus, FundamentalsResponse, JobStatus, LensResponse, LiveGame, LpPerGame, LpPoint, MatchDetail, MatchFacets, MatchFilters, MatchPage, MatchReview, RenderQueueRow, ReviewVerdicts, Stats, StorageInfo, Status } from './types'

async function get<T>(url: string): Promise<T> {
  const resp = await fetch(url)
  if (!resp.ok) throw new Error(`${url} -> HTTP ${resp.status}`)
  return resp.json()
}

async function post<T>(url: string): Promise<T> {
  const resp = await fetch(url, { method: 'POST' })
  if (!resp.ok && resp.status !== 409) throw new Error(`${url} -> HTTP ${resp.status}`)
  return resp.json()
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
    const r = await fetch(`/api/matches/${id}/review`)
    if (r.status === 204) return null   // no timeline for this game
    if (!r.ok) throw new Error(`/api/matches/${id}/review -> HTTP ${r.status}`)
    return r.json()
  },
  reviews: (ids: string[]) => get<ReviewVerdicts>(`/api/reviews?ids=${ids.join(',')}`),
  clips: (id: string) => get<ClipInfo[]>(`/api/matches/${id}/clips`),
  deleteClip: async (id: string, index: number) => { await fetch(`/api/matches/${id}/clips/${index}`, { method: 'DELETE' }) },
  renderQueue: () => get<RenderQueueRow[]>('/api/render/queue'),
  fullGameStatus: (id: string) => get<FullGameStatus>(`/api/matches/${id}/fullgame/status`),
  requestFullGame: (id: string) => post<FullGameStatus>(`/api/matches/${id}/fullgame`),
  toggleFullGameKeep: (id: string) => post<FullGameStatus>(`/api/matches/${id}/fullgame/keep`),
  deleteFullGame: async (id: string) => { await fetch(`/api/matches/${id}/fullgame`, { method: 'DELETE' }) },
  retryRender: async (id: string, kind: 'clips' | 'full') => { await fetch(`/api/render/${id}/retry?kind=${kind}`, { method: 'POST' }) },
  storage: () => get<StorageInfo>('/api/storage'),
  lens: async (opts: { window?: number; days?: number; role?: string }): Promise<LensResponse | null> => {
    const params = new URLSearchParams()
    if (opts.window) params.set('window', String(opts.window))
    if (opts.days) params.set('days', String(opts.days))
    if (opts.role) params.set('role', opts.role)
    const r = await fetch(`/api/lens?${params}`)
    if (r.status === 204) return null   // not enough games yet (for this role/window)
    if (!r.ok) throw new Error(`/api/lens -> HTTP ${r.status}`)
    return r.json()
  },
  fundamentals: async (opts: { window?: number; days?: number; role?: string }): Promise<FundamentalsResponse | null> => {
    const params = new URLSearchParams()
    if (opts.window) params.set('window', String(opts.window))
    if (opts.days) params.set('days', String(opts.days))
    if (opts.role) params.set('role', opts.role)
    const r = await fetch(`/api/fundamentals?${params}`)
    if (r.status === 204) return null   // not enough games yet (for this role/window)
    if (!r.ok) throw new Error(`/api/fundamentals -> HTTP ${r.status}`)
    return r.json()
  },
  lpHistory: (queue: string) => get<LpPoint[]>(`/api/lp/history?queue=${encodeURIComponent(queue)}`),
  lpPerGame: () => get<LpPerGame[]>('/api/lp/per-game'),
  jobStatus: () => get<JobStatus>('/api/jobs/status'),
  // No params = the whole thing: pages Riot's match list until it runs dry, all queues.
  syncHistory: () => post<JobStatus>('/api/sync/history'),
  importFolder: (path: string) => post<JobStatus>(`/api/import?path=${encodeURIComponent(path)}`),
  analytics: (lastN: number) => get<AnalyticsSummary>(`/api/analytics/summary?lastN=${lastN}`),
  challengePercentiles: async (): Promise<ChallengeBenchmark | null> => {
    const r = await fetch('/api/challenges/percentiles')
    if (r.status === 204) return null   // not fetched from Riot yet (or the fetch failed with nothing cached)
    if (!r.ok) throw new Error(`/api/challenges/percentiles -> HTTP ${r.status}`)
    return r.json()
  },
  live: async (): Promise<LiveGame | null> => {
    const r = await fetch('/api/live')
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

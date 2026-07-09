import type { AnalyticsSummary, ChallengeBenchmark, ClipInfo, FullGameStatus, JobStatus, LensResponse, LiveGame, LpPerGame, LpPoint, MatchDetail, MatchPage, RenderQueueRow, Stats, StorageInfo, Status } from './types'

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
  matches: (page: number, pageSize: number, ranked?: boolean) =>
    get<MatchPage>(`/api/matches?page=${page}&pageSize=${pageSize}${ranked === undefined ? '' : `&ranked=${ranked}`}`),
  match: (id: string) => get<MatchDetail>(`/api/matches/${id}`),
  clips: (id: string) => get<ClipInfo[]>(`/api/matches/${id}/clips`),
  renderQueue: () => get<RenderQueueRow[]>('/api/render/queue'),
  fullGameStatus: (id: string) => get<FullGameStatus>(`/api/matches/${id}/fullgame/status`),
  requestFullGame: (id: string) => post<FullGameStatus>(`/api/matches/${id}/fullgame`),
  toggleFullGameKeep: (id: string) => post<FullGameStatus>(`/api/matches/${id}/fullgame/keep`),
  deleteFullGame: async (id: string) => { await fetch(`/api/matches/${id}/fullgame`, { method: 'DELETE' }) },
  retryRender: async (id: string, kind: 'clips' | 'full') => { await fetch(`/api/render/${id}/retry?kind=${kind}`, { method: 'POST' }) },
  storage: () => get<StorageInfo>('/api/storage'),
  lens: async (window: number, role: string): Promise<LensResponse | null> => {
    const r = await fetch(`/api/lens?window=${window}${role ? `&role=${role}` : ''}`)
    if (r.status === 204) return null   // not enough games yet (for this role)
    if (!r.ok) throw new Error(`/api/lens -> HTTP ${r.status}`)
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

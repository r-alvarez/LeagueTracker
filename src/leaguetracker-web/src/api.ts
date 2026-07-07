import type { AnalyticsSummary, JobStatus, LpPerGame, LpPoint, MatchDetail, MatchPage, Stats, Status } from './types'

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
  lpHistory: (queue: string) => get<LpPoint[]>(`/api/lp/history?queue=${encodeURIComponent(queue)}`),
  lpPerGame: () => get<LpPerGame[]>('/api/lp/per-game'),
  jobStatus: () => get<JobStatus>('/api/jobs/status'),
  syncHistory: (rankedTarget: number) => post<JobStatus>(`/api/sync/history?rankedTarget=${rankedTarget}`),
  importFolder: (path: string) => post<JobStatus>(`/api/import?path=${encodeURIComponent(path)}`),
  analytics: (lastN: number) => get<AnalyticsSummary>(`/api/analytics/summary?lastN=${lastN}`),
  stats: (opts: { days?: number; lastGames?: number }) => {
    const params = new URLSearchParams()
    if (opts.days) params.set('days', String(opts.days))
    if (opts.lastGames) params.set('lastGames', String(opts.lastGames))
    const qs = params.toString()
    return get<Stats>(`/api/stats${qs ? `?${qs}` : ''}`)
  },
  reprocess: () => post<JobStatus>('/api/analytics/reprocess'),
}

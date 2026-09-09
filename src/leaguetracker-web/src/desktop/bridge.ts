import { setReviewTransport } from '../api'
import { account } from '../account'

export interface ReviewAccount { id: string; accountId: string; region: string; slug: string; label: string; riotId: string }
export interface Recording { id: string; name: string; matchId: string | null; player: string | null; recordedUtc: string; durationSec: number; sizeBytes: number; available: boolean; pinned: boolean; published: boolean }
export interface LibrarySettings { keepGames: number; maxGb: number; minFreeGb: number; keepAll: boolean }
export interface Library { recordings: Recording[]; settings: LibrarySettings; freeGb: number }
export interface Reply { status: number; body: string; cached: boolean; offline: boolean; savedUtc: string }
interface WebView {
  postMessage(message: unknown): void
  addEventListener(name: 'message', listener: (event: MessageEvent) => void): void
}
const webview = (window as unknown as { chrome?: { webview?: WebView } }).chrome?.webview
const pending = new Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> }>()
let sequence = 0
webview?.addEventListener('message', event => {
  if (event.data.activation) { window.dispatchEvent(new CustomEvent('review-activation', { detail: event.data.activation })); return }
  const request = pending.get(event.data.id)
  if (!request) return
  pending.delete(event.data.id)
  clearTimeout(request.timer)
  if (event.data.error) request.reject(new Error(event.data.error))
  else request.resolve(event.data.result)
})

export function invoke<T>(operation: string, argument: unknown = {}): Promise<T> {
  return new Promise((resolve, reject) => {
    if (!webview) { reject(new Error('Open gameplay review from the LeagueTracker agent.')); return }
    const id = String(++sequence)
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('The request took too long. Try again.')) }, 45000)
    pending.set(id, { resolve: value => resolve(value as T), reject, timer })
    webview.postMessage({ id, operation, argument })
  })
}

export function selectAccount(selected: ReviewAccount, recording: Recording | null) {
  account.useReviewAccount({ ...selected, id: selected.accountId })
  setReviewTransport(async (url, init) => {
    if (init.method && !['GET', 'HEAD'].includes(init.method.toUpperCase())) throw new Error('Manage your online account on the website.')
    if (!url.startsWith('/api/')) throw new Error('Unsupported review request.')
    const reply = await invoke<Reply>('get', { account: selected.id, path: url.slice(4), recording: recording?.id })
    if (reply.offline) window.dispatchEvent(new CustomEvent('review-cached', { detail: reply.savedUtc }))
    return new Response(reply.status === 204 ? null : reply.body, { status: reply.status, headers: { 'Content-Type': 'application/json' } })
  })
}

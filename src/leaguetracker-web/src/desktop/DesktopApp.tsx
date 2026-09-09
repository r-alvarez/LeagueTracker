import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import { Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import type { MatchPage, MatchSummary, VodApm, VodStatus } from '../types'
import { invoke, selectAccount, type Library, type LibrarySettings, type Recording, type Reply, type ReviewAccount } from './bridge'

const MatchDetail = lazy(() => import('../pages/MatchDetail'))
const FootageView = lazy(() => import('../components/FootageView'))

const gb = (bytes: number) => `${(bytes / 1024 ** 3).toFixed(1)} GB`
const when = (date: string) => new Date(date).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })

function LocalPlayer({ recording, selected, showAnalysis }: { recording: Recording; selected: ReviewAccount | null; showAnalysis: () => void }) {
  const [vod, setVod] = useState<VodStatus | null>(null)
  const [ready, setReady] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [checking, setChecking] = useState(false)
  const [stale, setStale] = useState(false)
  const generation = useRef(0)
  useEffect(() => {
    let live = true
    void invoke<VodStatus>('localVod', { id: recording.id }).then(value => {
      if (!live) return
      setVod(value)
      void invoke<VodApm | null>('localApm', { id: recording.id }).then(apm => { if (live) setVod(v => v && { ...v, apm }) }).catch(() => undefined)
    }).catch(e => { if (live) setError(String(e)) })
    return () => { live = false }
  }, [recording.id])
  const check = useCallback(async (refresh = false) => {
    const version = ++generation.current
    setReady(false)
    if (!selected || !recording.matchId) return
    setChecking(true)
    try {
      const result = await invoke<Reply>('get', { account: selected.id, path: `/matches/${recording.matchId}`, refresh })
      if (version !== generation.current) return
      setReady(result.status === 200)
      setStale(result.cached)
    } catch { /* The recording remains playable independently. */ }
    finally { if (version === generation.current) setChecking(false) }
  }, [selected, recording.matchId])
  useEffect(() => { void check() }, [check])
  return <section>
    <div className="review-heading"><div><p className="eyebrow">Recorded on this PC</p><h1>{recording.name}</h1><p className="mut">{when(recording.recordedUtc)} · {gb(recording.sizeBytes)}</p></div></div>
    {error && <p role="alert" className="review-notice">{error}</p>}
    {vod ? <div className="card"><FootageView matchId={recording.matchId ?? recording.id} vod={vod} onVodChange={setVod}
      fullGame={null} onFullGameChange={() => undefined} canManage={false} moment={null} moments={[]} durationSec={recording.durationSec}
      seekKey={0} active onJump={() => undefined} /></div> : <p className="mut">Opening recording…</p>}
    <div className="review-notice">
      {ready ? <><span>{stale ? 'Saved analysis is available.' : 'Your gameplay analysis is ready.'}</span><button className="action primary" onClick={showAnalysis}>Review moments & metrics</button></>
        : <><span>{selected ? 'You can watch now. Analysis will be available after this match syncs.' : 'Choose your account above to add moments and metrics.'}</span>
          {selected && <button className="action" disabled={checking} onClick={() => void check(true)}>{checking ? 'Checking…' : 'Check analysis'}</button>}</>}
    </div>
  </section>
}

function StorageSettings({ value, save, close }: { value: LibrarySettings; save: (value: LibrarySettings) => Promise<void>; close: () => void }) {
  const [draft, setDraft] = useState(value)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  return <form className="card review-settings" onSubmit={e => { e.preventDefault(); setBusy(true); setError(null); void save(draft).then(close).catch(e => setError(String(e))).finally(() => setBusy(false)) }}>
    <h2>Your recording library</h2>
    <p className="mut">Older recordings rotate within these limits. Pinned games stay. If protected files leave too little space, recording pauses instead of deleting them.</p>
    <label>Recent games <input type="number" min={1} max={500} required value={draft.keepGames} onChange={e => setDraft({ ...draft, keepGames: Number(e.target.value) })} /></label>
    <label>Recording budget (GB) <input type="number" min={1} max={2000} required value={draft.maxGb} onChange={e => setDraft({ ...draft, maxGb: Number(e.target.value) })} /></label>
    <label>Keep this much drive space free (GB) <input type="number" min={1} max={500} required value={draft.minFreeGb} onChange={e => setDraft({ ...draft, minFreeGb: Number(e.target.value) })} /></label>
    <label className="review-check"><input type="checkbox" checked={draft.keepAll} onChange={e => setDraft({ ...draft, keepAll: e.target.checked })} />Keep all recordings until I delete them</label>
    {draft.keepAll && <p className="mut">The game count and recording budget are disabled. The free-space requirement still applies.</p>}
    {error && <p role="alert">{error}</p>}
    <div className="review-actions"><button type="submit" className="action primary" disabled={busy}>Save</button><button type="button" className="action" onClick={close}>Cancel</button></div>
  </form>
}

export default function DesktopApp({ openLast }: { openLast: boolean }) {
  const navigate = useNavigate()
  const location = useLocation()
  useEffect(() => { if (location.pathname === '/matches' || location.pathname === '/') void invoke('releasePlayback').catch(() => undefined) }, [location.pathname])
  const [library, setLibrary] = useState<Library | null>(null)
  const [accounts, setAccounts] = useState<ReviewAccount[]>([])
  const [selected, setSelected] = useState<ReviewAccount | null>(null)
  const [recording, setRecording] = useState<Recording | null>(null)
  const [matches, setMatches] = useState<MatchSummary[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const [offline, setOffline] = useState(false)
  const [busy, setBusy] = useState(false)
  const lastHandled = useRef(false)
  const requests = useRef(0)
  const refreshHistory = useRef(false)
  const reload = useCallback(async () => { const value = await invoke<Library>('library'); setLibrary(value); return value }, [])
  const openLocal = useCallback((r: Recording) => { setRecording(r); navigate('/local'); }, [navigate])
  useEffect(() => {
    void reload().then(value => {
      if (openLast && !lastHandled.current) { lastHandled.current = true; const first = value.recordings.find(r => r.available); if (first) openLocal(first) }
    }).catch(e => setNotice(String(e)))
    void invoke<{ accounts: ReviewAccount[]; offline: boolean; denied: boolean }>('accounts').then(value => {
      setAccounts(value.accounts); setSelected(value.accounts[0] ?? null); setOffline(value.offline)
      if (value.denied) setNotice('This machine needs access to your tracker. Check enrollment in the agent’s Settings.')
    }).catch(() => setOffline(true))
  }, [reload, openLast, openLocal])

  useEffect(() => {
    const handler = () => setOffline(true)
    window.addEventListener('review-cached', handler)
    return () => window.removeEventListener('review-cached', handler)
  }, [])
  useEffect(() => {
    const handler = (event: Event) => {
      if ((event as CustomEvent).detail === 'last') void reload().then(value => { const first = value.recordings.find(r => r.available); if (first) openLocal(first) })
    }
    window.addEventListener('review-activation', handler)
    return () => window.removeEventListener('review-activation', handler)
  }, [reload, openLocal])

  const loadMatches = useCallback(async (a: ReviewAccount, number: number, refresh = false) => {
    const version = ++requests.current
    setBusy(true)
    try {
      const result = await invoke<Reply>('get', { account: a.id, path: `/matches?page=${number}&pageSize=20`, refresh })
      if (requests.current !== version) return
      if (result.status !== 200) throw new Error('Match history is unavailable. Your local recordings are still here.')
      const data = JSON.parse(result.body) as MatchPage
      setMatches(previous => number === 1 ? data.items : [...previous, ...data.items]); setTotal(data.total); setPage(number)
      setOffline(result.offline)
    } catch (e) { if (requests.current === version) setNotice(String(e)) }
    finally { if (requests.current === version) setBusy(false) }
  }, [])
  useEffect(() => {
    setMatches([]); setTotal(0); setPage(1)
    requests.current++
    const force = refreshHistory.current
    refreshHistory.current = false
    if (selected) { selectAccount(selected, null); void loadMatches(selected, 1, force) }
  }, [selected, loadMatches])

  const showMatch = (matchId: string, local: Recording | null) => {
    if (!selected) return
    selectAccount(selected, local); setRecording(local)
    navigate(`/matches/${matchId}`)
  }
  const action = async (operation: string, argument: unknown) => {
    setNotice(null)
    try { await invoke(operation, argument); await reload() } catch (e) { setNotice(String(e)) }
  }
  const changeAccount = (id: string) => { setSelected(accounts.find(a => a.id === id) ?? null); setRecording(null); navigate('/matches') }
  const refresh = async () => {
    setNotice(null)
    await reload()
    const discovery = await invoke<{ accounts: ReviewAccount[]; offline: boolean; denied: boolean }>('accounts', { refresh: true })
    setAccounts(discovery.accounts); setOffline(discovery.offline)
    const current = discovery.accounts.find(a => a.id === selected?.id) ?? discovery.accounts[0] ?? null
    refreshHistory.current = true
    setSelected(current)
    if (discovery.denied) { navigate('/matches'); setNotice('Tracker access was refused. Check the agent’s enrollment.') }
  }
  const available = library?.recordings.filter(r => r.available) ?? []
  const visible = available.filter(r => `${r.name} ${r.player ?? ''}`.toLowerCase().includes(query.toLowerCase()))
  return <div className="desktop-shell">
    <header className="desktop-bar"><button className="desktop-brand" onClick={() => navigate('/matches')}><img src="/favicon.svg" alt="" />LeagueTracker</button>
      <div className="desktop-account">{accounts.length > 0 && <select aria-label="Review account" value={selected?.id ?? ''} onChange={e => changeAccount(e.target.value)}>{accounts.map(a => <option key={a.id} value={a.id}>{a.label}</option>)}</select>}
        <button className="action" onClick={() => setSettingsOpen(s => !s)}>Storage</button>
        {selected && <button className="action" onClick={() => void action('openWebsite', { account: selected.id })}>Open website ↗</button>}</div>
    </header>
    <main className="desktop-main">
      {notice && <div className="review-notice" role="alert">{notice}<button className="action" onClick={() => setNotice(null)}>Dismiss</button></div>}
      {offline && <p className="review-offline">Showing saved information. Local recordings play without a connection.</p>}
      {settingsOpen && library && <StorageSettings value={library.settings} close={() => setSettingsOpen(false)} save={async value => { await invoke('settings', value); await reload() }} />}
      <Suspense fallback={<p className="mut">Opening review…</p>}><Routes>
        <Route path="/local" element={recording ? <><button className="action review-back" onClick={() => navigate('/matches')}>← Library</button><LocalPlayer key={recording.id} recording={recording} selected={selected} showAnalysis={() => recording.matchId && showMatch(recording.matchId, recording)} /></> : <p>Choose a recording in your library.</p>} />
        <Route path="/matches/:id" element={<MatchDetail key={`${selected?.id}:${location.pathname}`} />} />
        <Route path="*" element={<>
          <div className="review-heading"><div><p className="eyebrow">Your gameplay</p><h1>Review. Learn. Play again.</h1><p className="mut">Your recordings and the moments worth another look.</p></div>
            <button className="action" disabled={busy} onClick={() => void refresh().catch(e => setNotice(String(e)))}>{busy ? 'Refreshing…' : 'Refresh'}</button></div>
          <div className="review-library-heading"><h2>On this PC <span className="mut">{available.length}</span></h2><input className="review-search" aria-label="Find a recording" placeholder="Find a recording…" value={query} onChange={e => setQuery(e.target.value)} /></div>
          {library && <p className="mut sm-text">{gb(available.reduce((n, r) => n + r.sizeBytes, 0))} of recordings · {library.freeGb.toFixed(0)} GB free · {library.settings.keepAll ? 'Keep all' : `Up to ${library.settings.keepGames} games within ${library.settings.maxGb} GB`}</p>}
          {visible.length === 0 && <div className="card review-empty"><h3>{query ? 'No recordings match your search' : 'Your next game belongs here'}</h3><p className="mut">Finished recordings appear here automatically. Previously removed videos may still be available in your match history below.</p></div>}
          <div className="recording-grid">{visible.map(r => <article className="card recording-card" key={r.id}>
            <button className="recording-open" onClick={() => openLocal(r)}><span className="recording-play" aria-hidden>▶</span><span className="recording-title">{r.name}</span><span className="mut">{when(r.recordedUtc)} · {Math.round(r.durationSec / 60)} min · {gb(r.sizeBytes)}</span></button>
            <div className="recording-actions"><button className={`action${r.pinned ? ' primary' : ''}`} onClick={() => void action('pin', { id: r.id, pinned: !r.pinned })}>{r.pinned ? 'Pinned' : 'Pin'}</button>
              <button className="action" disabled={r.pinned} onClick={() => {
                if (window.confirm(r.published ? 'Delete this local video? Its online copy and analysis remain.' : 'This may be your only copy. Delete this local video?')) void action('delete', { id: r.id, confirm: true })
              }}>Delete</button><span className="mut sm-text">{r.published ? 'Published' : 'Local copy'}</span></div>
          </article>)}</div>
          <div className="review-library-heading"><h2>Match history</h2><span className="mut">{selected?.label ?? 'Connect your tracker to see analysis'}</span></div>
          <div className="review-matches">{matches.map(m => <button className="review-match card" key={m.id} onClick={() => showMatch(m.id, available.find(r => r.matchId === m.id && (!r.player || r.player === selected?.riotId)) ?? null)}>
            <span className={m.win ? 'win' : 'loss'}>{m.win ? 'Victory' : 'Defeat'}</span><strong>{m.champion}</strong><span>{m.kills}/{m.deaths}/{m.assists}</span><span className="mut">{m.queueName}</span><span className="mut">{when(m.gameEndUtc)}</span><span aria-hidden>→</span>
          </button>)}</div>
          {matches.length < total && selected && <button className="action" disabled={busy} onClick={() => void loadMatches(selected, page + 1)}>Load more games</button>}
        </>} />
      </Routes></Suspense>
    </main>
  </div>
}

import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import type { MatchPage, MatchSummary, VodApm, VodStatus } from '../types'
import { useChampionIcons } from '../champions'
import Loadout from '../components/Loadout'
import { invoke, selectAccount, type Library, type LibrarySettings, type Recording, type Reply, type ReviewAccount } from './bridge'

const MatchDetail = lazy(() => import('../pages/MatchDetail'))
const FootageView = lazy(() => import('../components/FootageView'))

const gb = (bytes: number) => `${(bytes / 1024 ** 3).toFixed(1)} GB`
const when = (date: string) => new Date(date).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
const shortQueue = (queue: string) => queue.replace(/^Ranked\s+/, '').replace(/^Normal\s+/, '')

interface RecordingContext { account: ReviewAccount; match: MatchSummary }

function RecordingCard({ recording, context, open, act }: {
  recording: Recording
  context: RecordingContext | undefined
  open: () => void
  act: (operation: string, argument: unknown) => Promise<void>
}) {
  const championIcon = useChampionIcons()(context?.match.champion ?? '')
  const match = context?.match
  return <article className={`card recording-card${match ? match.win ? ' recording-win' : ' recording-loss' : ''}`}>
    <button className="recording-preview" onClick={open} aria-label={`Preview ${recording.name}`}>
      {recording.thumbnailUrl
        ? <img src={recording.thumbnailUrl} alt="" loading="lazy" />
        : <span className="recording-preview-empty" />}
      <span className="recording-preview-shade" />
      <span className="recording-play" aria-hidden>▶</span>
      <span className="recording-preview-label">Preview</span>
    </button>
    <div className="recording-summary">
      <div className="recording-kicker">
        {match && <span className={match.win ? 'win' : 'loss'}>{match.win ? 'Victory' : 'Defeat'}</span>}
        <span>{context?.account.label ?? recording.player ?? 'Recorded game'}</span>
        {match && <span>{shortQueue(match.queueName)} · {match.durationMin.toFixed(0)}m</span>}
      </div>
      {match ? <div className="recording-game">
        <span className="recording-champion">
          {championIcon ? <img src={championIcon} alt={match.champion} loading="lazy" /> : <span className="champ-mono">{match.champion.slice(0, 2).toUpperCase()}</span>}
          <span><strong>{match.champion}</strong>{match.opponentChampion && <small>vs {match.opponentChampion}</small>}</span>
        </span>
        <span className="recording-kda"><strong>{match.kills}/{match.deaths}/{match.assists}</strong><small>{match.kda} KDA</small></span>
        <Loadout items={match.items} summoner1Id={match.summoner1Id} summoner2Id={match.summoner2Id} />
      </div> : <p className="recording-awaiting">Match details will appear when this game is available from your tracker.</p>}
      <div className="recording-file"><span>{when(recording.recordedUtc)} · {Math.round(recording.durationSec / 60)} min · {gb(recording.sizeBytes)}</span><span title={recording.name}>{recording.name}</span></div>
    </div>
    <div className="recording-actions"><button className="action primary" onClick={open}>Preview</button><button className={`action${recording.pinned ? ' primary' : ''}`} onClick={() => void act('pin', { id: recording.id, pinned: !recording.pinned })}>{recording.pinned ? 'Pinned' : 'Pin'}</button>
      <button className="action" disabled={recording.pinned} onClick={() => {
        if (window.confirm(recording.published ? 'Delete this local video? Its online copy and analysis remain.' : 'This may be your only copy. Delete this local video?')) void act('delete', { id: recording.id, confirm: true })
      }}>Delete</button><span className="mut sm-text">{recording.published ? 'Published' : 'Local copy'}</span></div>
  </article>
}

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

function StorageSettings({ library, recordings, save, close }: { library: Library; recordings: Recording[]; save: (value: LibrarySettings) => Promise<void>; close: () => void }) {
  const [draft, setDraft] = useState(library.settings)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') close() }
    window.addEventListener('keydown', escape)
    return () => window.removeEventListener('keydown', escape)
  }, [close])
  const available = recordings.filter(r => r.available)
  const gameCount = new Set(recordings.map(r => r.matchId).filter(Boolean)).size || recordings.length
  const used = available.reduce((sum, r) => sum + r.sizeBytes, 0)
  return <div className="review-settings-backdrop" role="presentation" onMouseDown={e => { if (e.target === e.currentTarget) close() }}>
    <form className="card review-settings" role="dialog" aria-modal="true" aria-labelledby="storage-title" onSubmit={e => { e.preventDefault(); setBusy(true); setError(null); void save(draft).then(close).catch(e => setError(String(e))).finally(() => setBusy(false)) }}>
      <div className="review-settings-head"><div><p className="eyebrow">On this PC</p><h2 id="storage-title">Recording storage</h2><p className="mut">Choose how much gameplay stays immediately playable. Match history and published analysis remain after an older local video rotates out.</p></div><button type="button" className="review-close" aria-label="Close storage settings" onClick={close}>×</button></div>
      <div className="storage-stats">
        <div><strong>{gameCount}</strong><span>games recorded here</span></div>
        <div><strong>{available.length}</strong><span>playable recordings</span></div>
        <div><strong>{gb(used)}</strong><span>used by recordings</span></div>
        <div><strong>{library.freeGb.toFixed(0)} GB</strong><span>drive space free</span></div>
      </div>
      <div className="storage-fields">
        <label><span><strong>Recent games</strong><small>Maximum playable recordings to retain</small></span><input type="number" min={1} max={500} required disabled={draft.keepAll} value={draft.keepGames} onChange={e => setDraft({ ...draft, keepGames: Number(e.target.value) })} /></label>
        <label><span><strong>Recording budget</strong><small>Maximum disk space for playable videos</small></span><span className="storage-unit"><input type="number" min={1} max={2000} required disabled={draft.keepAll} value={draft.maxGb} onChange={e => setDraft({ ...draft, maxGb: Number(e.target.value) })} /> GB</span></label>
        <label><span><strong>Drive space reserve</strong><small>Recording pauses before free space falls below this</small></span><span className="storage-unit"><input type="number" min={1} max={500} required value={draft.minFreeGb} onChange={e => setDraft({ ...draft, minFreeGb: Number(e.target.value) })} /> GB</span></label>
      </div>
      <label className="storage-keep"><input type="checkbox" checked={draft.keepAll} onChange={e => setDraft({ ...draft, keepAll: e.target.checked })} /><span><strong>Keep every recording until I delete it</strong><small>Disables the game-count and recording-budget limits. The drive-space reserve still applies.</small></span></label>
      {error && <p className="review-settings-error" role="alert">{error}</p>}
      <div className="review-settings-actions"><button type="button" className="action" onClick={close}>Cancel</button><button type="submit" className="action primary" disabled={busy}>{busy ? 'Saving…' : 'Save storage settings'}</button></div>
    </form>
  </div>
}

export default function DesktopApp({ openLast }: { openLast: boolean }) {
  const navigate = useNavigate()
  const location = useLocation()
  useEffect(() => { if (location.pathname === '/matches' || location.pathname === '/') void invoke('releasePlayback').catch(() => undefined) }, [location.pathname])
  const [library, setLibrary] = useState<Library | null>(null)
  const [accounts, setAccounts] = useState<ReviewAccount[]>([])
  const [selected, setSelected] = useState<ReviewAccount | null>(null)
  const [recordingContexts, setRecordingContexts] = useState<Record<string, RecordingContext>>({})
  const [recording, setRecording] = useState<Recording | null>(null)
  const [matches, setMatches] = useState<MatchSummary[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const [offline, setOffline] = useState(false)
  const [busy, setBusy] = useState(false)
  const lastRequested = useRef(openLast)
  const requests = useRef(0)
  const refreshHistory = useRef(false)
  const reload = useCallback(async () => { const value = await invoke<Library>('library'); setLibrary(value); return value }, [])
  const openLocal = useCallback((r: Recording, owner?: ReviewAccount) => {
    if (owner) setSelected(owner)
    setRecording(r)
    navigate('/local')
  }, [navigate])
  useEffect(() => {
    void reload().catch(e => setNotice(String(e)))
    void invoke<{ accounts: ReviewAccount[]; offline: boolean; denied: boolean }>('accounts').then(value => {
      setRecordingContexts({})
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
      if ((event as CustomEvent).detail === 'last') { lastRequested.current = true; void reload() }
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

  // Match ids survive Riot ID changes. Walk each of this machine's account
  // histories in pages, then attach the real champion, result and loadout to
  // every playable local recording instead of guessing from its filename.
  useEffect(() => {
    const wanted = new Set((library?.recordings ?? []).filter(r => r.matchId).map(r => r.matchId!))
    if (wanted.size === 0 || accounts.length === 0) return
    let cancelled = false
    const discover = async (reviewAccount: ReviewAccount) => {
      const found: Array<[string, RecordingContext]> = []
      const pageSize = 200
      for (let number = 1; number <= 25; number++) {
        const reply = await invoke<Reply>('get', { account: reviewAccount.id, path: `/matches?page=${number}&pageSize=${pageSize}` })
        if (reply.status !== 200) break
        const data = JSON.parse(reply.body) as MatchPage
        for (const match of data.items) if (wanted.has(match.id)) found.push([match.id, { account: reviewAccount, match }])
        if (number * pageSize >= data.total || found.length === wanted.size) break
      }
      return found
    }
    void Promise.all(accounts.map(discover)).then(groups => {
      if (cancelled) return
      const byMatch = new Map(groups.flat())
      const contexts: Record<string, RecordingContext> = {}
      for (const item of library!.recordings) if (item.matchId && byMatch.has(item.matchId)) contexts[item.id] = byMatch.get(item.matchId)!
      setRecordingContexts(contexts)
    }).catch(() => { /* Thumbnails and playback remain useful offline. */ })
    return () => { cancelled = true }
  }, [library, accounts])

  // Local files obey the same grant as online history. An old recording does
  // not become visible merely because it still exists on a reassigned PC.
  const accountIds = useMemo(() => new Set(accounts.map(a => a.id)), [accounts])
  const accountsByRiotId = useMemo(() => new Map(accounts.map(a => [a.riotId.toLocaleLowerCase(), a])), [accounts])
  const ownerFor = useCallback((r: Recording) => {
    const context = recordingContexts[r.id]
    if (context && accountIds.has(context.account.id)) return context.account
    return r.player ? accountsByRiotId.get(r.player.toLocaleLowerCase()) : undefined
  }, [accountIds, accountsByRiotId, recordingContexts])
  const permittedRecordings = useMemo(() => (library?.recordings ?? []).filter(ownerFor), [library, ownerFor])
  const available = useMemo(() => permittedRecordings.filter(r => r.available), [permittedRecordings])
  useEffect(() => {
    if (!lastRequested.current || available.length === 0) return
    lastRequested.current = false
    const first = available[0]
    openLocal(first, ownerFor(first))
  }, [available, openLocal, ownerFor])

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
    setRecordingContexts({})
    setAccounts(discovery.accounts); setOffline(discovery.offline)
    const current = discovery.accounts.find(a => a.id === selected?.id) ?? discovery.accounts[0] ?? null
    refreshHistory.current = true
    setSelected(current)
    if (discovery.denied) { navigate('/matches'); setNotice('Tracker access was refused. Check the agent’s enrollment.') }
  }
  const recordedGames = new Set(permittedRecordings.map(r => r.matchId).filter(Boolean)).size || permittedRecordings.length
  const visible = available.filter(r => {
    const context = recordingContexts[r.id]
    return `${r.name} ${r.player ?? ''} ${context?.account.label ?? ''} ${context?.match.champion ?? ''} ${context?.match.opponentChampion ?? ''} ${context?.match.queueName ?? ''}`.toLowerCase().includes(query.toLowerCase())
  })
  return <div className="desktop-shell">
    <header className="desktop-bar"><button className="desktop-brand" onClick={() => navigate('/matches')}><img src="/favicon.svg" alt="" />LeagueTracker</button>
      <div className="desktop-account">{accounts.length > 0 && <select aria-label="Review account" value={selected?.id ?? ''} onChange={e => changeAccount(e.target.value)}>{accounts.map(a => <option key={a.id} value={a.id}>{a.label}</option>)}</select>}
        <button className="action" aria-expanded={settingsOpen} onClick={() => setSettingsOpen(s => !s)}>Storage</button>
        {selected && <button className="action" onClick={() => void action('openWebsite', { account: selected.id })}>Open website ↗</button>}</div>
    </header>
    <main className="desktop-main">
      {notice && <div className="review-notice" role="alert">{notice}<button className="action" onClick={() => setNotice(null)}>Dismiss</button></div>}
      {offline && <p className="review-offline">Showing saved information. Local recordings play without a connection.</p>}
      {settingsOpen && library && <StorageSettings library={library} recordings={permittedRecordings} close={() => setSettingsOpen(false)} save={async value => { await invoke('settings', value); await reload() }} />}
      <Suspense fallback={<p className="mut">Opening review…</p>}><Routes>
        <Route path="/local" element={recording ? <><button className="action review-back" onClick={() => navigate('/matches')}>← Library</button><LocalPlayer key={recording.id} recording={recording} selected={selected} showAnalysis={() => recording.matchId && showMatch(recording.matchId, recording)} /></> : <p>Choose a recording in your library.</p>} />
        <Route path="/matches/:id" element={<MatchDetail key={`${selected?.id}:${location.pathname}`} />} />
        <Route path="*" element={<>
          <div className="review-heading"><div><p className="eyebrow">Your gameplay</p><h1>Review. Learn. Play again.</h1><p className="mut">Your recordings and the moments worth another look.</p></div>
            <button className="action" disabled={busy} onClick={() => void refresh().catch(e => setNotice(String(e)))}>{busy ? 'Refreshing…' : 'Refresh'}</button></div>
          <div className="review-library-heading"><div><h2>On this PC</h2><p className="review-library-count"><strong>{recordedGames}</strong> games recorded here · <strong>{available.length}</strong> playable now</p></div><input className="review-search" aria-label="Find a recording" placeholder="Champion, account or recording…" value={query} onChange={e => setQuery(e.target.value)} /></div>
          {library && <p className="mut sm-text">{gb(available.reduce((n, r) => n + r.sizeBytes, 0))} of playable recordings · {library.freeGb.toFixed(0)} GB free · {library.settings.keepAll ? 'Keeping every recording' : `Keeping up to ${library.settings.keepGames} games within ${library.settings.maxGb} GB`}</p>}
          {visible.length === 0 && <div className="card review-empty"><h3>{query ? 'No recordings match your search' : 'Your next game belongs here'}</h3><p className="mut">Finished recordings appear here automatically. Previously removed videos may still be available in your match history below.</p></div>}
          <div className="recording-grid">{visible.map(r => <RecordingCard key={r.id} recording={r} context={recordingContexts[r.id]}
            open={() => openLocal(r, recordingContexts[r.id]?.account)} act={action} />)}</div>
          <div className="review-library-heading"><h2>Match history</h2><span className="mut">{selected?.label ?? 'Connect your tracker to see analysis'}</span></div>
          <div className="review-matches">{matches.map(m => <button className="review-match card" key={m.id} onClick={() => showMatch(m.id, available.find(r => r.matchId === m.id) ?? null)}>
            <span className={m.win ? 'win' : 'loss'}>{m.win ? 'Victory' : 'Defeat'}</span><strong>{m.champion}</strong><span>{m.kills}/{m.deaths}/{m.assists}</span><span className="mut">{m.queueName}</span><span className="mut">{when(m.gameEndUtc)}</span><span aria-hidden>→</span>
          </button>)}</div>
          {matches.length < total && selected && <button className="action" disabled={busy} onClick={() => void loadMatches(selected, page + 1)}>Load more games</button>}
        </>} />
      </Routes></Suspense>
    </main>
  </div>
}

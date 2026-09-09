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

function RecordingRow({ recording, context, open, act }: {
  recording: Recording
  context: RecordingContext | undefined
  open: () => void
  act: (operation: string, argument: unknown) => Promise<void>
}) {
  const championIconFor = useChampionIcons()
  const match = context?.match
  const championIcon = championIconFor(match?.champion ?? '')
  const opponentIcon = championIconFor(match?.opponentChampion ?? '')
  return <article className={`card recording-row${match ? match.win ? ' recording-win' : ' recording-loss' : ''}`}>
    <button className="recording-row-open" onClick={open} aria-label={`Review ${recording.name}`}>
      <span className="recording-row-meta">
        <strong className={match ? match.win ? 'win' : 'loss' : ''}>{match ? match.win ? 'Victory' : 'Defeat' : 'Recorded game'}</strong>
        <small>{match ? shortQueue(match.queueName) : 'Waiting for tracker'} · {Math.round(recording.durationSec / 60)}m</small>
        <small>{context?.account.label ?? recording.player ?? 'Local recording'} · {when(recording.recordedUtc)}</small>
      </span>
      <span className="recording-row-matchup">
        <span className="recording-row-champion">
          {championIcon ? <img src={championIcon} alt="" loading="lazy" /> : <span className="champ-mono">{match?.champion.slice(0, 2).toUpperCase() ?? '?'}</span>}
          <span><strong>{match?.champion ?? 'Details pending'}</strong><small>{match?.position || 'Recorded on this PC'}</small></span>
        </span>
        {match?.opponentChampion && <><span className="recording-row-vs">vs</span><span className="recording-row-opponent">
          {opponentIcon ? <img src={opponentIcon} alt="" loading="lazy" /> : <span className="champ-mono">{match.opponentChampion.slice(0, 2).toUpperCase()}</span>}
          <strong>{match.opponentChampion}</strong>
        </span></>}
      </span>
      <span className="recording-row-kda">{match ? <><strong>{match.kills}/{match.deaths}/{match.assists}</strong><small>{match.kda} KDA</small></> : <small>Waiting for tracker</small>}</span>
      <span className="recording-row-loadout">{match && <Loadout items={match.items} summoner1Id={match.summoner1Id} summoner2Id={match.summoner2Id} />}</span>
      <span className={`recording-availability${recording.available ? ' has-footage' : ''}`}><strong>{recording.available ? 'Footage' : 'Map'}</strong><small>{recording.available ? `${gb(recording.sizeBytes)} on this PC` : 'video rotated'}</small></span>
      <span className="recording-row-arrow" aria-hidden>→</span>
    </button>
    <div className="recording-row-actions">{recording.available && <><button className={`action${recording.pinned ? ' primary' : ''}`} onClick={() => void act('pin', { id: recording.id, pinned: !recording.pinned })}>{recording.pinned ? 'Pinned' : 'Pin'}</button>
      <button className="action" disabled={recording.pinned} onClick={() => {
        const warning = recording.published ? 'Delete this local video? Its online copy and analysis remain.' : 'This may be your only copy. Delete this local video?'
        if (window.confirm(warning)) void act('delete', { id: recording.id, confirm: true })
      }}>Delete</button></>}</div>
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
  const gameCount = new Set(recordings.map(r => r.matchId ?? r.id)).size
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
  const [libraryPage, setLibraryPage] = useState(1)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const [offline, setOffline] = useState(false)
  const [busy, setBusy] = useState(false)
  const lastRequested = useRef(openLast)
  const localForAccountChange = useRef<Recording | null>(null)
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
      setAccounts(value.accounts); setOffline(value.offline)
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

  useEffect(() => {
    if (selected) {
      const local = localForAccountChange.current
      localForAccountChange.current = null
      selectAccount(selected, local)
    }
  }, [selected])

  // Only the identity written while this PC was recording can establish a
  // local account. A match id cannot: every participant in a game has the
  // same id, so using history overlap would let a duo partner into the picker.
  const accountsByRiotId = useMemo(() => new Map(accounts.map(a => [a.riotId.toLocaleLowerCase(), a])), [accounts])
  const directOwnerFor = useCallback((r: Recording) => {
    if (!r.player?.includes('#') || r.player.toLocaleLowerCase() === 'unknown') return undefined
    return accountsByRiotId.get(r.player.toLocaleLowerCase())
  }, [accountsByRiotId])
  // The catalogue is newest-first, so the first proven account owns the
  // newest local recording and is the correct initial selection.
  const recordedAccounts = useMemo(() => {
    const seen = new Set<string>()
    const result: ReviewAccount[] = []
    for (const item of library?.recordings ?? []) {
      const owner = directOwnerFor(item)
      if (owner && !seen.has(owner.id)) { seen.add(owner.id); result.push(owner) }
    }
    return result
  }, [library, directOwnerFor])
  useEffect(() => {
    setSelected(current => recordedAccounts.find(a => a.id === current?.id) ?? recordedAccounts[0] ?? null)
  }, [recordedAccounts])

  // Match ids survive Riot ID changes. Walk each of this machine's account
  // histories in pages, then attach the real champion, result and loadout to
  // every attributable local recording instead of guessing from its filename.
  useEffect(() => {
    const wanted = new Set((library?.recordings ?? []).filter(r => r.matchId).map(r => r.matchId!))
    if (wanted.size === 0 || recordedAccounts.length === 0) return
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
    void (async () => {
      const byMatch = new Map<string, RecordingContext>()
      // This is background decoration, not identity discovery or a reason to
      // compete with the match being opened. Scan only sidecar-proven accounts
      // and publish each result immediately.
      for (const reviewAccount of recordedAccounts) {
        if (cancelled) return
        for (const [matchId, context] of await discover(reviewAccount)) if (!byMatch.has(matchId)) byMatch.set(matchId, context)
        if (cancelled) return
        const contexts: Record<string, RecordingContext> = {}
        for (const item of library!.recordings) if (item.matchId && byMatch.has(item.matchId)) contexts[item.id] = byMatch.get(item.matchId)!
        setRecordingContexts(contexts)
        if (byMatch.size === wanted.size) break
      }
    })().catch(() => { /* Thumbnails and playback remain useful offline. */ })
    return () => { cancelled = true }
  }, [library, recordedAccounts])

  // Local files obey the same grant as online history. An old recording does
  // not become visible merely because it still exists on a reassigned PC.
  const recordedAccountIds = useMemo(() => new Set(recordedAccounts.map(a => a.id)), [recordedAccounts])
  const ownerFor = useCallback((r: Recording) => {
    const direct = directOwnerFor(r)
    if (direct) return direct
    const context = recordingContexts[r.id]
    if (context && recordedAccountIds.has(context.account.id)) return context.account
    return undefined
  }, [directOwnerFor, recordedAccountIds, recordingContexts])
  const permittedRecordings = useMemo(() => (library?.recordings ?? []).filter(ownerFor), [library, ownerFor])
  const games = useMemo(() => {
    const byMatch = new Map<string, Recording>()
    for (const item of permittedRecordings) {
      const key = item.matchId ?? item.id
      const current = byMatch.get(key)
      if (!current || (!current.available && item.available)) byMatch.set(key, item)
    }
    return [...byMatch.values()]
  }, [permittedRecordings])
  const available = useMemo(() => games.filter(r => r.available), [games])
  useEffect(() => {
    if (!lastRequested.current || available.length === 0) return
    lastRequested.current = false
    const first = available[0]
    openLocal(first, ownerFor(first))
  }, [available, openLocal, ownerFor])

  const showMatch = (matchId: string, local: Recording | null, owner: ReviewAccount | null = selected) => {
    if (!owner) return
    if (owner.id !== selected?.id) {
      localForAccountChange.current = local
      setSelected(owner)
    }
    selectAccount(owner, local); setRecording(local)
    navigate(`/matches/${matchId}`)
  }
  const openRecording = (item: Recording) => {
    const owner = ownerFor(item)
    if (item.matchId && owner) showMatch(item.matchId, item, owner)
    else openLocal(item, owner)
  }
  const action = async (operation: string, argument: unknown) => {
    setNotice(null)
    try { await invoke(operation, argument); await reload() } catch (e) { setNotice(String(e)) }
  }
  const changeAccount = (id: string) => {
    localForAccountChange.current = null
    setSelected(recordedAccounts.find(a => a.id === id) ?? null); setRecording(null); navigate('/matches')
  }
  const refresh = async () => {
    setBusy(true)
    setNotice(null)
    try {
      await reload()
      const discovery = await invoke<{ accounts: ReviewAccount[]; offline: boolean; denied: boolean }>('accounts', { refresh: true })
      setRecordingContexts({})
      localForAccountChange.current = null
      setAccounts(discovery.accounts); setOffline(discovery.offline)
      if (discovery.denied) { navigate('/matches'); setNotice('Tracker access was refused. Check the agent’s enrollment.') }
    } finally { setBusy(false) }
  }
  const recordedGames = games.length
  const visible = games.filter(r => {
    const context = recordingContexts[r.id]
    return `${r.name} ${r.player ?? ''} ${context?.account.label ?? ''} ${context?.match.champion ?? ''} ${context?.match.opponentChampion ?? ''} ${context?.match.queueName ?? ''}`.toLowerCase().includes(query.toLowerCase())
  })
  const pageSize = 25
  const pageCount = Math.max(1, Math.ceil(visible.length / pageSize))
  const currentPage = Math.min(libraryPage, pageCount)
  const pageStart = (currentPage - 1) * pageSize
  const pageRecordings = visible.slice(pageStart, pageStart + pageSize)
  return <div className="desktop-shell">
    <header className="desktop-bar"><button className="desktop-brand" onClick={() => navigate('/matches')}><img src="/favicon.svg" alt="" />LeagueTracker</button>
      <div className="desktop-account">{recordedAccounts.length > 0 && <select aria-label="Review account" value={selected?.id ?? ''} onChange={e => changeAccount(e.target.value)}>{recordedAccounts.map(a => <option key={a.id} value={a.id}>{a.label}</option>)}</select>}
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
          <div className="review-library-heading"><div><h2>Games on this PC</h2><p className="review-library-count"><strong>{recordedGames}</strong> recorded matches · <strong>{available.length}</strong> with footage</p></div><input className="review-search" aria-label="Find a recording" placeholder="Champion, opponent or account…" value={query} onChange={e => { setQuery(e.target.value); setLibraryPage(1) }} /></div>
          {library && <p className="mut sm-text">{gb(available.reduce((n, r) => n + r.sizeBytes, 0))} of playable recordings · {library.freeGb.toFixed(0)} GB free · {library.settings.keepAll ? 'Keeping every recording' : `Keeping up to ${library.settings.keepGames} games within ${library.settings.maxGb} GB`}</p>}
          {visible.length === 0 && <div className="card review-empty"><h3>{query ? 'No games match your search' : 'Your next game belongs here'}</h3><p className="mut">Finished games appear here automatically. Their match review remains available after local footage rotates out.</p></div>}
          <div className="recording-list">{pageRecordings.map(r => <RecordingRow key={r.id} recording={r} context={recordingContexts[r.id]}
            open={() => openRecording(r)} act={action} />)}</div>
          {visible.length > 0 && <nav className="recording-pages" aria-label="Recorded games pages"><span>Showing {pageStart + 1}–{Math.min(pageStart + pageSize, visible.length)} of {visible.length}</span><div><button className="action" disabled={currentPage === 1} onClick={() => setLibraryPage(currentPage - 1)}>Previous</button><span>Page {currentPage} of {pageCount}</span><button className="action" disabled={currentPage === pageCount} onClick={() => setLibraryPage(currentPage + 1)}>Next</button></div></nav>}
        </>} />
      </Routes></Suspense>
    </main>
  </div>
}

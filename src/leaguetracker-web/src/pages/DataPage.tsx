import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { account } from '../account'
import { auth } from '../auth'
import Machines from '../components/Machines'
import { api } from '../api'
import type { JobStatus, RenderQueueRow, Status, StorageInfo } from '../types'

export default function DataPage() {
  // The page has two readers: the owner (jobs, machines, exports, settings)
  // and everyone else (the public figures and, when signed in, a way to claim
  // an account nobody owns). The server enforces the same split; this only
  // decides what to draw.
  const canManage = auth.owns(account.current.id)
  const [status, setStatus] = useState<Status | null>(null)
  const [job, setJob] = useState<JobStatus | null>(null)
  const [renderQueue, setRenderQueue] = useState<RenderQueueRow[]>([])
  const [storage, setStorage] = useState<StorageInfo | null>(null)
  // In the Docker deployment backup folders are mounted read-only at
  // /imports (see docker-compose.override.yml); host runs use Windows paths.
  const [importPath, setImportPath] = useState('/imports')
  const pollTimer = useRef<number | null>(null)
  const [showFailed, setShowFailed] = useState(false)

  // Failed renders grouped by their (normalised) reason: the same patch-mismatch
  // or sim-hang message repeats across many games, so one row per reason with
  // the games listed beats a comma-soup paragraph.
  const reloadRenderQueue = () => api.renderQueue().then(setRenderQueue).catch(() => setRenderQueue([]))

  const failedGroups = (() => {
    const groups = new Map<string, RenderQueueRow[]>()
    for (const r of renderQueue) {
      if (r.status !== 'failed') continue
      const reason = (r.error ?? 'unknown error').replace(/window\(s\) [\d, ]+ skipped/, 'some windows skipped')
      groups.set(reason, [...(groups.get(reason) ?? []), r])
    }
    return [...groups.entries()].map(([reason, rows]) => ({ reason, rows })).sort((a, b) => b.rows.length - a.rows.length)
  })()

  useEffect(() => {
    api.status().then(s => { setStatus(s); setJob(s.job) }).catch(console.error)
    if (canManage) {
      reloadRenderQueue()
      api.storage().then(setStorage).catch(() => setStorage(null))
    }
    return () => { if (pollTimer.current) window.clearInterval(pollTimer.current) }
  }, [canManage])

  const pollJob = () => {
    if (pollTimer.current) window.clearInterval(pollTimer.current)
    pollTimer.current = window.setInterval(async () => {
      const j = await api.jobStatus()
      setJob(j)
      if (!j.running && pollTimer.current) {
        window.clearInterval(pollTimer.current)
        api.status().then(setStatus).catch(console.error)
      }
    }, 2000)
  }

  const startSync = async () => {
    setJob(await api.syncHistory())
    pollJob()
  }

  const startImport = async () => {
    setJob(await api.importFolder(importPath))
    pollJob()
  }

  if (!canManage) {
    return (
      <div className="grid" style={{ gap: 14 }}>
        <div className="card">
          <h2>Live capture</h2>
          <p className="mut" style={{ marginTop: 0 }}>
            The tracker follows this account automatically: games, timelines, everyone's rank at game time and the LP change
            are captured within seconds of a game ending.
          </p>
          <p>
            Tracking <strong>{status?.riotId ?? '…'}</strong> · {status?.matches ?? 0} games · {status?.lpSnapshots ?? 0} LP
            snapshots · {status?.replays ?? 0} replays archived
          </p>
        </div>
        <div className="card">
          <h2>{status?.account.owned ? 'Owned account' : 'Nobody owns this account yet'}</h2>
          <p className="mut" style={{ marginTop: 0 }}>
            {status?.account.owned
              ? 'Only its owner can run syncs, manage machines, download exports or change what the profile shows.'
              : auth.signedIn
                ? 'If this is your Riot account you can claim it: the tracker asks you to set a profile icon in the League client and checks it with Riot.'
                : 'Sign in to claim it if it is yours.'}
          </p>
        </div>
      </div>
    )
  }

  return (
    <div className="grid" style={{ gap: 14 }}>
      {status && status.apiKeyConfigured === false && (
        <div className="card">
          <h2>API key missing</h2>
          <p>
            Live capture and history sync need a Riot API key. Put it on the first line of the key file
            configured in <code>appsettings.json</code> (Riot → ApiKeyFile), or set the <code>RIOT_API_KEY</code>{' '}
            environment variable. Use a <strong>personal</strong> key from developer.riotgames.com — dev keys expire daily.
            Importing existing export folders works without a key.
          </p>
        </div>
      )}

      <div className="card">
        <h2>Live capture</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          Runs automatically in the background. The tracker spots your game while it's still being played (that's the
          banner up top), and the moment it ends switches to a fast cadence so the match, timeline, everyone's rank at
          game time, your exact LP change <em>and the official replay file</em> are captured within seconds.
        </p>
        <p>
          Tracking <strong>{status?.riotId ?? '…'}</strong> · {status?.matches ?? 0} games · {status?.lpSnapshots ?? 0} LP
          snapshots · {status?.replays ?? 0} replays archived
        </p>
      </div>

      <div className="card">
        <h2>Sync full match history</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          Pages through everything Riot still serves for this account (all queues, match + timeline) and downloads
          whatever isn't stored yet - already-stored games are skipped, so re-running is cheap and safe.
          Note: ranks attached to backfilled games are the players' ranks <em>now</em>, not at game time; only live
          capture gets at-game-time ranks.
        </p>
        <button className="action" onClick={startSync} disabled={job?.running === true}>Sync everything</button>
      </div>

      <div className="card">
        <h2>Restore from raw game files</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          The database is just an index - the raw per-game JSON files are the source of truth. Point this at a backup
          of a <code>games</code> folder (or an old PowerShell-exporter folder; same format) to rebuild games, deaths
          and the LP ledger without touching the Riot API. Already-imported games are skipped.
        </p>
        <div className="filters" style={{ margin: 0 }}>
          <input className="text" style={{ flex: 1 }} value={importPath} onChange={e => setImportPath(e.target.value)} aria-label="Folder to import" />
          <button className="action" onClick={startImport} disabled={job?.running === true}>Import</button>
        </div>
      </div>

      <div className="card">
        <h2>Reprocess analytics</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          Recomputes everything timeline-derived (collapse counts, positions, objectives, damage breakdowns) from the raw
          game files on disk - run after an update adds new metrics. No API calls; LP records and captured ranks are untouched.
        </p>
        <button className="action" onClick={async () => { setJob(await api.reprocess()); pollJob() }} disabled={job?.running === true}>
          Reprocess all games
        </button>
      </div>

      <Machines />

      {renderQueue.length > 0 && (
        <div className="card">
          <h2>Clip rendering</h2>
          <p className="mut" style={{ marginTop: 0 }}>
            Each archived replay gets its kill/death moments cut into mp4 clips by the render agent on the gaming PC
            (it drives the game client's replay mode). Clips appear on the match pages as they land.
          </p>
          <div className="status-tiles">
            {(['pending', 'partial', 'rendering', 'done', 'failed'] as const).map(s => {
              const n = renderQueue.filter(r => r.status === s).length
              return n > 0 ? (
                <div key={s} className={`tile status-tile ${s}`}>
                  <div className="value">{n}</div>
                  <div className="label">{s}</div>
                </div>
              ) : null
            })}
            {renderQueue.every(r => r.status === 'no-events') && <span className="mut">nothing to render yet</span>}
          </div>
          {failedGroups.length > 0 && (
            <div className="render-failed">
              <button className="kpi-toggle" onClick={() => setShowFailed(v => !v)}>
                {showFailed ? 'Hide' : 'Show'} failed renders ({failedGroups.reduce((n, g) => n + g.rows.length, 0)})
              </button>
              {showFailed && (
                <div className="table-scroll">
                  <table className="data render-failed-table">
                    <thead><tr><th>Reason</th><th>Games</th></tr></thead>
                    <tbody>
                      {failedGroups.map(g => (
                        <tr key={g.reason}>
                          <td className="reason">
                            {g.reason}
                            <button className="dismiss-x" title={`Dismiss all ${g.rows.length}`}
                              onClick={async () => { for (const r of g.rows) await api.dismissRender(r.matchId, r.kind); reloadRenderQueue() }}>✕ all</button>
                          </td>
                          <td className="games"><div className="games-list">
                            {g.rows.map(r => (
                              <span key={r.matchId} className="failed-game">
                                <Link to={`/matches/${r.matchId}`} title={r.matchId}>
                                  {r.champion} · {new Date(r.gameEndUtc).toLocaleDateString()}
                                </Link>
                                <button className="dismiss-x" title="Dismiss - a dead render (won't retry). Retry it from the match page to bring it back."
                                  onClick={async () => { await api.dismissRender(r.matchId, r.kind); reloadRenderQueue() }}>✕</button>
                              </span>
                            ))}
                          </div></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {storage && (
        <div className="card">
          <h2>Storage</h2>
          <p className="mut" style={{ marginTop: 0 }}>
            What the tracker's data folder holds. Clips are small and permanent; full-game renders are the heavy tier
            and expire automatically unless marked keep on their match page.
          </p>
          <div className="status-tiles">
            {([['raw games', storage.rawGamesMb], ['replays', storage.replaysMb], ['clips', storage.clipsMb],
              ['full games', storage.fullGamesMb], ['database', storage.databaseMb]] as const).map(([label, mb]) => (
              <div key={label} className="tile status-tile">
                <div className="value">{mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`}</div>
                <div className="label">{label}</div>
              </div>
            ))}
          </div>
        </div>
      )}

      {job && (job.running || job.message) && (
        <div className="card">
          <h2>Job status</h2>
          <p>
            <strong>{job.jobName ?? 'idle'}</strong> — {job.message}
            {job.running && job.total > 0 && ` (${Math.round((100 * job.processed) / job.total)}%)`}
          </p>
        </div>
      )}

      <div className="card">
        <h2>Exports</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          The .zip bundles every table below plus <code>dashboard.json</code> (the computed dashboard views over all
          games) - for the coaching workflows. Or grab a single CSV.
        </p>
        <div className="filters" style={{ margin: 0, flexWrap: 'wrap' }}>
          <a className="action primary" href={account.apiUrl('/api/export/all.zip')} download>Download .zip</a>
          {['matches.csv', 'challenges.csv', 'lane-checkpoints.csv', 'ranks.csv', 'deaths.csv', 'objectives.csv', 'lp-history.csv'].map(f => (
            <a key={f} className="action" href={account.apiUrl(`/api/export/${f}`)} download>{f}</a>
          ))}
        </div>
      </div>
    </div>
  )
}

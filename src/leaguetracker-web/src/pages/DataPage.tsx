import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import type { JobStatus, RenderQueueRow, Status, StorageInfo } from '../types'

export default function DataPage() {
  const [status, setStatus] = useState<Status | null>(null)
  const [job, setJob] = useState<JobStatus | null>(null)
  const [renderQueue, setRenderQueue] = useState<RenderQueueRow[]>([])
  const [storage, setStorage] = useState<StorageInfo | null>(null)
  // In the Docker deployment backup folders are mounted read-only at
  // /imports (see docker-compose.override.yml); host runs use Windows paths.
  const [importPath, setImportPath] = useState('/imports')
  const pollTimer = useRef<number | null>(null)

  useEffect(() => {
    api.status().then(s => { setStatus(s); setJob(s.job) }).catch(console.error)
    api.renderQueue().then(setRenderQueue).catch(() => setRenderQueue([]))
    api.storage().then(setStorage).catch(() => setStorage(null))
    return () => { if (pollTimer.current) window.clearInterval(pollTimer.current) }
  }, [])

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

  return (
    <div className="grid" style={{ gap: 14 }}>
      {status && !status.apiKeyConfigured && (
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

      {renderQueue.length > 0 && (
        <div className="card">
          <h2>Clip rendering</h2>
          <p className="mut" style={{ marginTop: 0 }}>
            Each archived replay gets its kill/death moments cut into mp4 clips by the render agent on the gaming PC
            (it drives the game client's replay mode). Clips appear on the match pages as they land.
          </p>
          <p style={{ margin: 0 }}>
            {(['pending', 'partial', 'rendering', 'done', 'failed'] as const).map(s => {
              const n = renderQueue.filter(r => r.status === s).length
              return n > 0 ? <span key={s} style={{ marginRight: 14 }}><strong>{n}</strong> {s}</span> : null
            })}
            {renderQueue.every(r => r.status === 'no-events') && <span className="mut">nothing to render yet</span>}
          </p>
          {renderQueue.some(r => r.status === 'failed') && (
            <p className="mut sm-text" style={{ marginBottom: 0 }}>
              failed: {renderQueue.filter(r => r.status === 'failed').map(r => `${r.matchId} (${r.error})`).join(', ')}
            </p>
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
          <p style={{ margin: 0 }}>
            {([['raw games', storage.rawGamesMb], ['replays', storage.replaysMb], ['clips', storage.clipsMb],
              ['full games', storage.fullGamesMb], ['database', storage.databaseMb]] as const).map(([label, mb]) => (
              <span key={label} style={{ marginRight: 16 }}>
                <strong>{mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`}</strong> <span className="mut">{label}</span>
              </span>
            ))}
          </p>
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
          Download .zip mirrors everything on the screens: per-game stats, the full Riot challenges block, lane
          checkpoints, all-10 loadouts, deaths, the objective timeline, LP history, and <code>dashboard.json</code>
          (the computed dashboard views over all games) - for the coaching workflows.
        </p>
        <div className="filters" style={{ margin: 0, flexWrap: 'wrap' }}>
          <a className="action primary" href="/api/export/all.zip" download>Download .zip</a>
          {['matches.csv', 'challenges.csv', 'lane-checkpoints.csv', 'ranks.csv', 'deaths.csv', 'objectives.csv', 'lp-history.csv'].map(f => (
            <a key={f} className="action" href={`/api/export/${f}`} download>{f}</a>
          ))}
        </div>
      </div>
    </div>
  )
}

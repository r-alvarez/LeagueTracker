import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import type { JobStatus, Status } from '../types'

export default function DataPage() {
  const [status, setStatus] = useState<Status | null>(null)
  const [job, setJob] = useState<JobStatus | null>(null)
  const [rankedTarget, setRankedTarget] = useState('250')
  // In the Docker deployment old export folders are mounted read-only at
  // /imports (see docker-compose.override.yml); host runs use Windows paths.
  const [importPath, setImportPath] = useState('/imports/export-20260610-082650')
  const pollTimer = useRef<number | null>(null)

  useEffect(() => {
    api.status().then(s => { setStatus(s); setJob(s.job) }).catch(console.error)
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
    setJob(await api.syncHistory(parseInt(rankedTarget, 10) || 0))
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
          Runs automatically in the background: every couple of minutes the tracker checks for finished games and captures
          the match, timeline, everyone's rank at game time, and your exact LP change.
        </p>
        <p>
          Tracking <strong>{status?.riotId ?? '…'}</strong> · {status?.matches ?? 0} games · {status?.lpSnapshots ?? 0} LP snapshots
        </p>
      </div>

      <div className="card">
        <h2>Backfill match history</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          Downloads your most recent ranked games (match + timeline + current ranks) that aren't stored yet.
          Note: ranks attached to old games are the players' ranks <em>now</em>, not at game time.
        </p>
        <div className="filters" style={{ margin: 0 }}>
          <input className="text" value={rankedTarget} onChange={e => setRankedTarget(e.target.value)} aria-label="Ranked games to fetch" />
          <button className="action" onClick={startSync} disabled={job?.running === true}>Sync ranked history</button>
        </div>
      </div>

      <div className="card">
        <h2>Import PowerShell exports</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          Points at an export folder (or the live watcher folder). Games, deaths, the LP ledger and per-game LP
          are all carried over; already-imported games are skipped.
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
          Download .zip bundles everything (all four CSVs + <code>summary.json</code>) in one file - the same shapes the
          PowerShell tooling produced, for the coaching workflows.
        </p>
        <div className="filters" style={{ margin: 0, flexWrap: 'wrap' }}>
          <a className="action primary" href="/api/export/all.zip" download>Download .zip</a>
          {['matches.csv', 'deaths.csv', 'ranks.csv', 'lp-history.csv'].map(f => (
            <a key={f} className="action" href={`/api/export/${f}`} download>{f}</a>
          ))}
        </div>
      </div>
    </div>
  )
}

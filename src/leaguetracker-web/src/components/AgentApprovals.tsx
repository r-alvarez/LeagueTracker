import { Fragment, useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import type { AgentInfo } from '../types'

interface AgentKey {
  id: string; name: string; machine: string; status: 'pending' | 'approved' | 'revoked'
  createdUtc: string; decidedUtc: string | null; lastSeenUtc: string | null; lastIp: string | null; note: string | null
  live: AgentInfo | null
}

interface AgentAccess { latestVersion: string | null; agents: AgentKey[] }

/// Enrolled agents: a new machine knocks with only the tracker URL and
/// waits here as "pending" until the owner clicks Approve. Revoke cuts it
/// off at the next request; the machine keeps its key and shows "revoked".
/// Each row also carries what the agent last reported over its heartbeat -
/// version against the newest published build, what it is doing, whether it
/// is still reporting, its last error - so this one table answers "who is on
/// what" and holds the two agent actions (restart, dismiss error) too.
export default function AgentApprovals() {
  const [access, setAccess] = useState<AgentAccess | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(() => {
    fetch('/api/agents').then(r => (r.ok ? r.json() : null)).then(setAccess).catch(() => setAccess(null))
  }, [])
  useEffect(() => {
    load()
    const t = window.setInterval(load, 15000)
    return () => window.clearInterval(t)
  }, [load])

  const act = async (id: string, verb: 'approve' | 'revoke' | 'delete') => {
    setBusy(id)
    try {
      await fetch(verb === 'delete' ? `/api/agents/${id}` : `/api/agents/${id}/${verb}`, { method: verb === 'delete' ? 'DELETE' : 'POST' })
      load()
    } finally { setBusy(null) }
  }

  if (!access || access.agents.length === 0) return null
  const keys = access.agents
  const pending = keys.filter(k => k.status === 'pending').length
  const latest = access.latestVersion
  const when = (s: string | null) => (s ? new Date(s).toLocaleString() : '—')
  // Newest heartbeat or newest key activity, whichever the row has.
  const seen = (k: AgentKey) => k.live?.seenUtc ?? k.lastSeenUtc

  return (
    <div className="card">
      <h2>Agent access {pending > 0 && <span className="badge loss">{pending} waiting</span>}</h2>
      <p className="mut" style={{ marginTop: 0 }}>
        Machines that asked to talk to this tracker. A new agent needs only the site URL; approve it here once and it
        stays approved until revoked. Revoke instantly stops a machine (its key stops working); delete forgets it.
        {latest && <> Newest published build: <strong>{latest}</strong>.</>}
      </p>
      <div className="table-scroll">
        <table className="data agent-access">
          <thead>
            <tr>
              <th>Access</th>
              <th>Agent</th>
              <th>PC</th>
              <th>User</th>
              <th>Version</th>
              <th>Now</th>
              <th>Last seen</th>
              <th>From</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {keys.map(k => {
              const live = k.live
              const outdated = !!(live && latest && live.version !== latest && live.version !== '0.0.0.0')
              return (
                <Fragment key={k.id}>
                <tr className={k.status === 'pending' ? 'pending' : undefined}>
                  <td>
                    <span className={`badge ${k.status === 'approved' ? 'win' : k.status === 'pending' ? 'remake' : 'loss'}`}>{k.status}</span>
                  </td>
                  <td><strong>{k.name}</strong>{live && live.role !== 'full' && <span className="mut sm-text"> · {live.role}</span>}</td>
                  <td>{k.machine}</td>
                  <td>{live?.user ?? <span className="mut">—</span>}</td>
                  <td>
                    {live
                      ? <span title={outdated ? `Newest is ${latest}; the agent updates itself when idle` : 'On the newest build'}>
                          {live.version}{outdated && <span className="warn-text"> · update due</span>}
                        </span>
                      : <span className="mut">—</span>}
                  </td>
                  <td>
                    {live
                      ? <>
                          <span className={`badge ${live.online ? (live.paused ? 'remake' : 'win') : 'loss'}`}>
                            {live.online ? (live.paused ? 'paused' : 'online') : 'offline'}
                          </span>
                          {live.online && <span className="mut sm-text"> {live.state}{live.detail ? ` — ${live.detail}` : ''}</span>}
                          {!live.youTubeReady && <span className="warn-text sm-text"> · YouTube not authorized</span>}
                          {live.lastRecordingUtc && <div className="mut sm-text">last recording {when(live.lastRecordingUtc)}</div>}
                        </>
                      : k.status === 'pending'
                        ? <span className="mut sm-text">asked {when(k.createdUtc)}</span>
                        : <span className="mut sm-text">no heartbeat</span>}
                  </td>
                  <td>{when(seen(k))}</td>
                  <td>{k.lastIp ?? <span className="mut">—</span>}</td>
                  <td className="actions">
                    {k.status === 'pending' && <>
                      <button className="action primary sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'approve')}>Approve</button>{' '}
                      <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Reject</button>
                    </>}
                    {k.status === 'approved' && <>
                      {live && <>
                        <button className="action sm-action" title="Ask this agent to restart on its next heartbeat (when it is idle) - it re-reads settings and updates itself"
                          disabled={busy === k.id} onClick={async () => { setBusy(k.id); try { await api.restartAgent(live.agent) } finally { setBusy(null) } }}>Restart</button>{' '}
                      </>}
                      <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'revoke')}>Revoke</button>
                    </>}
                    {k.status === 'revoked' && <>
                      <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'approve')}>Re-approve</button>{' '}
                      <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Delete</button>
                    </>}
                  </td>
                </tr>
                {live?.lastError && (
                  <tr className="agent-error-row">
                    <td colSpan={9}>
                      <div className="agent-error">
                        <span className="agent-error-label">Last error</span>{live.lastError}
                        <button className="dismiss-x" title="Dismiss (comes back only if a new error appears)"
                          onClick={async () => { await api.dismissAgentError(live.agent); load() }}>✕</button>
                      </div>
                    </td>
                  </tr>
                )}
                </Fragment>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

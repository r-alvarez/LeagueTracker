import { Fragment, useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import type { AgentInfo } from '../types'

interface AgentLog { file: string; whenUtc: string; sizeBytes: number }

interface AgentKey {
  id: string; name: string; machine: string; status: 'pending' | 'approved' | 'revoked'
  createdUtc: string; decidedUtc: string | null; lastSeenUtc: string | null; lastIp: string | null; note: string | null
  live: AgentInfo | null
  logs: AgentLog[]
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
  // Rows whose log was just requested: the file lands on the next heartbeat,
  // so the button reads "asked" until the list shows a newer file.
  const [logAsked, setLogAsked] = useState<Record<string, number>>({})

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
  // Day + time is what the eye needs in the table; the full stamp sits on hover.
  const whenShort = (s: string | null) => (s
    ? new Date(s).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : '—')
  // Newest heartbeat or newest key activity, whichever the row has.
  const seen = (k: AgentKey) => k.live?.seenUtc ?? k.lastSeenUtc
  // One badge carries both facts: can the key talk to us, and is the agent
  // behind it alive. Approved + no heartbeat is the plain word.
  const statusOf = (k: AgentKey): { cls: string; text: string; title: string } => {
    if (k.status !== 'approved') return { cls: k.status === 'pending' ? 'remake' : 'loss', text: k.status, title: k.status === 'pending' ? 'Waiting for approval' : 'Key revoked - the machine is cut off' }
    const live = k.live
    if (!live) return { cls: 'remake', text: 'approved', title: 'Approved; the agent has not reported yet' }
    if (!live.online) return { cls: 'loss', text: 'offline', title: 'Approved; the agent has stopped reporting' }
    return live.paused ? { cls: 'remake', text: 'paused', title: 'Approved; paused from its tray icon' } : { cls: 'win', text: 'online', title: 'Approved and reporting' }
  }

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
              <th>Status</th>
              <th>Agent</th>
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
              const status = statusOf(k)
              return (
                <Fragment key={k.id}>
                <tr className={k.status === 'pending' ? 'pending' : undefined}>
                  <td><span className={`badge ${status.cls}`} title={status.title}>{status.text}</span></td>
                  <td>
                    <strong>{k.name}</strong>
                    {k.machine && k.machine !== k.name && <span className="mut sm-text"> · {k.machine}</span>}
                    {live && live.role !== 'full' && <span className="mut sm-text"> · {live.role}</span>}
                    {k.logs.length > 0 && (
                      <div className="sm-text">
                        <a href={`/api/agents/${encodeURIComponent(k.name)}/logs/${k.logs[0].file}`} target="_blank" rel="noreferrer"
                          title={`agent.log tail shipped ${when(k.logs[0].whenUtc)} (${Math.round(k.logs[0].sizeBytes / 1024)} KB)${k.logs.length > 1 ? ` · ${k.logs.length - 1} older` : ''}`}>
                          log · {whenShort(k.logs[0].whenUtc)}
                        </a>
                      </div>
                    )}
                  </td>
                  <td>{live?.user ?? <span className="mut">—</span>}</td>
                  <td>
                    {live
                      ? <span title={outdated ? `Newest is ${latest}; the agent updates itself when idle` : 'On the newest build'}>
                          {live.version}{outdated && <span className="warn-text"> · update due</span>}
                        </span>
                      : <span className="mut">—</span>}
                  </td>
                  <td className="now">
                    {live
                      ? <>
                          <span className="agent-now" title={`${live.state}${live.detail ? ` — ${live.detail}` : ''}`}>
                            {live.online ? live.state : 'last: ' + live.state}{live.detail ? ` — ${live.detail}` : ''}
                          </span>
                          {!live.youTubeReady && <span className="warn-text sm-text"> · YouTube not authorized</span>}
                          {live.lastRecordingUtc && <div className="mut sm-text" title={when(live.lastRecordingUtc)}>last recording {whenShort(live.lastRecordingUtc)}</div>}
                        </>
                      : k.status === 'pending'
                        ? <span className="mut sm-text">asked {whenShort(k.createdUtc)}</span>
                        : <span className="mut sm-text">no heartbeat</span>}
                  </td>
                  <td title={when(seen(k))}>{whenShort(seen(k))}</td>
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
                        <button className="action sm-action" title="Ask this agent to send the tail of its agent.log - it arrives on the next heartbeat (about a minute)"
                          disabled={busy === k.id || !!logAsked[k.id]}
                          onClick={async () => { setBusy(k.id); try { await api.requestAgentLog(live.agent); setLogAsked(a => ({ ...a, [k.id]: Date.now() })) } finally { setBusy(null) } }}>
                          {logAsked[k.id] && !(k.logs[0] && new Date(k.logs[0].whenUtc).getTime() > logAsked[k.id]) ? 'Log asked…' : 'Log'}
                        </button>{' '}
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
                    <td colSpan={8}>
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

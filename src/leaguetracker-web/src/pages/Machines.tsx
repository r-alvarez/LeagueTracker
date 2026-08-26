import { Fragment, useCallback, useEffect, useState } from 'react'
import { account } from '../account'
import { api } from '../api'
import { auth } from '../auth'
import OwnerSelect from '../components/OwnerSelect'
import type { AdminUser, AgentKey, MyAgents } from '../types'

interface Release { version: string; file: string; sizeBytes: number; installer: string | null; installerSizeBytes: number }

type Role = 'recorder' | 'renderer'

/// A person's machines: every machine they may see - theirs, plus the
/// renderer that serves everyone; all of them for an admin - each row joined
/// with the heartbeat of the agent behind the key (version against the newest
/// build, what it is doing, whether it still reports, its last error, the log
/// it shipped), the join code that makes a new machine theirs at enrolment,
/// and the agent build to install. Admins can hand a machine to its owner.
export default function Machines() {
  const [release, setRelease] = useState<Release | null>(null)
  const [mine, setMine] = useState<MyAgents | null>(null)
  const [users, setUsers] = useState<AdminUser[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [code, setCode] = useState<{ pretty: string; paste: string; expiresUtc: string; role: string } | null>(null)
  const [role, setRole] = useState<Role>('recorder')
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The one row whose assign form is open, and what it holds. actsFor is the
  // shared-PC grant: accounts someone else owns that get played on this
  // machine, so only this machine can deliver their recordings.
  const [assigning, setAssigning] = useState<{ id: string; owner: string; role: Role; actsFor: string[] } | null>(null)
  // Rows whose log was just requested: the file lands on the next heartbeat,
  // so the button reads "asked" until the list shows a newer file.
  const [logAsked, setLogAsked] = useState<Record<string, number>>({})

  const load = useCallback(() => {
    api.myAgents().then(setMine).catch(() => setMine(null))
  }, [])
  useEffect(() => {
    fetch('/api/agent/release').then(r => (r.status === 200 ? r.json() : null)).then(setRelease).catch(() => setRelease(null))
    if (auth.isAdmin) api.adminUsers().then(r => setUsers(r.users)).catch(() => setUsers([]))
    load()
    const t = window.setInterval(load, 15000)
    return () => window.clearInterval(t)
  }, [load])

  const act = async (id: string, verb: 'approve' | 'revoke' | 'delete' | 'restart' | 'dismiss-error') => {
    setBusy(id)
    try { await api.agentAction(id, verb); load() } finally { setBusy(null) }
  }
  const askLog = async (id: string) => {
    setBusy(id)
    try { await api.requestAgentLog(id); setLogAsked(a => ({ ...a, [id]: Date.now() })) } finally { setBusy(null) }
  }

  const mint = async () => {
    setError(null)
    try {
      const c = await api.mintJoinCode(role)
      // One paste for the setup window: where to knock and the code to say.
      const payload = JSON.stringify({ server: window.location.origin, code: c.code, role })
      const b64 = btoa(unescape(encodeURIComponent(payload))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
      setCode({ pretty: c.pretty, paste: `lt2:${b64}`, expiresUtc: c.expiresUtc, role })
      setCopied(false)
      load()
    } catch (e) { setError(String(e)) }
  }
  const copy = async (text: string) => {
    try { await navigator.clipboard.writeText(text); setCopied(true) } catch { /* selectable text stays */ }
  }

  const saveAssign = async (k: AgentKey) => {
    if (!assigning) return
    setError(null)
    setBusy(k.id)
    try {
      const grantChanged = [...assigning.actsFor].sort().join(',') !== [...(k.actsFor ?? [])].sort().join(',')
      await api.adminAssignAgent(k.id, assigning.owner || null, assigning.role !== k.role ? assigning.role : null, grantChanged ? assigning.actsFor : null)
      setAssigning(null)
      load()
    } catch (e) { setError(String(e)) } finally { setBusy(null) }
  }

  const keys = mine?.keys ?? []
  const pending = keys.filter(k => k.status === 'pending').length
  const latest = mine?.latestVersion ?? null
  const when = (s: string | null) => (s ? new Date(s).toLocaleString() : '—')
  // Day + time is what the eye needs in the table; the full stamp sits on hover.
  const whenShort = (s: string | null) => (s
    ? new Date(s).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : '—')
  const seen = (k: AgentKey) => k.live?.seenUtc ?? k.lastSeenUtc
  // One badge carries both facts: can the key talk to us, and is the agent
  // behind it alive. Approved + no heartbeat is the plain word.
  const statusOf = (k: AgentKey): { cls: string; text: string; title: string } => {
    if (k.status !== 'approved') return { cls: k.status === 'pending' ? 'remake' : 'loss', text: k.status === 'pending' ? 'waiting' : k.status, title: k.status === 'pending' ? 'Waiting for approval' : 'Key revoked - the machine is cut off' }
    const live = k.live
    if (!live) return { cls: 'remake', text: 'approved', title: 'Approved; the agent has not reported yet' }
    if (!live.online) return { cls: 'loss', text: 'offline', title: 'Approved; the agent has stopped reporting' }
    return live.paused ? { cls: 'remake', text: 'paused', title: 'Approved; paused from its tray icon' } : { cls: 'win', text: 'online', title: 'Approved and reporting' }
  }
  const canAct = (k: AgentKey) => auth.isAdmin || k.mine
  const columns = 8

  return (
    <div className="grid" style={{ gap: 14 }}>
      <div className="card">
        <div className="card-head">
          <h2>Machines {pending > 0 && <span className="badge loss">{pending} waiting</span>}</h2>
          <div className="card-head-actions">
            {auth.isAdmin && (
              <select className="select" value={role} onChange={e => setRole(e.target.value as Role)} aria-label="Role of the machine to add">
                <option value="recorder">Recorder – a player's PC</option>
                <option value="renderer">Renderer – the replay box, serves every account</option>
              </select>
            )}
            <button className="action primary" onClick={mint}>Add a machine…</button>
          </div>
        </div>
        <p className="mut card-intro">
          Every machine that runs the agent for you{auth.isAdmin ? ' - and, as an admin, everyone else\'s' : ''}. Press{' '}
          <strong>Add a machine</strong> for a join code, paste it in the agent's setup window on that PC, and the machine appears
          here as <em>waiting</em>; approve it once and it stays yours until you revoke it.
          {latest && <> Newest published build: <strong>{latest}</strong> - machines update themselves when idle.</>}
        </p>

        {code && (
          <div className="join-code-box">
            <div className="join-code">{code.pretty}</div>
            <p className="mut sm-text">
              Type this in the agent's setup window as the join code, or paste the one-line version below (it carries the
              site address too). Valid 15 minutes, single use, {code.role} role.
            </p>
            <textarea className="agent-join-code" readOnly value={code.paste} rows={2} onFocus={e => e.currentTarget.select()} />
            <div className="filters" style={{ margin: '8px 0 0' }}>
              <button className="action" onClick={() => copy(code.paste)}>{copied ? 'Copied' : 'Copy'}</button>
              <button className="action" onClick={() => setCode(null)}>Done</button>
            </div>
          </div>
        )}
        {mine && mine.joinCodes.length > 0 && !code && (
          <p className="mut sm-text card-intro">
            Open join code{mine.joinCodes.length > 1 ? 's' : ''}: {mine.joinCodes.map(c => `${c.code.slice(0, 4)}-${c.code.slice(4)}`).join(', ')}
          </p>
        )}
        {error && <p className="warn-text sm-text">{error}</p>}

        {mine && keys.length === 0 ? (
          <p className="empty">No machines yet - install the agent below, then press <strong>Add a machine</strong> for its join code.</p>
        ) : keys.length > 0 && (
          <div className="table-scroll">
            <table className="data agent-access">
              <thead>
                <tr>
                  <th>Status</th>
                  <th>Machine</th>
                  <th>{auth.isAdmin ? 'Owner' : 'User'}</th>
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
                  const assignOpen = assigning?.id === k.id
                  return (
                    <Fragment key={k.id}>
                    <tr className={k.status === 'pending' ? 'pending' : undefined}>
                      <td><span className={`badge ${status.cls}`} title={status.title}>{status.text}</span></td>
                      <td>
                        <strong>{k.name}</strong>
                        {k.machine && k.machine !== k.name && <span className="mut sm-text"> · {k.machine}</span>}
                        <span className="mut sm-text"> · {k.role}</span>
                        {k.actsForRiotIds?.length > 0 && <span className="mut sm-text"> · also {k.actsForRiotIds.join(', ')}</span>}
                        {auth.isAdmin && <span className="mut sm-text" title="Key id - what Agent__Profiles__<id>__* overrides are keyed by"> · <code>{k.id}</code></span>}
                        {!k.bound && <span className="warn-text sm-text"> · not tied to anyone</span>}
                        {k.logs.length > 0 && (
                          <div className="sm-text">
                            <a href={`/api/me/agents/${k.id}/logs/${k.logs[0].file}`} target="_blank" rel="noreferrer"
                              title={`agent.log tail shipped ${when(k.logs[0].whenUtc)} (${Math.round(k.logs[0].sizeBytes / 1024)} KB)${k.logs.length > 1 ? ` · ${k.logs.length - 1} older` : ''}`}>
                              log · {whenShort(k.logs[0].whenUtc)}
                            </a>
                          </div>
                        )}
                      </td>
                      <td>
                        {auth.isAdmin
                          ? (k.ownerEmail ?? <span className="mut">—</span>)
                          : (live?.user ?? <span className="mut">—</span>)}
                        {auth.isAdmin && live?.user && <div className="mut sm-text">{live.user}</div>}
                      </td>
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
                      <td>{canAct(k) ? (k.lastIp ?? <span className="mut">—</span>) : <span className="mut">—</span>}</td>
                      <td className="actions">
                        {canAct(k) && k.status === 'pending' && <>
                          <button className="action primary sm-action" disabled={busy === k.id || (!k.bound && !auth.isAdmin)} title={!k.bound && !auth.isAdmin ? 'Enrolled without a join code - an admin has to assign it first' : undefined}
                            onClick={() => act(k.id, 'approve')}>Approve</button>
                          <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Reject</button>
                        </>}
                        {canAct(k) && k.status === 'approved' && <>
                          {live && <>
                            <button className="action sm-action" title="Ask this agent to restart on its next heartbeat (when it is idle) - it re-reads settings and updates itself"
                              disabled={busy === k.id} onClick={() => act(k.id, 'restart')}>Restart</button>
                            <button className="action sm-action" title="Ask this agent to send the tail of its agent.log - it arrives on the next heartbeat (about a minute)"
                              disabled={busy === k.id || !!logAsked[k.id]} onClick={() => askLog(k.id)}>
                              {logAsked[k.id] && !(k.logs[0] && new Date(k.logs[0].whenUtc).getTime() > logAsked[k.id]) ? 'Log asked…' : 'Log'}
                            </button>
                          </>}
                          <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'revoke')}>Revoke</button>
                        </>}
                        {canAct(k) && k.status === 'revoked' && <>
                          <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'approve')}>Re-approve</button>
                          <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Delete</button>
                        </>}
                        {auth.isAdmin && (
                          <button className={`action sm-action${assignOpen ? ' on' : ''}`} title="Set who owns this machine and what it does"
                            onClick={() => setAssigning(assignOpen ? null : { id: k.id, owner: k.ownerEmail ?? '', role: k.role, actsFor: k.actsFor ?? [] })}>{k.bound ? 'Change…' : 'Assign…'}</button>
                        )}
                        {!canAct(k) && <span className="mut sm-text">shared</span>}
                      </td>
                    </tr>
                    {assignOpen && assigning && (
                      <tr className="agent-detail-row">
                        <td colSpan={columns}>
                          <div className="assign-form">
                            <label>Owner
                              <OwnerSelect users={users} value={assigning.owner} onChange={owner => setAssigning({ ...assigning, owner })} />
                            </label>
                            <label>Role
                              <select className="select" value={assigning.role} onChange={e => setAssigning({ ...assigning, role: e.target.value as Role })}>
                                <option value="recorder">recorder – records and renders its owner's accounts</option>
                                <option value="renderer">renderer – renders replays for everyone</option>
                              </select>
                            </label>
                            {(() => {
                              // Accounts the owner doesn't already cover: the
                              // shared-PC grant - someone else's account that
                              // gets played on this machine, whose games only
                              // this machine can deliver.
                              const ownerId = users.find(u => u.email === assigning.owner)?.id ?? k.ownerUserId
                              const grantable = account.all.filter(a => a.ownerUserId !== ownerId)
                              if (grantable.length === 0) return null
                              return (
                                <div className="assign-field" title="Accounts someone else owns that get played on this machine - their recordings can only come from here">
                                  <span className="assign-field-name">Also acts for</span>
                                  <span className="assign-grants">
                                    {grantable.map(a => (
                                      <label key={a.id} className="assign-grant">
                                        <input type="checkbox" checked={assigning.actsFor.includes(a.id)}
                                          onChange={e => setAssigning({
                                            ...assigning,
                                            actsFor: e.target.checked ? [...assigning.actsFor, a.id] : assigning.actsFor.filter(id => id !== a.id),
                                          })} />
                                        {a.riotId}
                                      </label>
                                    ))}
                                  </span>
                                </div>
                              )
                            })()}
                            <button className="action primary sm-action" disabled={busy === k.id} onClick={() => saveAssign(k)}>Save</button>
                            <button className="action sm-action" disabled={busy === k.id} onClick={() => setAssigning(null)}>Cancel</button>
                          </div>
                        </td>
                      </tr>
                    )}
                    {live?.lastError && canAct(k) && (
                      <tr className="agent-detail-row">
                        <td colSpan={columns}>
                          <div className="agent-error">
                            <span className="agent-error-label">Last error</span>{live.lastError}
                            <button className="dismiss-x" title="Dismiss (comes back only if a new error appears)" onClick={() => act(k.id, 'dismiss-error')}>✕</button>
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
        )}
      </div>

      <div className="card">
        <h2>Install the agent</h2>
        <p className="mut card-intro">
          The agent runs on the gaming PC - no admin rights needed. It records your games, renders the replay clips, publishes to
          YouTube, and updates itself from this site.
        </p>
        <p className="mut sm-text" style={{ margin: '0 0 10px' }}>
          It never opens anything on your screen unless you turn on <strong>post-game review</strong> in its setup window - then,
          about 30 seconds after a game, it opens the replay through your League client, follows your champion through the moments
          that mattered, and closes it again. Recordings go in their own folder; videos you made yourself are never touched.
        </p>
        {release ? (
          <div className="filters" style={{ margin: 0 }}>
            {release.installer && (
              <a className="action primary" href={`/api/agent/release/${encodeURIComponent(release.installer)}`} download>
                Download installer {release.version} ({Math.round(release.installerSizeBytes / 1_000_000)} MB)
              </a>
            )}
            <a className={`action${release.installer ? '' : ' primary'}`} href={`/api/agent/release/${encodeURIComponent(release.file)}`} download title="Portable: unzip anywhere and double-click the exe">
              {release.installer ? 'Portable zip' : `Download agent ${release.version}`} ({Math.round(release.sizeBytes / 1_000_000)} MB)
            </a>
          </div>
        ) : (
          <p className="mut">No agent build published on this tracker yet.</p>
        )}
        <p className="mut sm-text" style={{ margin: '10px 0 0' }}>
          Windows SmartScreen may say "not commonly downloaded" - the build is not code-signed yet. Choose <strong>Keep</strong>{' '}
          in the download bar, then <strong>More info → Run anyway</strong> if the blue box appears.
        </p>
      </div>
    </div>
  )
}

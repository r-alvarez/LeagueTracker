import { useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import { auth } from '../auth'
import type { AgentInfo, AgentKey, MyAgents } from '../types'

interface Release { version: string; file: string; sizeBytes: number; installer: string | null; installerSizeBytes: number }

/// The owner's machines: the agent build to install, a join code that
/// makes a new machine theirs at enrolment, the machines waiting for the
/// Approve click, and the ones running (heartbeats). Admins see everyone's
/// and can hand a machine to its owner.
export default function Machines() {
  const [release, setRelease] = useState<Release | null>(null)
  const [mine, setMine] = useState<MyAgents | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [code, setCode] = useState<{ pretty: string; paste: string; expiresUtc: string; role: string } | null>(null)
  const [role, setRole] = useState<'recorder' | 'renderer'>('recorder')
  const [copied, setCopied] = useState(false)
  const [assign, setAssign] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api.myAgents().then(setMine).catch(() => setMine(null))
  }, [])
  useEffect(() => {
    fetch('/api/agent/release').then(r => (r.status === 200 ? r.json() : null)).then(setRelease).catch(() => setRelease(null))
    load()
    const t = window.setInterval(load, 15000)
    return () => window.clearInterval(t)
  }, [load])

  const act = async (id: string, verb: 'approve' | 'revoke' | 'delete' | 'restart' | 'dismiss-error') => {
    setBusy(id)
    try { await api.agentAction(id, verb); load() } finally { setBusy(null) }
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

  const when = (s: string | null) => (s ? new Date(s).toLocaleString() : '—')
  const keys = mine?.keys ?? []
  const pending = keys.filter(k => k.status === 'pending')
  const known = keys.filter(k => k.status !== 'pending')
  const liveById = new Map<string, AgentInfo>((mine?.live ?? []).map(a => [a.id, a]))
  const doAssign = async (k: AgentKey) => {
    setError(null)
    try { await api.adminAssignAgent(k.id, assign[k.id]?.trim() || null, null); load() } catch (e) { setError(String(e)) }
  }

  return (
    <>
      <div className="card">
        <h2>Machines</h2>
        <p className="mut" style={{ marginTop: 0 }}>
          The agent runs on your gaming PC: it records your games and publishes them to YouTube. Install it (no admin
          rights needed), then in its setup window paste the join code below - the machine appears here as{' '}
          <em>waiting</em>, you press Approve once, and it stays yours until you revoke it. It updates itself from here.
        </p>
        <p style={{ margin: '0 0 10px' }}>
          {release ? (
            <>
              {release.installer && (
                <a className="action primary" href={`/api/agent/release/${encodeURIComponent(release.installer)}`} download>
                  Download installer {release.version} ({Math.round(release.installerSizeBytes / 1_000_000)} MB)
                </a>
              )}{' '}
              <a className={`action${release.installer ? '' : ' primary'}`} href={`/api/agent/release/${encodeURIComponent(release.file)}`} download title="Portable: unzip anywhere and double-click the exe">
                {release.installer ? 'zip' : `Download agent ${release.version}`} ({Math.round(release.sizeBytes / 1_000_000)} MB)
              </a>
            </>
          ) : (
            <span className="mut">No agent build published on this tracker yet.</span>
          )}
        </p>
        <p className="mut sm-text" style={{ margin: '0 0 12px' }}>
          Windows SmartScreen may say "not commonly downloaded" - the build is not code-signed yet. Choose <strong>Keep</strong>{' '}
          in the download bar, then <strong>More info → Run anyway</strong> if the blue box appears.
        </p>
        <div className="agent-join">
          <p style={{ margin: '0 0 8px' }}>
            {auth.isAdmin && (
              <select value={role} onChange={e => setRole(e.target.value as typeof role)} aria-label="Machine role" style={{ marginRight: 8 }}>
                <option value="recorder">Recorder – a player's PC</option>
                <option value="renderer">Renderer – the replay box (serves every account)</option>
              </select>
            )}
            <button className="action primary" onClick={mint}>Add a machine…</button>
            {mine && mine.joinCodes.length > 0 && !code && (
              <span className="mut sm-text" style={{ marginLeft: 10 }}>
                open code{mine.joinCodes.length > 1 ? 's' : ''}: {mine.joinCodes.map(c => `${c.code.slice(0, 4)}-${c.code.slice(4)}`).join(', ')}
              </span>
            )}
          </p>
          {code && (
            <div className="join-code-box">
              <div className="join-code">{code.pretty}</div>
              <p className="mut sm-text" style={{ margin: '4px 0 8px' }}>
                Type this in the agent's setup window as the join code, or paste the one-line version below (it carries the
                site address too). Valid 15 minutes, single use, {code.role} role.
              </p>
              <textarea className="agent-join-code" readOnly value={code.paste} rows={2} onFocus={e => e.currentTarget.select()} />
              <p style={{ margin: '6px 0 0' }}>
                <button className="action" onClick={() => copy(code.paste)}>{copied ? 'Copied' : 'Copy'}</button>
              </p>
            </div>
          )}
          {error && <p className="warn-text sm-text">{error}</p>}
        </div>
      </div>

      {pending.length > 0 && (
        <div className="card">
          <h2>Waiting for approval <span className="badge loss">{pending.length}</span></h2>
          {pending.map(k => (
            <p key={k.id} style={{ margin: '6px 0' }}>
              <span className="badge remake">waiting</span>{' '}
              <strong>{k.name}</strong>{' '}
              <span className="mut sm-text">
                {k.machine} · {k.role} · from {k.lastIp ?? '?'} · asked {when(k.createdUtc)}
                {!k.bound && ' · no join code - not tied to anyone'}{k.ownerEmail && ` · ${k.ownerEmail}`}
              </span>{' '}
              <button className="action primary" disabled={busy === k.id || (!k.bound && !auth.isAdmin)} onClick={() => act(k.id, 'approve')}>Approve</button>{' '}
              <button className="action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Reject</button>
              {auth.isAdmin && !k.bound && (
                <span style={{ marginLeft: 8 }}>
                  <input className="text" placeholder="owner email" value={assign[k.id] ?? ''} onChange={e => setAssign({ ...assign, [k.id]: e.target.value })} style={{ width: 200 }} />{' '}
                  <button className="action" onClick={() => doAssign(k)}>Assign</button>
                </span>
              )}
            </p>
          ))}
        </div>
      )}

      {known.length > 0 && (
        <div className="card">
          <h2>Your machines</h2>
          <div className="agent-list">
            {known.map(k => {
              const a = liveById.get(k.id)
              return (
                <div key={k.id} className="agent-row">
                  <div className="agent-head">
                    <span className={`badge ${k.status === 'revoked' ? 'loss' : a?.online ? (a.paused ? 'remake' : 'win') : 'loss'}`}>
                      {k.status === 'revoked' ? 'revoked' : a?.online ? (a.paused ? 'paused' : 'online') : 'offline'}
                    </span>
                    <strong>{k.name}</strong>
                    <span className="mut sm-text">
                      {k.role}{a ? ` · v${a.version}` : ''}{k.machine ? ` · ${k.machine}` : ''}{k.ownerEmail ? ` · ${k.ownerEmail}` : (!k.bound ? ' · unassigned' : '')}
                    </span>
                    {k.status === 'approved' && a && (
                      <button className="action sm-action" title="Ask this agent to restart on its next heartbeat (when it is idle) - it re-reads settings and updates itself"
                        onClick={() => act(k.id, 'restart')}>Restart</button>
                    )}
                    {k.status === 'approved'
                      ? <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'revoke')}>Revoke</button>
                      : <>
                          <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'approve')}>Re-approve</button>
                          <button className="action sm-action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Delete</button>
                        </>}
                  </div>
                  {a && (
                    <div className="agent-state">
                      <span className="agent-state-name">{a.state}</span>
                      {a.detail && <span className="mut"> — {a.detail}</span>}
                    </div>
                  )}
                  <dl className="agent-meta">
                    <div><dt>Seen</dt><dd>{when(a?.seenUtc ?? k.lastSeenUtc)}</dd></div>
                    {a?.lastRecordingUtc && <div><dt>Last recording</dt><dd>{new Date(a.lastRecordingUtc).toLocaleString()}</dd></div>}
                    {a && !a.youTubeReady && <div><dt>YouTube</dt><dd className="warn-text">not authorized</dd></div>}
                  </dl>
                  {a?.lastError && (
                    <div className="agent-error">
                      <span className="agent-error-label">Last error</span>{a.lastError}
                      <button className="dismiss-x" title="Dismiss (comes back only if a new error appears)" onClick={() => act(k.id, 'dismiss-error')}>✕</button>
                    </div>
                  )}
                  {auth.isAdmin && (
                    <p className="sm-text" style={{ margin: '6px 0 0' }}>
                      <input className="text" placeholder={k.ownerEmail ?? 'owner email'} value={assign[k.id] ?? ''} onChange={e => setAssign({ ...assign, [k.id]: e.target.value })} style={{ width: 220 }} />{' '}
                      <button className="action sm-action" onClick={() => doAssign(k)}>Assign owner</button>
                    </p>
                  )}
                </div>
              )
            })}
          </div>
        </div>
      )}
    </>
  )
}

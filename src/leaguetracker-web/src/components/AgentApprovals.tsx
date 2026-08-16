import { useCallback, useEffect, useState } from 'react'

interface AgentKey {
  id: string; name: string; machine: string; status: 'pending' | 'approved' | 'revoked'
  createdUtc: string; decidedUtc: string | null; lastSeenUtc: string | null; lastIp: string | null; note: string | null
}

/// Enrolled agents: a new machine knocks with only the tracker URL and
/// waits here as "pending" until the owner clicks Approve. Revoke cuts it
/// off at the next request; the machine keeps its key and shows "revoked".
export default function AgentApprovals() {
  const [keys, setKeys] = useState<AgentKey[]>([])
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(() => {
    fetch('/api/agents').then(r => (r.ok ? r.json() : [])).then(setKeys).catch(() => setKeys([]))
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

  if (keys.length === 0) return null
  const pending = keys.filter(k => k.status === 'pending')
  const rest = keys.filter(k => k.status !== 'pending')
  const when = (s: string | null) => (s ? new Date(s).toLocaleString() : '—')

  return (
    <div className="card">
      <h2>Agent access {pending.length > 0 && <span className="badge loss">{pending.length} waiting</span>}</h2>
      <p className="mut" style={{ marginTop: 0 }}>
        Machines that asked to talk to this tracker. A new agent needs only the site URL; approve it here once and it
        stays approved until revoked. Revoke instantly stops a machine (its key stops working); delete forgets it.
      </p>
      {pending.map(k => (
        <p key={k.id} style={{ margin: '6px 0' }}>
          <span className="badge remake">pending</span>{' '}
          <strong>{k.name}</strong> <span className="mut sm-text">{k.machine} · from {k.lastIp ?? '?'} · asked {when(k.createdUtc)}</span>{' '}
          <button className="action primary" disabled={busy === k.id} onClick={() => act(k.id, 'approve')}>Approve</button>{' '}
          <button className="action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Reject</button>
        </p>
      ))}
      {rest.map(k => (
        <p key={k.id} style={{ margin: '4px 0' }}>
          <span className={`badge ${k.status === 'approved' ? 'win' : 'loss'}`}>{k.status}</span>{' '}
          <strong>{k.name}</strong> <span className="mut sm-text">{k.machine} · last seen {when(k.lastSeenUtc)}{k.lastIp ? ` from ${k.lastIp}` : ''}</span>{' '}
          {k.status === 'approved'
            ? <button className="action" disabled={busy === k.id} onClick={() => act(k.id, 'revoke')}>Revoke</button>
            : <>
                <button className="action" disabled={busy === k.id} onClick={() => act(k.id, 'approve')}>Re-approve</button>{' '}
                <button className="action" disabled={busy === k.id} onClick={() => act(k.id, 'delete')}>Delete</button>
              </>}
        </p>
      ))}
    </div>
  )
}

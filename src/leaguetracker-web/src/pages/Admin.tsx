import { useCallback, useEffect, useState } from 'react'
import { account } from '../account'
import { api } from '../api'
import { auth } from '../auth'
import type { AdminUser } from '../types'

/// The admin's view of who is here: every person who has signed in (or was
/// named in configuration), what they own, and the hand that gives a tracked
/// account to its owner or makes someone an admin. Ownership by hand exists
/// for the accounts that predate claiming and for a friend whose profile
/// icon proof is impractical - the claim flow stays the normal road.
export default function People() {
  const [users, setUsers] = useState<AdminUser[] | null>(null)
  const [owner, setOwner] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api.adminUsers().then(setUsers).catch(e => setError(String(e)))
  }, [])
  useEffect(load, [load])

  const run = async (key: string, work: () => Promise<void>) => {
    setBusy(key)
    setError(null)
    try { await work(); load() } catch (e) { setError(String(e)) } finally { setBusy(null) }
  }

  const emailOf = (userId: string | null) => users?.find(u => u.id === userId)?.email ?? null
  const when = (s: string | null) => (s ? new Date(s).toLocaleString() : 'never')

  return (
    <div className="card">
      <h2>People</h2>
      <p className="mut" style={{ marginTop: 0 }}>
        Everyone who has signed in. A person becomes assignable the first time they sign in (or when their email is
        named in configuration); until then their accounts and machines wait unowned.
      </p>
      {users === null ? <p className="mut">Loading…</p> : (
        <div className="table-scroll">
          <table className="data">
            <thead>
              <tr><th>Who</th><th>Accounts</th><th>Machines</th><th>Last seen</th><th></th></tr>
            </thead>
            <tbody>
              {users.map(u => (
                <tr key={u.id}>
                  <td>
                    <strong>{u.displayName || u.email}</strong>
                    <div className="mut sm-text">{u.email}{u.isAdmin && ' · admin'}{u.logins.length === 0 && ' · never signed in'}</div>
                  </td>
                  <td className="sm-text">{u.accounts.length > 0 ? u.accounts.join(', ') : <span className="mut">—</span>}</td>
                  <td className="sm-text">{u.agents > 0 ? u.agents : <span className="mut">—</span>}</td>
                  <td className="sm-text">{when(u.lastSeenUtc)}</td>
                  <td>
                    {u.id !== auth.user?.id && (
                      <button className="action sm-action" disabled={busy === u.id}
                        title={u.isAdmin ? 'Back to an ordinary user' : 'Admins see and manage every account and machine'}
                        onClick={() => run(u.id, () => api.adminSetUserAdmin(u.id, !u.isAdmin))}>
                        {u.isAdmin ? 'Remove admin' : 'Make admin'}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h3 style={{ margin: '16px 0 4px' }}>Tracked accounts</h3>
      <p className="mut sm-text" style={{ marginTop: 0 }}>
        Who owns which Riot account. Leave the box empty and press Set to make an account unowned again.
      </p>
      <div className="agent-list">
        {account.all.map(a => (
          <p key={a.id} style={{ margin: '6px 0' }}>
            <strong>{a.riotId}</strong>{' '}
            <span className="mut sm-text">{a.region} · {a.owned ? (emailOf(a.ownerUserId) ?? 'owned') : 'unowned'}</span>{' '}
            <input className="text" placeholder="owner email" value={owner[a.id] ?? ''} style={{ width: 220 }}
              onChange={e => setOwner({ ...owner, [a.id]: e.target.value })} />{' '}
            <button className="action sm-action" disabled={busy === a.id}
              onClick={() => run(a.id, async () => {
                await api.adminSetAccountOwner(a.id, owner[a.id]?.trim() || null)
                // The accounts list is loaded once at boot; a full reload is how it refreshes.
                window.location.reload()
              })}>Set</button>
          </p>
        ))}
      </div>
      {error && <p className="warn-text sm-text">{error}</p>}
    </div>
  )
}

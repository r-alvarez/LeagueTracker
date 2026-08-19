import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { account } from '../account'
import { api } from '../api'
import { auth } from '../auth'
import OwnerSelect from '../components/OwnerSelect'
import type { AdminUser } from '../types'

/// The admin's view of who is here: every person who has signed in (or was
/// named in configuration), what they own, and the hand that gives a tracked
/// account to its owner or makes someone an admin. Ownership by hand exists
/// for the accounts that predate claiming and for a friend whose profile
/// icon proof is impractical - the claim flow stays the normal road.
export default function Admin() {
  const [users, setUsers] = useState<AdminUser[] | null>(null)
  // Per account, the owner picked in the select but not yet saved; an owned
  // account shows its owner as text until "Change" opens the select.
  const [picked, setPicked] = useState<Record<string, string>>({})
  const [editing, setEditing] = useState<Record<string, boolean>>({})
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    if (!auth.isAdmin) return
    api.adminUsers().then(setUsers).catch(e => setError(String(e)))
  }, [])
  useEffect(load, [load])

  if (!auth.isAdmin) {
    return (
      <div className="card signin-wall">
        <h2>Nothing here for you</h2>
        <p className="mut">This page is for the tracker's admins. Your own machines are under your name, top right.</p>
      </div>
    )
  }

  const run = async (key: string, work: () => Promise<void>) => {
    setBusy(key)
    setError(null)
    try { await work(); load() } catch (e) { setError(String(e)) } finally { setBusy(null) }
  }

  const emailOf = (userId: string | null) => users?.find(u => u.id === userId)?.email ?? ''
  const when = (s: string | null) => (s ? new Date(s).toLocaleString() : 'never')

  return (
    <div className="grid" style={{ gap: 14 }}>
      <div className="card">
        <h2>People</h2>
        <p className="mut card-intro">
          Everyone who has signed in. A person becomes assignable the first time they sign in (or when their email is
          named in configuration); until then their accounts and machines wait unowned. Machines - everyone's, with owner
          and role - are on <Link to="/machines">your machines page</Link>.
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
                      {u.isAdmin && <span className="badge remake tag">admin</span>}
                      <div className="mut sm-text">{u.email}{u.logins.length === 0 && ' · never signed in'}</div>
                    </td>
                    <td className="sm-text">{u.accounts.length > 0 ? u.accounts.join(', ') : <span className="mut">—</span>}</td>
                    <td className="sm-text">{u.agents > 0 ? u.agents : <span className="mut">—</span>}</td>
                    <td className="sm-text">{when(u.lastSeenUtc)}</td>
                    <td className="actions">
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
      </div>

      <div className="card">
        <h2>Tracked accounts</h2>
        <p className="mut card-intro">
          Who owns which Riot account. Owners run syncs, manage what the profile shows and get their games recorded;
          pick <em>nobody</em> to make an account unowned again.
        </p>
        <div className="table-scroll">
          <table className="data">
            <thead>
              <tr><th>Account</th><th>Region</th><th>Owner</th><th></th></tr>
            </thead>
            <tbody>
              {account.all.map(a => {
                const current = emailOf(a.ownerUserId)
                const value = picked[a.id] ?? current
                const changed = value !== current
                const open = !a.owned || editing[a.id] === true
                const save = () => run(a.id, async () => {
                  await api.adminSetAccountOwner(a.id, value || null)
                  // The accounts list is loaded once at boot; a full reload is how it refreshes.
                  window.location.reload()
                })
                return (
                  <tr key={a.id}>
                    <td><strong>{a.riotId}</strong>{!a.owned && <span className="mut sm-text"> · unowned</span>}</td>
                    <td className="sm-text">{a.region.toUpperCase()}</td>
                    <td>
                      {open
                        ? <OwnerSelect users={users ?? []} value={value} disabled={busy === a.id}
                            onChange={email => setPicked({ ...picked, [a.id]: email })} />
                        : (current || <span className="mut">owned</span>)}
                    </td>
                    <td className="actions">
                      {open ? <>
                        <button className="action primary sm-action" disabled={busy === a.id || !changed} onClick={save}>Save</button>
                        {a.owned && (
                          <button className="action sm-action" disabled={busy === a.id}
                            onClick={() => { setEditing({ ...editing, [a.id]: false }); setPicked({ ...picked, [a.id]: current }) }}>Cancel</button>
                        )}
                      </> : (
                        <button className="action sm-action" onClick={() => setEditing({ ...editing, [a.id]: true })}>Change</button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </div>

      {error && <p className="warn-text sm-text">{error}</p>}
    </div>
  )
}

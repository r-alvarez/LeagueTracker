import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { account } from '../account'
import { api } from '../api'
import { auth } from '../auth'
import OwnerSelect from '../components/OwnerSelect'
import type { AdminUser, AdminUsers, InviteResult } from '../types'

/// The admin's view of who is here: every person invited, signed in or named
/// in configuration, what they own, and the hands that invite someone, give a
/// tracked account to its owner or make someone an admin. Ownership by hand
/// exists for the accounts that predate claiming and for a friend whose
/// profile icon proof is impractical - the claim flow stays the normal road.
export default function Admin() {
  const [people, setPeople] = useState<AdminUsers | null>(null)
  const users = people?.users ?? null
  // Per account, the owner picked in the select but not yet saved; an owned
  // account shows its owner as text until "Change" opens the select.
  const [picked, setPicked] = useState<Record<string, string>>({})
  const [editing, setEditing] = useState<Record<string, boolean>>({})
  const [busy, setBusy] = useState<string | null>(null)
  // One line per card says how the last action went - success or failure -
  // and the next action replaces it.
  const [notice, setNotice] = useState<{ where: 'people' | 'accounts'; kind: 'ok' | 'warn'; text: string } | null>(null)
  const [adding, setAdding] = useState(false)
  const [inviteEmail, setInviteEmail] = useState('')
  const [inviteName, setInviteName] = useState('')
  const [link, setLink] = useState<{ email: string; url: string; expiresUtc: string } | null>(null)
  const [copied, setCopied] = useState(false)

  const warn = (where: 'people' | 'accounts', text: string) => setNotice({ where, kind: 'warn', text })

  const load = useCallback(() => {
    if (!auth.isAdmin) return
    api.adminUsers().then(setPeople).catch(e => warn('people', String(e)))
  }, [])
  useEffect(load, [load])

  const afterInvite = (r: InviteResult, verb: 'added' | 'resent') => {
    if (r.mailed) setNotice({ where: 'people', kind: 'ok', text: `Invite sent to ${r.user.email} - Auth0 mailed them a link to set their password.` })
    else warn('people', verb === 'added' ? `${r.user.email} added. ${r.warning ?? 'No invite went out.'}` : r.warning ?? 'No invite went out.')
    load()
  }
  const invite = async () => {
    setBusy('invite')
    setNotice(null)
    try {
      afterInvite(await api.adminInvite(inviteEmail.trim(), inviteName.trim() || null), 'added')
      setInviteEmail('')
      setInviteName('')
      setAdding(false)
    } catch (e) { warn('people', String(e)) } finally { setBusy(null) }
  }
  const showLink = async (u: AdminUser) => {
    setBusy(u.id)
    setNotice(null)
    try {
      const r = await api.adminInviteLink(u.id)
      setLink({ email: u.email, url: r.url, expiresUtc: r.expiresUtc })
      setCopied(false)
    } catch (e) { warn('people', String(e)) } finally { setBusy(null) }
  }
  const copyLink = async () => {
    if (!link) return
    try { await navigator.clipboard.writeText(link.url); setCopied(true) } catch { /* selectable text stays */ }
  }

  if (!auth.isAdmin) {
    return (
      <div className="card signin-wall">
        <h2>Nothing here for you</h2>
        <p className="mut">This page is for the tracker's admins. Your own machines are under your name, top right.</p>
      </div>
    )
  }

  const run = async (key: string, work: () => Promise<void>, where: 'people' | 'accounts' = 'people') => {
    setBusy(key)
    setNotice(null)
    try { await work(); load() } catch (e) { warn(where, String(e)) } finally { setBusy(null) }
  }
  const noticeIn = (where: 'people' | 'accounts') => notice?.where === where && <p className={`invite-notice ${notice.kind}`}>{notice.text}</p>

  const emailOf = (userId: string | null) => users?.find(u => u.id === userId)?.email ?? ''
  const when = (s: string | null) => (s ? new Date(s).toLocaleString() : 'never')
  const whenShort = (s: string) => new Date(s).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

  return (
    <div className="grid" style={{ gap: 14 }}>
      <div className="card">
        <div className="card-head">
          <h2>People</h2>
          <div className="card-head-actions">
            <button className="action primary" onClick={() => { setAdding(a => !a); setNotice(null) }}>Add a person…</button>
          </div>
        </div>
        <p className="mut card-intro">
          Everyone who has been invited, has signed in, or is named in configuration - and what they own. Adding a person
          creates their sign-in at Auth0 and has Auth0 mail them a link to set a password; they are assignable here at
          once. Machines - everyone's, with owner and role - are on <Link to="/machines">your machines page</Link>.
        </p>
        {adding && (
          <form className="invite-form" onSubmit={e => { e.preventDefault(); if (busy !== 'invite' && inviteEmail.includes('@')) void invite() }}>
            <label>Email
              <input className="text" type="email" value={inviteEmail} onChange={e => setInviteEmail(e.target.value)} placeholder="friend@example.com" autoFocus disabled={busy === 'invite'} />
            </label>
            <label>Name <span className="mut">(optional)</span>
              <input className="text" value={inviteName} onChange={e => setInviteName(e.target.value)} placeholder="How they show up here" disabled={busy === 'invite'} />
            </label>
            <button type="submit" className="action primary" disabled={busy === 'invite' || !inviteEmail.includes('@')}>{busy === 'invite' ? 'Inviting…' : 'Send invite'}</button>
            <button type="button" className="action" disabled={busy === 'invite'} onClick={() => { setAdding(false); setInviteEmail(''); setInviteName('') }}>Cancel</button>
            {people && !people.invitesConfigured && (
              <p className="warn-text sm-text">Auth0 management is not configured on this instance: the person is added here, but no sign-in is created and no mail goes out.</p>
            )}
          </form>
        )}
        {noticeIn('people')}
        {link && (
          <div className="join-code-box">
            <p className="mut sm-text">
              Invite link for <strong>{link.email}</strong> - the same link the mail carries. Valid until {new Date(link.expiresUtc).toLocaleString()}; single use.
            </p>
            <textarea className="agent-join-code" readOnly value={link.url} rows={2} onFocus={e => e.currentTarget.select()} />
            <div className="filters" style={{ margin: '8px 0 0' }}>
              <button className="action" onClick={copyLink}>{copied ? 'Copied' : 'Copy'}</button>
              <button className="action" onClick={() => setLink(null)}>Done</button>
            </div>
          </div>
        )}
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
                      {u.invited && <span className="badge remake tag">invited</span>}
                      <div className="mut sm-text">
                        {u.email}
                        {u.invited && (u.inviteSentUtc ? ` · invite sent ${whenShort(u.inviteSentUtc)}` : ' · no invite sent yet')}
                        {!u.invited && u.lastSeenUtc === null && ' · never signed in'}
                      </div>
                    </td>
                    <td className="sm-text">{u.accounts.length > 0 ? u.accounts.join(', ') : <span className="mut">—</span>}</td>
                    <td className="sm-text">{u.agents > 0 ? u.agents : <span className="mut">—</span>}</td>
                    <td className="sm-text">{u.invited && u.invitedUtc ? <span className="mut">invited {whenShort(u.invitedUtc)}</span> : when(u.lastSeenUtc)}</td>
                    <td className="actions">
                      {u.invited && <>
                        <button className="action sm-action" disabled={busy === u.id} title="Have Auth0 mail the set-your-password link again"
                          onClick={() => run(u.id, async () => afterInvite(await api.adminReinvite(u.id), 'resent'))}>Resend invite</button>
                        <button className="action sm-action" disabled={busy === u.id} title="Get the link to hand over yourself (when the mail does not arrive)"
                          onClick={() => showLink(u)}>Copy link</button>
                        <button className="action sm-action" disabled={busy === u.id} title="Remove the invite - here and at Auth0"
                          onClick={() => run(u.id, () => api.adminRemoveInvited(u.id))}>Remove</button>
                      </>}
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
                }, 'accounts')
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
        {noticeIn('accounts')}
      </div>
    </div>
  )
}

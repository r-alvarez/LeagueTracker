import { useState } from 'react'
import { account, type AccountInfo } from '../account'

const ADD = '__add__'

/// The header's account control: the player pill as a select over the
/// tracked accounts, with "Add account…" at the bottom. Adding is a Riot
/// ID + region typed by a person: the server checks it against Riot,
/// gives it a folder and a database, and we land on its page.
export default function AccountSwitch() {
  const [adding, setAdding] = useState(false)
  const [riotId, setRiotId] = useState('')
  const [region, setRegion] = useState(account.current.region || 'euw')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const onPick = (value: string) => {
    if (value === ADD) { setAdding(true); return }
    account.switchTo(value)
  }

  const submit = async () => {
    setBusy(true)
    setError(null)
    try {
      const resp = await fetch('/api/accounts', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ riotId: riotId.trim(), region }),
      })
      const body = await resp.json().catch(() => ({}))
      if (resp.status === 409 && body.account) { account.goTo(body.account as AccountInfo); return }
      if (!resp.ok) { setError(body.error ?? body.detail ?? `HTTP ${resp.status}`); return }
      account.goTo(body as AccountInfo)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <span className="account-control">
      <label className="player account-switch" title="Switch account">
        <select value={account.current.slug} onChange={e => onPick(e.target.value)} aria-label="Account">
          {account.all.map(a => (
            <option key={a.slug} value={a.slug}>{a.label} · {a.riotId}{a.available ? '' : ' · unavailable'}</option>
          ))}
          {account.canAdd && <option value={ADD}>＋ Add account…</option>}
        </select>
      </label>
      {adding && (
        <form className="account-add" onSubmit={e => { e.preventDefault(); if (!busy) void submit() }}>
          <input
            autoFocus
            placeholder="GameName#TAG"
            value={riotId}
            onChange={e => setRiotId(e.target.value)}
            aria-label="Riot ID"
            disabled={busy}
          />
          <select value={region} onChange={e => setRegion(e.target.value)} aria-label="Region" disabled={busy}>
            {account.regions.map(r => <option key={r.code} value={r.code}>{r.code.toUpperCase()} · {r.label}</option>)}
          </select>
          <button type="submit" className="action primary" disabled={busy || !riotId.includes('#')}>{busy ? 'Checking…' : 'Track'}</button>
          <button type="button" className="action" onClick={() => { setAdding(false); setError(null) }} disabled={busy}>Cancel</button>
          {error && <span className="account-add-error">{error}</span>}
        </form>
      )}
    </span>
  )
}

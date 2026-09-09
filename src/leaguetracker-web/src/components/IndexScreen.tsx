import { useState } from 'react'
import { account, pathOf, slugOf } from '../account'
import { auth } from '../auth'
import Footer from './Footer'

// The front page lists only the visitor's own accounts: the population was
// a directory of everyone's owner and rename history for anyone who asked.
export default function IndexScreen() {
  const regionLabel = (code: string) => account.regions.find(r => r.code === code)?.label ?? code.toUpperCase()
  const accounts = [...account.all].sort((a, b) =>
    (a.slug === account.defaultSlug ? 0 : 1) - (b.slug === account.defaultSlug ? 0 : 1) || a.label.localeCompare(b.label))
  const user = auth.user
  const [region, setRegion] = useState(accounts[0]?.region ?? account.regions[0]?.code ?? 'euw')
  const [name, setName] = useState('')

  const open = () => {
    const slug = slugOf(name)
    if (slug) window.location.assign(`/${region}/${encodeURIComponent(slug)}/`)
  }

  return (
    <div className="signin-screen">
      <main className="signin index">
        <img className="signin-logo" src="/favicon.svg" alt="" width={44} height={42} />
        <h1>LeagueTracker</h1>
        <p className="index-lede mut">
          Every game of the players tracked here, reviewed for how it was played, not just how it ended.
        </p>
        {accounts.length > 0 && (
          <>
            <h2 className="index-heading">Your accounts</h2>
            <ul className="index-accounts">
              {accounts.map(a => (
                <li key={a.slug}>
                  <a className="index-account" href={`${pathOf(a)}/`}>
                    <span className="index-label">{a.label}</span>
                    <span className="mut">{a.riotId} · {regionLabel(a.region)}{a.available ? '' : ' · unavailable'}</span>
                  </a>
                </li>
              ))}
            </ul>
          </>
        )}
        <form className="signin-panel index-open" onSubmit={e => { e.preventDefault(); open() }}>
          <p className="mut">Open a tracked account by its Riot ID.</p>
          <div className="account-add">
            <select value={region} onChange={e => setRegion(e.target.value)} aria-label="Region">
              {account.regions.map(r => <option key={r.code} value={r.code}>{r.code.toUpperCase()} · {r.label}</option>)}
            </select>
            <input
              placeholder="GameName#TAG"
              value={name}
              onChange={e => setName(e.target.value)}
              aria-label="Riot ID"
            />
            <button type="submit" className="action primary" disabled={!slugOf(name)}>Open</button>
          </div>
        </form>
        <p className="signin-help mut">
          {user
            ? <>Signed in as {user.displayName || user.email} · <a href={auth.logoutUrl('/')}>Sign out</a></>
            : <a href={auth.loginUrl('/')}>Sign in</a>}
        </p>
      </main>
      <Footer />
    </div>
  )
}

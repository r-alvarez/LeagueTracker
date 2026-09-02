import { account, pathOf } from '../account'
import { auth } from '../auth'
import Footer from './Footer'

// The front page: what this tracker is and who it follows. Every account is
// one click away; nobody's dashboard is borrowed as the home page.
export default function IndexScreen() {
  const regionLabel = (code: string) => account.regions.find(r => r.code === code)?.label ?? code.toUpperCase()
  const accounts = [...account.all].sort((a, b) =>
    (a.slug === account.defaultSlug ? 0 : 1) - (b.slug === account.defaultSlug ? 0 : 1) || a.label.localeCompare(b.label))
  const user = auth.user

  return (
    <div className="signin-screen">
      <main className="signin index">
        <img className="signin-logo" src="/favicon.svg" alt="" width={44} height={42} />
        <h1>LeagueTracker</h1>
        <p className="index-lede mut">
          Every game of the players tracked here, reviewed for how it was played, not just how it ended.
        </p>
        {accounts.length > 0 ? (
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
        ) : (
          <div className="signin-panel">
            <p className="mut">No accounts are tracked yet.</p>
          </div>
        )}
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

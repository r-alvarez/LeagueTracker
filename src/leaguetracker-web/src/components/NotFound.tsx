import { Link, useLocation } from 'react-router-dom'
import { account, pathOf, type Resolution } from '../account'
import { auth } from '../auth'
import Footer from './Footer'

// A URL that names no account, or an account this tracker does not follow:
// the whole page says so - no tabs, no dashboard borrowed from someone else.
// The tracked accounts are offered when the visitor may read them, so a typo
// is one click from the right page.
export default function NotFound({ resolution }: { resolution: Exclude<Resolution, { kind: 'account' | 'index' }> }) {
  const readable = account.all
  const regionLabel = (code: string) => account.regions.find(r => r.code === code)?.label ?? code.toUpperCase()

  return (
    <div className="signin-screen">
      <main className="signin notfound">
        <img className="signin-logo" src="/favicon.svg" alt="" width={44} height={42} />
        <h1>{resolution.kind === 'unknownAccount' ? 'No tracked account here' : 'There is no page here'}</h1>
        <div className="signin-panel">
          {resolution.kind === 'unknownAccount' ? (
            <p className="mut">
              Nothing is tracked as <b>{resolution.slug.replace(/-([^-]*)$/, '#$1')}</b> in {regionLabel(resolution.region)}.
              {resolution.suggestion && <> The same name is tracked in {regionLabel(resolution.suggestion.region)}.</>}
            </p>
          ) : (
            <p className="mut"><code>{resolution.path}</code> does not lead anywhere on this tracker.</p>
          )}
          {resolution.kind === 'unknownAccount' && resolution.suggestion && (
            <a className="action primary signin-cta" href={`${pathOf(resolution.suggestion)}/`}>
              Open {resolution.suggestion.riotId} in {regionLabel(resolution.suggestion.region)}
            </a>
          )}
          {readable.length > 0 ? (
            <ul className="notfound-accounts">
              {readable.map(a => (
                <li key={a.slug}>
                  <a href={`${pathOf(a)}/`}>{a.label} <span className="mut">· {a.riotId} · {regionLabel(a.region)}</span></a>
                </li>
              ))}
            </ul>
          ) : (
            !auth.signedIn && <a className="action primary signin-cta" href={auth.loginUrl('/')}>Sign in</a>
          )}
        </div>
        <p className="signin-help mut"><a href="/">Front page</a></p>
      </main>
      <Footer />
    </div>
  )
}

// A path under a tracked account that matches no page: the shell stays,
// because the account is right, only the page is not.
export function RouteNotFound() {
  const { pathname } = useLocation()
  return (
    <div className="card notfound-card">
      <h2>There is no page at <code>{pathname}</code></h2>
      <p className="mut">Nothing on {account.current.riotId}'s tracker answers to that address.</p>
      <p className="notfound-links">
        <Link to="/">Dashboard</Link>
        <Link to="/matches">Matches</Link>
        <Link to="/coach">Coach</Link>
      </p>
    </div>
  )
}

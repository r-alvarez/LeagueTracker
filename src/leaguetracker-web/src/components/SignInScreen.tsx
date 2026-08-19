import { useState } from 'react'
import { auth } from '../auth'
import Footer from './Footer'

/// The whole page for a visitor who is not signed in: nothing of the
/// tracker shows until they are. Logo, title, one panel, one button - the
/// provider does the rest. On a Development instance without a provider the
/// dev sign-in takes the button's place, so a local review does not need a
/// hand-typed URL.
export default function SignInScreen() {
  const [email, setEmail] = useState('')
  const dev = !auth.state.loginConfigured && auth.state.devLogin
  const returnTo = window.location.pathname + window.location.search
  const devUrl = `/api/auth/dev-login?email=${encodeURIComponent(email.trim())}&admin=true&returnUrl=${encodeURIComponent(returnTo)}`

  return (
    <div className="signin-screen">
      <main className="signin">
        <img className="signin-logo" src="/favicon.svg" alt="" width={44} height={42} />
        <h1>Sign in to LeagueTracker</h1>
        <div className="signin-panel">
          <p className="mut">
            Games, coaching and recordings for the players tracked here. The tracker is invite-only - sign in with the
            email you were invited with.
          </p>
          {dev ? (
            <form className="signin-dev" onSubmit={e => { e.preventDefault(); if (email.includes('@')) window.location.assign(devUrl) }}>
              <input className="text" type="email" placeholder="you@example.com" value={email} onChange={e => setEmail(e.target.value)} aria-label="Email" autoFocus />
              <button type="submit" className="action primary signin-cta" disabled={!email.includes('@')}>Sign in</button>
              <p className="mut sm-text">Development instance - no identity provider configured, so this signs you in as an admin with that email.</p>
            </form>
          ) : (
            <a className="action primary signin-cta" href={auth.loginUrl()}>Sign in</a>
          )}
          {!auth.state.loginConfigured && !dev && (
            <p className="warn-text sm-text">Sign-in is not configured on this instance yet - tell its admin.</p>
          )}
        </div>
        <p className="signin-help mut">Not invited? There is nothing to see here yet.</p>
      </main>
      <Footer />
    </div>
  )
}

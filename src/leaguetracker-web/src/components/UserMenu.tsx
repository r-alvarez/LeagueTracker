import { auth } from '../auth'

/// The header's identity control: who is signed in and the way out, or the
/// way in. Navigation, not fetch - the login round-trips the provider.
export default function SignInPill() {
  if (!auth.signedIn || !auth.user) {
    return <a className="signin-pill" href={auth.loginUrl()}>Sign in</a>
  }
  return (
    <span className="signin-pill signed-in" title={auth.user.email}>
      <span className="who">{auth.user.displayName || auth.user.email}{auth.isAdmin ? ' · admin' : ''}</span>
      <a href={auth.logoutUrl()} className="signout">Sign out</a>
    </span>
  )
}

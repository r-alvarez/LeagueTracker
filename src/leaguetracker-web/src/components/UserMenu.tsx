import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { auth } from '../auth'

const stroke = { fill: 'none', stroke: 'currentColor', strokeWidth: 1.7, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }

function MenuIcon({ kind }: { kind: 'machines' | 'admin' | 'signout' }) {
  return (
    <svg viewBox="0 0 24 24" width={15} height={15} aria-hidden>
      {kind === 'machines' && <path {...stroke} d="M3 5.5h18v10H3zM9 20h6M12 15.5V20" />}
      {kind === 'admin' && <path {...stroke} d="M12 3l7 3v5c0 5-3.5 8.2-7 10-3.5-1.8-7-5-7-10V6zM9 12l2 2 4-4" />}
      {kind === 'signout' && <path {...stroke} d="M10 4H5v16h5M14 8l4 4-4 4M18 12H9" />}
    </svg>
  )
}

/// The header's identity control. Signed out it is the way in; signed in it
/// is the person's menu - their machines, the admin area when they are one,
/// and the way out. Navigation, not fetch: sign-in/out round-trip the provider.
export default function UserMenu() {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onPointer = (e: PointerEvent) => { if (!root.current?.contains(e.target as Node)) setOpen(false) }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', onPointer)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onPointer)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  if (!auth.signedIn || !auth.user) {
    return <a className="signin-pill" href={auth.loginUrl()}>Sign in</a>
  }

  const name = auth.user.displayName || auth.user.email
  const initial = name.trim().charAt(0).toUpperCase() || '?'
  const close = () => setOpen(false)

  return (
    <div className="user-menu" ref={root}>
      <button type="button" className={`signin-pill signed-in${open ? ' open' : ''}`} aria-haspopup="menu" aria-expanded={open}
        title={auth.user.email} onClick={() => setOpen(o => !o)}>
        <span className="avatar" aria-hidden>{initial}</span>
        <span className="who">{name}</span>
        <span className="caret" aria-hidden />
      </button>
      {open && (
        <div className="menu" role="menu" aria-label="Account menu">
          <div className="menu-head">
            <div className="menu-name">{name}{auth.isAdmin && <span className="menu-tag">admin</span>}</div>
            {name !== auth.user.email && <div className="menu-email">{auth.user.email}</div>}
          </div>
          <Link role="menuitem" to="/machines" onClick={close}><MenuIcon kind="machines" />Your machines</Link>
          {auth.isAdmin && <Link role="menuitem" to="/admin" onClick={close}><MenuIcon kind="admin" />Admin</Link>}
          <div className="menu-sep" role="separator" />
          <a role="menuitem" href={auth.logoutUrl()}><MenuIcon kind="signout" />Sign out</a>
        </div>
      )}
    </div>
  )
}

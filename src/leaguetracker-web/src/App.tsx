import { useEffect, useState } from 'react'
import { NavLink, Route, Routes } from 'react-router-dom'
import { api } from './api'
import { account } from './account'
import { auth } from './auth'
import AccountSwitch from './components/AccountSwitch'
import UserMenu from './components/UserMenu'
import type { Status } from './types'
import Dashboard from './pages/Dashboard'
import Matches from './pages/Matches'
import MatchDetail from './pages/MatchDetail'
import DataPage from './pages/DataPage'
import Coach from './pages/Coach'
import Fundamentals from './pages/Fundamentals'
import Gameplans from './pages/Gameplans'
import Machines from './pages/Machines'
import Admin from './pages/Admin'
import LiveGameBanner from './components/LiveGameBanner'
import StopLossBanner from './components/StopLossBanner'
import SignInScreen from './components/SignInScreen'
import IndexScreen from './components/IndexScreen'
import NotFound, { RouteNotFound } from './components/NotFound'
import Footer from './components/Footer'

export default function App() {
  const [status, setStatus] = useState<Status | null>(null)
  const resolution = account.resolution

  useEffect(() => {
    if (!auth.canRead || resolution.kind !== 'account') return
    api.status().then(setStatus).catch(() => setStatus(null))
  }, [resolution.kind])

  // Signed out on a private tracker: not the shell with a wall in it, the
  // sign-in screen alone - no tabs, no account names, nothing to read.
  if (!auth.canRead) return <SignInScreen />
  // The URL named no account: the front page, or a plain "nothing here" -
  // never the shell around somebody else's dashboard.
  if (resolution.kind === 'index') return <IndexScreen />
  if (resolution.kind !== 'account') return <NotFound resolution={resolution} />

  // Month-level dates and a patch range keep the scope line one calm phrase;
  // the full patch list lives in the tooltip for anyone who wants it.
  const month = (d: string) =>
    new Date(`${d}T00:00:00`).toLocaleDateString(undefined, { month: 'short', year: 'numeric' })
  const patches = status?.patches ?? []
  const scope = status && status.matches > 0
    ? `${status.matches} games` +
      (patches.length > 0
        ? ` · patch ${patches[0]}${patches.length > 1 ? ` → ${patches[patches.length - 1]}` : ''}`
        : '') +
      (status.dateFrom && status.dateTo ? ` · ${month(status.dateFrom)} → ${month(status.dateTo)}` : '')
    : null

  return (
    <div className="shell">
      <header className="topbar">
        <h1><img className="brand-mark" src="/favicon.svg" alt="" />LeagueTracker</h1>
        {account.all.length > 1 || account.canAdd ? <AccountSwitch /> : status && <span className="player">{status.riotId}</span>}
        {scope && <span className="sub" title={patches.length > 1 ? `patches ${patches.join(', ')}` : undefined}>{scope}</span>}
        <UserMenu />
      </header>

      <nav className="tabs">
        <NavLink to="/" end className={({ isActive }) => (isActive ? 'active' : '')}>Dashboard</NavLink>
        <NavLink to="/coach" className={({ isActive }) => (isActive ? 'active' : '')}>Coach</NavLink>
        <NavLink to="/fundamentals" className={({ isActive }) => (isActive ? 'active' : '')}>Fundamentals</NavLink>
        <NavLink to="/gameplans" className={({ isActive }) => (isActive ? 'active' : '')}>Gameplans</NavLink>
        <NavLink to="/matches" className={({ isActive }) => (isActive ? 'active' : '')}>Matches</NavLink>
        <NavLink to="/data" className={({ isActive }) => (isActive ? 'active' : '')}>Data & sync</NavLink>
      </nav>

      {!account.current.available && (
        <div className="card" role="alert" style={{ marginBottom: 16, borderLeft: '3px solid var(--warn)' }}>
          <b>This account's data is unavailable right now</b> — its database could not be opened
          {account.current.unavailable ? ` (${account.current.unavailable})` : ''}. The tracker retries every minute; the other accounts are unaffected.
        </div>
      )}
      <LiveGameBanner />
      <StopLossBanner />

      <Routes>
        <Route path="/" element={<Dashboard />} />
        <Route path="/coach" element={<Coach />} />
        <Route path="/fundamentals" element={<Fundamentals />} />
        <Route path="/gameplans" element={<Gameplans />} />
        <Route path="/matches" element={<Matches />} />
        <Route path="/matches/:id" element={<MatchDetail />} />
        <Route path="/data" element={<DataPage />} />
        <Route path="/machines" element={<Machines />} />
        <Route path="/admin" element={<Admin />} />
        <Route path="*" element={<RouteNotFound />} />
      </Routes>

      <Footer />
    </div>
  )
}

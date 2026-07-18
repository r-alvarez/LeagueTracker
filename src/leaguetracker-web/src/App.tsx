import { useEffect, useState } from 'react'
import { NavLink, Route, Routes } from 'react-router-dom'
import { api } from './api'
import type { Status } from './types'
import Dashboard from './pages/Dashboard'
import Matches from './pages/Matches'
import MatchDetail from './pages/MatchDetail'
import DataPage from './pages/DataPage'
import Coach from './pages/Coach'
import Fundamentals from './pages/Fundamentals'
import LiveGameBanner from './components/LiveGameBanner'

export default function App() {
  const [status, setStatus] = useState<Status | null>(null)

  useEffect(() => {
    api.status().then(setStatus).catch(() => setStatus(null))
  }, [])

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
        <h1>LeagueTracker</h1>
        {status && <span className="player">{status.riotId}</span>}
        {scope && <span className="sub" title={patches.length > 1 ? `patches ${patches.join(', ')}` : undefined}>{scope}</span>}
      </header>

      <nav className="tabs">
        <NavLink to="/" end className={({ isActive }) => (isActive ? 'active' : '')}>Dashboard</NavLink>
        <NavLink to="/coach" className={({ isActive }) => (isActive ? 'active' : '')}>Coach</NavLink>
        <NavLink to="/fundamentals" className={({ isActive }) => (isActive ? 'active' : '')}>Fundamentals</NavLink>
        <NavLink to="/matches" className={({ isActive }) => (isActive ? 'active' : '')}>Matches</NavLink>
        <NavLink to="/data" className={({ isActive }) => (isActive ? 'active' : '')}>Data & sync</NavLink>
      </nav>

      <LiveGameBanner />

      <Routes>
        <Route path="/" element={<Dashboard />} />
        <Route path="/coach" element={<Coach />} />
        <Route path="/fundamentals" element={<Fundamentals />} />
        <Route path="/matches" element={<Matches />} />
        <Route path="/matches/:id" element={<MatchDetail />} />
        <Route path="/data" element={<DataPage />} />
      </Routes>

      <footer className="footer">
        LeagueTracker is a personal, non-commercial tool. It isn't endorsed by Riot Games and doesn't reflect the views
        or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot
        Games and League of Legends are trademarks or registered trademarks of Riot Games, Inc.
      </footer>
    </div>
  )
}

import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { MatchSummary } from '../types'
import ChampBadge from '../components/ChampBadge'
import Loadout from '../components/Loadout'

const PAGE_SIZE = 25

export default function Matches() {
  const [items, setItems] = useState<MatchSummary[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [rankedOnly, setRankedOnly] = useState(true)
  const navigate = useNavigate()

  useEffect(() => {
    api.matches(page, PAGE_SIZE, rankedOnly ? true : undefined)
      .then(p => { setItems(p.items); setTotal(p.total) })
      .catch(console.error)
  }, [page, rankedOnly])

  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))

  return (
    <div className="card">
      <div className="filters">
        <div className="seg">
          <button className={rankedOnly ? 'on' : ''} onClick={() => { setRankedOnly(true); setPage(1) }}>Ranked</button>
          <button className={!rankedOnly ? 'on' : ''} onClick={() => { setRankedOnly(false); setPage(1) }}>All queues</button>
        </div>
        <span className="mut">{total} games</span>
      </div>

      <div className="table-scroll">
        <table className="data">
          <thead>
            <tr>
              <th>Date</th><th>Champion</th><th>Result</th><th>vs Laner</th><th>vs Jungler</th>
              <th className="num">K/D/A</th><th className="num">KP</th>
              <th className="num">CS@10</th><th className="num">G@10</th><th>Build</th><th className="num">Min</th>
              <th>Avg enemy rank</th><th className="num">LP</th>
            </tr>
          </thead>
          <tbody>
            {items.map(m => (
              <tr key={m.id} className={m.win ? 'row-win' : 'row-loss'} style={{ cursor: 'pointer' }} onClick={() => navigate(`/matches/${m.id}`)}>
                <td><Link to={`/matches/${m.id}`} onClick={e => e.stopPropagation()}>{new Date(m.date).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}</Link></td>
                <td><ChampBadge name={m.champion} sub={m.position.toLowerCase()} /></td>
                <td><span className={m.win ? 'badge win' : 'badge loss'}>{m.win ? 'Victory' : 'Defeat'}</span></td>
                <td>{m.opponentChampion ? <ChampBadge name={m.opponentChampion} small /> : <span className="mut">—</span>}</td>
                <td>{m.enemyJungler ? <ChampBadge name={m.enemyJungler} small /> : <span className="mut">—</span>}</td>
                <td className="num">{m.kills}/{m.deaths}/{m.assists} <span className="mut">({m.kda})</span></td>
                <td className="num">{m.killParticipation !== null ? `${Math.round(m.killParticipation * 100)}%` : <span className="mut">—</span>}</td>
                <td className="num">{m.csAt10 ?? <span className="mut">—</span>}</td>
                <td className="num">{m.laneGoldDiff10 !== null
                  ? <span className={m.laneGoldDiff10 >= 0 ? 'win' : 'loss'}>{m.laneGoldDiff10 > 0 ? '+' : ''}{m.laneGoldDiff10}</span>
                  : <span className="mut">—</span>}</td>
                <td><Loadout items={m.items} summoner1Id={m.summoner1Id} summoner2Id={m.summoner2Id} /></td>
                <td className="num">{m.durationMin.toFixed(0)}</td>
                <td>{m.avgEnemyRank ?? <span className="mut">—</span>}</td>
                <td className="num">{m.lpChange !== null ? <span className={m.lpChange >= 0 ? 'win' : 'loss'}>{m.lpChange >= 0 ? '+' : ''}{m.lpChange}</span> : <span className="mut">—</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {items.length === 0 && <div className="empty">No games yet - import your exports or start the live capture on the Data page.</div>}

      <div className="pager">
        <button className="action" disabled={page <= 1} onClick={() => setPage(p => p - 1)}>Previous</button>
        <span>Page {page} of {pages}</span>
        <button className="action" disabled={page >= pages} onClick={() => setPage(p => p + 1)}>Next</button>
      </div>
    </div>
  )
}

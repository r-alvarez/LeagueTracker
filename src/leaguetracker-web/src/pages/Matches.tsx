import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { MatchSummary } from '../types'
import { useChampionIcons } from '../champions'
import Loadout from '../components/Loadout'
import RoleIcon from '../components/RoleIcon'
import { KdaStat, LpChip, RankChip, RelTime } from '../components/Stats'

const PAGE_SIZE = 25

function Portrait({ name, level, small }: { name: string; level?: number; small?: boolean }) {
  const icon = useChampionIcons()(name)
  return (
    <span className={`mr-portrait champ-frame ${small ? 'sm' : ''}`} title={name}>
      {icon ? <img src={icon} alt={name} loading="lazy" /> : <span className="champ-mono">{name.slice(0, 2).toUpperCase()}</span>}
      {level !== undefined && <span className="lvl">{level}</span>}
    </span>
  )
}

const shortQueue = (q: string) => q.replace(/^Ranked\s+/, '').replace(/^Normal\s+/, '')

const ROLE_WORD: Record<string, string> = { JUNGLE: 'jungler', UTILITY: 'support', BOTTOM: 'bot carry', MIDDLE: 'mid laner' }

/// The small companion icon riding a portrait's corner (my jungler / support / carry).
function Companion({ name, role }: { name: string; role: string | null }) {
  const icon = useChampionIcons()(name)
  return (
    <span className="companion" title={`${name}${role ? ` (${ROLE_WORD[role] ?? role.toLowerCase()})` : ''}`}>
      {icon ? <img src={icon} alt={name} loading="lazy" /> : <span className="champ-mono">{name.slice(0, 1)}</span>}
    </span>
  )
}

function Row({ m }: { m: MatchSummary }) {
  const navigate = useNavigate()
  const csPerMin = m.durationMin > 0 ? (m.cs / m.durationMin).toFixed(1) : '—'
  const result = m.isRemake ? 'Remake' : m.win ? 'Victory' : 'Defeat'
  const rowClass = m.isRemake ? 'mr-remake' : m.win ? 'mr-win' : 'mr-loss'

  return (
    <div className={`match-row ${rowClass}`} onClick={() => navigate(`/matches/${m.id}`)}
      role="link" tabIndex={0} onKeyDown={e => e.key === 'Enter' && navigate(`/matches/${m.id}`)}>
      <div className="mr-meta">
        <span className={`mr-result ${m.isRemake ? 'mut' : m.win ? 'win' : 'loss'}`}>{result}</span>
        <span className="sub"><RelTime date={m.gameEndUtc} /></span>
        <span className="sub">{shortQueue(m.queueName)} · {m.durationMin.toFixed(0)}m</span>
      </div>

      <div className="mr-champ">
        <span className="mr-duel">
          <span className="duo">
            <Portrait name={m.champion} level={m.champLevel} />
            {m.myCompanion && <Companion name={m.myCompanion} role={m.companionRole} />}
          </span>
          {m.opponentChampion && (
            <>
              <span className="vs-badge">vs</span>
              <span className="duo">
                <Portrait name={m.opponentChampion} small />
                {m.enemyCompanion && <Companion name={m.enemyCompanion} role={m.companionRole} />}
              </span>
            </>
          )}
        </span>
        <span className="mr-vs">
          <span className="name">{m.champion} <RoleIcon role={m.position} size={12} /></span>
          {m.opponentChampion
            ? <span className="opp">vs {m.opponentChampion}</span>
            : <span className="opp">{shortQueue(m.queueName)}</span>}
        </span>
      </div>

      <KdaStat kills={m.kills} deaths={m.deaths} assists={m.assists} kp={m.killParticipation} />

      <Loadout items={m.items} summoner1Id={m.summoner1Id} summoner2Id={m.summoner2Id} />

      <div className="mr-minis">
        <span className="mini-stat"><span className="v">{m.cs} ({csPerMin})</span><span className="k">CS (/m)</span></span>
        <span className="mini-stat"><span className="v">{m.csAt10 ?? '—'}</span><span className="k">CS@10</span></span>
        <span className="mini-stat">
          <span className={`v ${m.laneGoldDiff10 !== null ? (m.laneGoldDiff10 >= 0 ? 'win' : 'loss') : ''}`}>
            {m.laneGoldDiff10 !== null ? `${m.laneGoldDiff10 > 0 ? '+' : ''}${m.laneGoldDiff10}` : '—'}
          </span>
          <span className="k">G@10</span>
        </span>
        <span className="mini-stat"><span className="v">{m.visionScore}</span><span className="k">Vision</span></span>
        <span className="mini-stat"><span className="v">{m.soloKills}</span><span className="k">Solo kills</span></span>
      </div>

      <div className="mr-rank">
        <RankChip label={m.avgEnemyRank} />
        {m.rankGapLp !== null && (
          <span className={`sm-text ${m.rankGapLp > 0 ? 'win' : m.rankGapLp < 0 ? 'loss' : 'mut'}`}>
            gap {m.rankGapLp > 0 ? '+' : ''}{m.rankGapLp} LP
          </span>
        )}
      </div>

      <LpChip change={m.lpChange} />

      <div className="mr-links" onClick={e => e.stopPropagation()}>
        {m.hasReplay && (
          <a href={`/api/matches/${m.id}/replay`} download title="Download replay (.rofl)">⬇︎</a>
        )}
      </div>
    </div>
  )
}

export default function Matches() {
  const [items, setItems] = useState<MatchSummary[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [rankedOnly, setRankedOnly] = useState(true)

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

      <div className="match-rows">
        {items.map(m => <Row key={m.id} m={m} />)}
      </div>
      {items.length === 0 && <div className="empty">No games yet - sync your history on the Data page.</div>}

      <div className="pager">
        <button className="action" disabled={page <= 1} onClick={() => setPage(p => p - 1)}>Previous</button>
        <span>Page {page} of {pages}</span>
        <button className="action" disabled={page >= pages} onClick={() => setPage(p => p + 1)}>Next</button>
      </div>
    </div>
  )
}

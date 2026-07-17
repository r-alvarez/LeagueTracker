import { Fragment, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api'
import type { MatchFacets, MatchSummary, ReviewVerdicts } from '../types'
import { CONTEST_SHORT, contestSide } from '../contest'
import { useChampionIcons } from '../champions'
import ChampPicker from '../components/ChampPicker'
import Loadout from '../components/Loadout'
import ReviewDots from '../components/ReviewDots'
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

function Row({ m, reviews }: { m: MatchSummary; reviews: ReviewVerdicts }) {
  const navigate = useNavigate()
  const csPerMin = m.durationMin > 0 ? (m.cs / m.durationMin).toFixed(1) : '—'
  const result = m.isRemake ? 'Remake' : m.win ? 'Victory' : 'Defeat'
  // The contest verdict is the headline; the game's result demotes to the
  // muted sub-line - visible (never hidden), just no longer the verdict.
  // Rows without a review (no timeline, or still loading) keep the old look.
  const contest = m.isRemake ? null : reviews[m.id]?.contest ?? null
  const rowClass = m.isRemake ? 'mr-remake'
    : contest ? `mr-${contestSide(contest)}`
    : m.win ? 'mr-win' : 'mr-loss'

  return (
    <div className={`match-row ${rowClass}`} onClick={() => navigate(`/matches/${m.id}`)}
      role="link" tabIndex={0} onKeyDown={e => e.key === 'Enter' && navigate(`/matches/${m.id}`)}>
      <div className="mr-meta">
        {contest ? (
          <>
            <span className={`mr-contest ${contest}`}>{CONTEST_SHORT[contest]}</span>
            <span className="sub">
              <span className={`mr-sub-result ${m.win ? 'win' : 'loss'}`}>{result}</span> · <RelTime date={m.gameEndUtc} />
            </span>
          </>
        ) : (
          <>
            <span className={`mr-result ${m.isRemake ? 'mut' : m.win ? 'win' : 'loss'}`}>{result}</span>
            <span className="sub"><RelTime date={m.gameEndUtc} /></span>
          </>
        )}
        <span className="sub">{shortQueue(m.queueName)} · {m.durationMin.toFixed(0)}m · {m.patch}</span>
        <ReviewDots v={reviews[m.id]} />
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
        {(m.avgAllyRank !== null || m.avgEnemyRank !== null) && (
          <span className="rank-pair">
            <RankChip label={m.avgAllyRank} />
            <span className="vs-badge">vs</span>
            <RankChip label={m.avgEnemyRank} />
          </span>
        )}
        {m.rankGapLp !== null && m.rankGapLp !== 0 && (
          <span className={`sm-text ${m.rankGapLp < 0 ? 'win' : 'loss'}`}>
            {m.rankGapLp < 0 ? `favored by ${-m.rankGapLp} LP` : `outranked by ${m.rankGapLp} LP`}
          </span>
        )}
      </div>

      {m.lpChange !== null ? <LpChip change={m.lpChange} /> : <span />}

      <div className="mr-links" onClick={e => e.stopPropagation()}>
        {m.hasReplay && (
          <a href={`/api/matches/${m.id}/replay`} download title="Download replay (.rofl)">⬇︎</a>
        )}
      </div>
    </div>
  )
}

// "Ranked" = solo+flex (the old default); the rest are queue families.
const QUEUES = [
  { key: 'ranked', label: 'Ranked' },
  { key: '', label: 'All' },
  { key: 'solo', label: 'Solo' },
  { key: 'flex', label: 'Flex' },
  { key: 'normal', label: 'Normal' },
  { key: 'aram', label: 'ARAM' },
] as const

const ROLE_FILTERS: Array<{ key: string; label: string }> = [
  { key: '', label: 'All roles' }, { key: 'TOP', label: 'Top' }, { key: 'JUNGLE', label: 'Jungle' },
  { key: 'MIDDLE', label: 'Mid' }, { key: 'BOTTOM', label: 'Bot' }, { key: 'UTILITY', label: 'Support' },
]

type Grouping = 'day' | 'session' | 'none'
/** Games this far apart stop being one session. */
const SESSION_GAP_MS = 3 * 3600_000

const dayLabel = (iso: string) =>
  new Date(iso).toLocaleDateString(undefined, { day: '2-digit', month: 'short' })
const hm = (iso: string) =>
  new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })

/// Splits a newest-first page of matches into rendered groups. A page boundary
/// can cut a day/session in half - the header still labels what's visible.
function groupMatches(items: MatchSummary[], grouping: Grouping): { label: string | null; items: MatchSummary[] }[] {
  if (grouping === 'none' || items.length === 0) return [{ label: null, items }]
  const groups: { label: string | null; items: MatchSummary[] }[] = []
  for (const m of items) {
    const last = groups[groups.length - 1]
    const oldestInLast = last?.items[last.items.length - 1]
    const startNew = !oldestInLast
      || (grouping === 'day'
        ? dayLabel(m.gameEndUtc) !== dayLabel(oldestInLast.gameEndUtc)
        : new Date(oldestInLast.gameEndUtc).getTime() - new Date(m.gameEndUtc).getTime() > SESSION_GAP_MS)
    if (startNew) groups.push({ label: null, items: [m] })
    else last.items.push(m)
  }
  for (const g of groups) {
    const newest = g.items[0]
    const oldest = g.items[g.items.length - 1]
    g.label = grouping === 'day'
      ? dayLabel(newest.gameEndUtc)
      : `${dayLabel(newest.gameEndUtc)} · ${hm(oldest.gameEndUtc)}–${hm(newest.gameEndUtc)}`
  }
  return groups
}

interface Filters { queue: string; role: string; champion: string; opponent: string; patch: string }
const DEFAULT_FILTERS: Filters = { queue: 'ranked', role: '', champion: '', opponent: '', patch: '' }

export default function Matches() {
  const [items, setItems] = useState<MatchSummary[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [filters, setFilters] = useState<Filters>(DEFAULT_FILTERS)
  const [grouping, setGrouping] = useState<Grouping>('day')
  const [facets, setFacets] = useState<MatchFacets | null>(null)
  const [reviews, setReviews] = useState<ReviewVerdicts>({})

  const set = (patch: Partial<Filters>) => { setFilters(f => ({ ...f, ...patch })); setPage(1) }

  useEffect(() => {
    api.matchFacets().then(setFacets).catch(() => setFacets(null))
  }, [])

  useEffect(() => {
    const load = () =>
      api.matches(page, PAGE_SIZE, {
        ...(filters.queue === 'ranked' ? { ranked: 'true' } : { queue: filters.queue }),
        role: filters.role,
        champion: filters.champion,
        opponent: filters.opponent,
        patch: filters.patch,
      })
        .then(p => {
          setItems(p.items)
          setTotal(p.total)
          if (p.items.length > 0) {
            api.reviews(p.items.map(m => m.id))
              .then(r => setReviews(prev => ({ ...prev, ...r })))
              .catch(() => { /* rows just render without dots */ })
          }
        })
        .catch(console.error)
    load()
    // Freshly finished games land server-side minutes after the game ends;
    // refetch quietly so they appear without a manual reload.
    const id = setInterval(load, 30_000)
    return () => clearInterval(id)
  }, [page, filters])

  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const groups = groupMatches(items, grouping)
  const isFiltered = JSON.stringify(filters) !== JSON.stringify(DEFAULT_FILTERS)

  return (
    <div className="card">
      <div className="filters">
        <div className="seg">
          {ROLE_FILTERS.map(r => (
            <button key={r.key} className={filters.role === r.key ? 'on' : ''} title={r.label}
              onClick={() => set({ role: r.key })}>
              {r.key ? <RoleIcon role={r.key} size={14} /> : '✳'}
            </button>
          ))}
        </div>
        <div className="seg">
          {QUEUES.map(q => (
            <button key={q.key} className={filters.queue === q.key ? 'on' : ''} onClick={() => set({ queue: q.key })}>
              {q.label}
            </button>
          ))}
        </div>
        <select className="patch-select" value={filters.patch} onChange={e => set({ patch: e.target.value })}>
          <option value="">All patches</option>
          {(facets?.patches ?? []).slice().reverse().map(p => <option key={p} value={p}>{p}</option>)}
        </select>
        <span className="cp-duo">
          <ChampPicker placeholder="Your champion" value={filters.champion}
            options={facets?.champions ?? []} onChange={champion => set({ champion })} />
          <span className="vs-badge">vs</span>
          <ChampPicker placeholder="Opponent" value={filters.opponent}
            options={facets?.opponents ?? []} onChange={opponent => set({ opponent })} />
        </span>
        <div className="seg">
          {(['day', 'session', 'none'] as const).map(g => (
            <button key={g} className={grouping === g ? 'on' : ''} onClick={() => setGrouping(g)}>
              {g === 'day' ? 'Day' : g === 'session' ? 'Session' : 'None'}
            </button>
          ))}
        </div>
        {isFiltered && <button className="action" onClick={() => { setFilters(DEFAULT_FILTERS); setPage(1) }}>Reset</button>}
        <span className="mut">{total} games</span>
      </div>

      <div className="match-rows">
        {groups.map((g, gi) => {
          const wins = g.items.filter(m => !m.isRemake && m.win).length
          const losses = g.items.filter(m => !m.isRemake && !m.win).length
          return (
            <Fragment key={g.label ?? gi}>
              {g.label && (
                <div className="day-head">
                  <span className="dh-label">{g.label}</span>
                  <span className="dh-chips">
                    <span className={`obj-chip win ${wins === 0 ? 'zero' : ''}`}>{wins} win{wins === 1 ? '' : 's'}</span>
                    <span className={`obj-chip loss ${losses === 0 ? 'zero' : ''}`}>{losses} loss{losses === 1 ? '' : 'es'}</span>
                  </span>
                </div>
              )}
              {g.items.map(m => <Row key={m.id} m={m} reviews={reviews} />)}
            </Fragment>
          )
        })}
      </div>
      {items.length === 0 && (
        <div className="empty">
          {isFiltered ? 'No games match these filters.' : 'No games yet - sync your history on the Data page.'}
        </div>
      )}

      <div className="pager">
        <button className="action" disabled={page <= 1} onClick={() => setPage(p => p - 1)}>Previous</button>
        <span>Page {page} of {pages}</span>
        <button className="action" disabled={page >= pages} onClick={() => setPage(p => p + 1)}>Next</button>
      </div>
    </div>
  )
}

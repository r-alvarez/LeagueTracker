import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { FundamentalArea, FundamentalsResponse, LensTile, RankInfo, Status } from '../types'
import { RankChip, tierClass } from '../components/Stats'
import Ring, { ringColor } from '../components/Ring'
import RoleIcon from '../components/RoleIcon'

// Ladder geometry: ranked tiers bottom-up; Master+ clamps above the top row.
const TIER_ORDER = ['IRON', 'BRONZE', 'SILVER', 'GOLD', 'PLATINUM', 'EMERALD', 'DIAMOND']
const TIER_LABEL: Record<string, string> = {
  IRON: 'Iron', BRONZE: 'Bronze', SILVER: 'Silver', GOLD: 'Gold',
  PLATINUM: 'Platinum', EMERALD: 'Emerald', DIAMOND: 'Diamond',
}
// Horizontal placement of each skill box as a fraction of the LANE (the area
// right of the 120px tier-label gutter), echoing the source chart's stagger.
const AREA_LEFT: Record<string, number> = {
  macro: 0.04, info: 0.62,
  matchup: 0.28, wincon: 0.54,
  trading: 0.12, teamfight: 0.68,
  jungletrack: 0.22, warding: 0.48,
}
/** left offset aligned to the same gutter the row/rank/goal lines use. */
const laneLeft = (key: string) => `calc(120px + ${AREA_LEFT[key]} * (100% - 128px))`

const BAND_H = 116

// Climb goals the boxes are judged against (chips, not rows, are the judgment).
const TARGETS = ['SILVER', 'GOLD', 'PLATINUM', 'EMERALD', 'DIAMOND']

/// Colors read the player's OWN form (recent-window percentile within their own
/// history) on the rows that matter for the goal. Riot Challenge levels are
/// deliberately not judged: they're lifetime grind counters — a high-playtime
/// Bronze account holds Master chips everywhere — so they anchor nothing and
/// live only in the detail card's labeled context strip.
function areaStatus(a: FundamentalArea, target: string): 'ready' | 'focus' | 'urgent' | 'later' | 'none' {
  const rowI = TIER_ORDER.indexOf(a.tier)
  const targetI = TIER_ORDER.indexOf(target)
  if (rowI > targetI) return 'later'
  if (a.score === null) return 'none'
  return a.score >= 60 ? 'ready' : a.score >= 40 ? 'focus' : 'urgent'
}

/// Net direction of an area's scored metrics, recent vs baseline.
function areaTrend(a: FundamentalArea): 'up' | 'down' | null {
  let up = 0
  let down = 0
  for (const t of a.tiles) {
    if (t.higherIsBetter === null || t.value === null || t.old === null || t.value === t.old) continue
    if (t.value > t.old === t.higherIsBetter) up++
    else down++
  }
  return up > down ? 'up' : down > up ? 'down' : null
}

function TierEmblem({ tier, size = 30 }: { tier: string; size?: number }) {
  return (
    <svg viewBox="0 0 24 24" width={size} height={size} className={`fund-emblem ${tierClass(tier)}`} aria-hidden>
      <path d="M12 3 L15.5 7 L21 5 L17.5 12.5 L12 21 L6.5 12.5 L3 5 L8.5 7 Z"
        fill="currentColor" opacity="0.9" />
      <path d="M12 8 L14 12 L12 16.5 L10 12 Z" fill="var(--page)" opacity="0.55" />
    </svg>
  )
}

const fmt = (t: LensTile, v: number | null) => {
  if (v === null) return '—'
  if (t.unit === 'm:ss') return `${Math.floor(v / 60)}:${String(Math.round(v % 60)).padStart(2, '0')}`
  return `${v.toLocaleString(undefined, { maximumFractionDigits: t.decimals })}${t.unit === '%' ? '%' : t.unit ? ` ${t.unit}` : ''}`
}

function Delta({ t }: { t: LensTile }) {
  if (t.value === null || t.old === null || t.value === t.old) return null
  const up = t.value > t.old
  // Context-dependent metrics move without being "better" or "worse".
  if (t.higherIsBetter === null) return <span className="lens-delta mut">{up ? '▲' : '▼'}</span>
  const improved = up === t.higherIsBetter
  return <span className={`lens-delta ${improved ? 'win' : 'loss'}`}>{up ? '▲' : '▼'}</span>
}

/// "top 12%" from the challenges percentile (share of players at or above the level).
const topShare = (p: number | null) => (p !== null ? `top ${Math.max(1, Math.round(p * 100))}%` : null)

function AreaDetail({ area, data }: { area: FundamentalArea; data: FundamentalsResponse }) {
  const [selected, setSelected] = useState(area.tiles[0]?.key)
  useEffect(() => { setSelected(area.tiles[0]?.key) }, [area])
  const active = area.tiles.find(t => t.key === selected) ?? area.tiles[0]
  return (
    <div className="card fund-detail">
      <div className="fund-detail-head">
        <TierEmblem tier={area.tier} size={26} />
        <h2>
          {area.label} <span className="mut">gates at {TIER_LABEL[area.tier]}</span>
        </h2>
        <Ring score={area.score} size={44} />
      </div>
      <p className="mut sm-text fund-desc">{area.desc}</p>

      <div className="lens-main">
        <div className="lens-tiles">
          {area.tiles.map(t => (
            <button key={t.key} className={`lens-tile ${t.key === active?.key ? 'on' : ''}`} onClick={() => setSelected(t.key)} title={t.desc}>
              <span className="lt-label">{t.label}</span>
              <span className="lt-value">{fmt(t, t.value)} <Delta t={t} /></span>
            </button>
          ))}
        </div>
        {active && (
          <div className="lens-detail">
            <div className="ld-top">
              <div className="ld-hero">
                <span className="ld-value">{fmt(active, active.value)}</span>
                <span className="ld-label">{active.label}</span>
              </div>
              {data.hasBaseline && active.old !== null && (
                <span className="ld-side">
                  <Delta t={active} /> <strong>{fmt(active, active.value)}</strong> <span className="mut sm-text">NEW</span>
                  <span className="vs-badge">vs</span>
                  <strong>{fmt(active, active.old)}</strong> <span className="mut sm-text">OLD</span>
                </span>
              )}
            </div>
            <p className="mut sm-text" style={{ margin: 0 }}>
              {active.desc}.{active.higherIsBetter === false && ' Lower is better.'}
              {active.higherIsBetter === null && ' Context-dependent — shown for awareness, not scored.'}
            </p>
          </div>
        )}
      </div>

      {area.ladder && (
        <div className="fund-challenges">
          <span className="mut sm-text">Riot Challenges anchor (lifetime — partly reflects playtime):</span>
          {area.ladder.challenges.map(c => (
            <span key={c.id} className="fund-chip" title={c.description}>
              {c.name} <span className={`rank-chip ${tierClass(c.level)}`}>{c.level}</span>
              {c.percentile !== null && <span className="mut sm-text"> {topShare(c.percentile)}</span>}
            </span>
          ))}
        </div>
      )}
      <p className="mut sm-text fund-measured">Measured from: {area.measured}</p>
    </div>
  )
}

const ROLE_FILTERS: Array<{ key: string; label: string }> = [
  { key: '', label: 'All roles' }, { key: 'TOP', label: 'Top' }, { key: 'JUNGLE', label: 'Jungle' },
  { key: 'MIDDLE', label: 'Mid' }, { key: 'BOTTOM', label: 'Bot' }, { key: 'UTILITY', label: 'Support' },
]

// Same windows as the Dashboard and Coach, so all pages talk about one slice.
const WINDOWS = [
  { key: '7d', label: 'Last 7d', days: 7 },
  { key: '15d', label: '15d', days: 15 },
  { key: '30d', label: '30d', days: 30 },
  { key: '60d', label: '60d', days: 60 },
  { key: '10g', label: 'Last 10', window: 10 },
  { key: '20g', label: '20', window: 20 },
  { key: '30g', label: '30', window: 30 },
  { key: '50g', label: '50', window: 50 },
  { key: '100g', label: '100', window: 100 },
  // The API clamps the window to the stored game count, so this means "all".
  { key: 'all', label: 'All', window: 100000 },
] as const

export default function Fundamentals() {
  const [data, setData] = useState<FundamentalsResponse | null | undefined>(undefined)
  const [rank, setRank] = useState<RankInfo | null>(null)
  const [windowKey, setWindowKey] = useState<(typeof WINDOWS)[number]['key']>('20g')
  const [role, setRole] = useState('')
  const [areaKey, setAreaKey] = useState<string | null>(null)
  const [target, setTarget] = useState(() => {
    const saved = localStorage.getItem('fund-target')
    return saved && TARGETS.includes(saved) ? saved : 'PLATINUM'
  })
  useEffect(() => { localStorage.setItem('fund-target', target) }, [target])

  useEffect(() => {
    api.status().then((s: Status) => {
      const solo = s.ranks.find(r => r.queue === 'Solo/Duo') ?? s.ranks[0] ?? null
      setRank(solo)
    }).catch(() => setRank(null))
  }, [])

  useEffect(() => {
    setData(undefined)
    const w = WINDOWS.find(x => x.key === windowKey)!
    api.fundamentals({
      window: 'window' in w ? w.window : undefined,
      days: 'days' in w ? w.days : undefined,
      role,
    }).then(setData).catch(() => setData(null))
  }, [windowKey, role])

  // Rows top-down; extend below Gold when the account or the goal sits lower.
  const rows = useMemo(() => {
    let bottom = Math.min(TIER_ORDER.indexOf('GOLD'), TIER_ORDER.indexOf(target))
    if (rank && TIER_ORDER.indexOf(rank.tier.toUpperCase()) >= 0) {
      bottom = Math.min(bottom, TIER_ORDER.indexOf(rank.tier.toUpperCase()))
    }
    return TIER_ORDER.slice(bottom).reverse()
  }, [rank, target])

  // The cyan line: tier band + division/LP fraction inside it (IV bottom, I top).
  const rankLineTop = useMemo(() => {
    if (!rank) return null
    const idx = rows.indexOf(rank.tier.toUpperCase())
    if (idx < 0) return rank.rankValue >= 2800 ? 0 : null   // Master+ pins to the top edge
    const frac = Math.min(1, Math.max(0, (rank.rankValue - TIER_ORDER.indexOf(rank.tier.toUpperCase()) * 400) / 400))
    return idx * BAND_H + (1 - frac) * BAND_H
  }, [rank, rows])

  // Auto-open the weakest skill that matters for the goal (rows gating beyond
  // it only when nothing below scores) — the "train this next" default.
  const weakest = useMemo(() => {
    const scored = (data?.areas ?? []).filter(a => a.score !== null)
    const targetI = TIER_ORDER.indexOf(target)
    const relevant = scored.filter(a => TIER_ORDER.indexOf(a.tier) <= targetI)
    const pool = relevant.length > 0 ? relevant : scored
    return pool.length > 0 ? pool.reduce((a, b) => (a.score! <= b.score! ? a : b)).key : null
  }, [data, target])

  const selectedArea = data?.areas.find(a => a.key === (areaKey ?? weakest)) ?? data?.areas[0]

  return (
    <div className="lens-shell">
      <div className="lens-header card">
        <span className="lens-logo"><TierEmblem tier="DIAMOND" size={18} /> <strong>FUNDAMENTALS</strong></span>
        <div className="seg">
          {ROLE_FILTERS.map(r => (
            <button key={r.key} className={role === r.key ? 'on' : ''} title={r.label} onClick={() => setRole(r.key)}>
              {r.key ? <RoleIcon role={r.key} size={14} /> : '✳'}
            </button>
          ))}
        </div>
        <div className="seg">
          {WINDOWS.map(w => (
            <button key={w.key} className={windowKey === w.key ? 'on' : ''} onClick={() => setWindowKey(w.key)}>{w.label}</button>
          ))}
        </div>
        <div className="seg" title="The rank you're climbing toward - boxes are judged against it">
          <span className="fund-goal-word">Goal</span>
          {TARGETS.map(t => (
            <button key={t} className={target === t ? `on ${tierClass(t)}` : ''} onClick={() => setTarget(t)}>{TIER_LABEL[t]}</button>
          ))}
        </div>
        {data && (
          <span className="lens-hstats">
            <span><strong>{data.window}</strong> <span className="mut">GAMES</span></span>
            <span><strong className={data.winrate >= 50 ? 'win' : 'loss'}>{data.winrate}%</strong> <span className="mut">WR</span></span>
            {rank && <RankChip label={rank.label} />}
          </span>
        )}
      </div>

      {data === undefined && (
        <div className="skeleton-stack">
          <div className="shimmer" style={{ height: 420 }} />
          <div className="shimmer" style={{ height: 220 }} />
        </div>
      )}
      {data === null && <div className="empty">Not enough ranked games {role ? `as ${ROLE_FILTERS.find(r => r.key === role)?.label}` : ''} for the fundamentals ladder (need 8+).</div>}

      {data && (
        <>
          <div className="card fund-card">
            <h2>Fundamentals ladder <span className="mut">skills at the rank where they start gating games</span></h2>
            <div className="fund-chart" style={{ height: rows.length * BAND_H + 12 }}>
              {rows.map((tier, i) => (
                <div key={tier} className="fund-row" style={{ top: i * BAND_H, height: BAND_H }}>
                  <span className="fund-row-label">
                    <TierEmblem tier={tier} />
                    <span className={`fund-tier-name ${tierClass(tier)}`}>{TIER_LABEL[tier]}</span>
                  </span>
                  <span className="fund-row-line" />
                  {data.areas.filter(a => a.tier === tier).map(a => (
                    <button
                      key={a.key}
                      className={`fund-box ${areaStatus(a, target)} ${a.key === selectedArea?.key ? 'on' : ''}`}
                      style={{ left: laneLeft(a.key) }}
                      onClick={() => setAreaKey(a.key)}
                      title={a.desc}
                    >
                      <span className="fund-box-label">{a.label}</span>
                      <span className="fund-box-meta">
                        {a.score !== null && (
                          <span className="fund-score" style={{ color: ringColor(a.score) }}>{a.score}</span>
                        )}
                        {data.hasBaseline && areaTrend(a) && (
                          <span className={`fund-trend ${areaTrend(a) === 'up' ? 'win' : 'loss'}`}>
                            {areaTrend(a) === 'up' ? '▲' : '▼'}
                          </span>
                        )}
                      </span>
                    </button>
                  ))}
                </div>
              ))}
              {rankLineTop !== null && rank && (
                <div className="fund-rank-line" style={{ top: rankLineTop }}>
                  <span className="fund-rank-label">Rank: {TIER_LABEL[rank.tier.toUpperCase()] ?? rank.tier} {rank.division}</span>
                </div>
              )}
              {rows.indexOf(target) >= 0 && (
                // Reaching the goal tier = entering its band, so the line sits
                // on the band's bottom edge.
                <div className="fund-goal-line" style={{ top: (rows.indexOf(target) + 1) * BAND_H }}>
                  <span className="fund-goal-label">Goal: {TIER_LABEL[target]}</span>
                </div>
              )}
            </div>
            <p className="mut sm-text" style={{ margin: '10px 2px 0' }}>
              Boxes sit at the rank tier where each skill typically starts deciding games — they don't move with your
              performance. The number on a box is your recent form: where your last {data.window} games sit within your own{' '}
              {data.games}-game history (self-relative — never comparable between accounts), with the arrow showing whether
              the underlying metrics moved up or down vs your baseline. Colors read that form on the skills that matter for
              your <strong>{TIER_LABEL[target]}</strong> goal: <span className="fund-key ready">strong</span> — recent games
              sit high in your own history, <span className="fund-key focus">train</span> — middling,{' '}
              <span className="fund-key urgent">priority</span> — well below your usual;{' '}
              <span className="fund-key later">white</span> skills gate beyond the goal — not judged yet. Riot Challenge
              levels are lifetime grind counters (a high-playtime account holds Master chips at any rank), so they appear
              only as labeled context in the detail card, never as a verdict.{' '}
              {rankLineTop === null && 'Rank line hidden — rank/LP display is off for this instance.'}
            </p>
          </div>

          {selectedArea && <AreaDetail area={selectedArea} data={data} />}
        </>
      )}
    </div>
  )
}

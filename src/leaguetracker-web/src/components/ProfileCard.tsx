import { useMemo, useState } from 'react'
import type { ProfileGroup, ProfileMetric } from '../types'

function fmt(v: number | null, unit: string): string {
  if (v === null) return '—'
  if (unit === '%') return `${Math.round(v * 100)}%`
  if (unit === 'sec') {
    // Short durations are LEADS (level 6 lead: -7 = seven seconds behind) -
    // a clock render like "0:-7" or even "−0:07" reads as gibberish there,
    // so under two minutes they print as signed seconds. Longer values are
    // times into the game and keep m:ss (rounded to whole seconds first,
    // avoiding "5:60").
    const s = Math.round(Math.abs(v))
    if (s < 120) return `${v < 0 ? '−' : '+'}${s}s`
    return `${v < 0 ? '−' : ''}${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
  }
  if (unit === '/min') return v.toFixed(2)
  return Number.isInteger(v) ? String(v) : v.toFixed(1)
}

const CATEGORIES = ['All', 'Laning', 'Vision', 'Combat', 'Objectives', 'Macro'] as const

// separationPct can spike (a rare metric near a zero baseline); clamp the bar so
// one outlier doesn't flatten the rest. Values still shown as text.
const BAR_CLAMP = 120

// Below this much separation, wins and losses look the same - not a story.
const SEP_FLOOR = 8

// Small trend sparkline: value per game across the window, oldest → newest.
function Sparkline({ values, unit }: { values: number[]; unit: string }) {
  if (values.length < 2) return <span className="mut sm-text">not enough games to trend</span>
  const w = 260, h = 40, pad = 3
  const min = Math.min(...values), max = Math.max(...values)
  const span = max - min || 1
  const pts = values.map((v, i) => {
    const x = pad + (i / (values.length - 1)) * (w - 2 * pad)
    const y = h - pad - ((v - min) / span) * (h - 2 * pad)
    return `${x.toFixed(1)},${y.toFixed(1)}`
  })
  return (
    <div>
      <svg width="100%" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" style={{ display: 'block' }}>
        <polyline points={pts.join(' ')} fill="none" stroke="var(--series-1)" strokeWidth={1.5} strokeLinejoin="round" strokeLinecap="round" />
      </svg>
      <div className="spark-scale">
        <span>{fmt(min, unit)}</span>
        <span className="mut">{values.length} games</span>
        <span>{fmt(max, unit)}</span>
      </div>
    </div>
  )
}

const STATES = [
  { key: 'evenBehind', label: 'Even or behind @10', blurb: 'Games that were NOT decided by laning — what you did differently in the ones you won. This is the controllable, causal view.' },
  { key: 'ahead', label: 'Ahead @10', blurb: 'Games where you had a laning lead — what separates the ones you converted from the ones you threw.' },
  { key: 'all', label: 'All games', blurb: 'Every game. Warning: metrics like plates and dragons are largely OUTCOMES of already being ahead, so this view overstates them — use the game-state views for causes.' },
] as const

function verdict(m: ProfileMetric, state: string): string {
  const sep = m.separationPct ?? 0
  const dir = m.higherIsBetter ? 'higher' : 'lower'
  if (Math.abs(sep) < SEP_FLOOR) return 'About the same in wins and losses — not a deciding factor for you in these games.'
  if (sep > 0) {
    const causal = state === 'all'
      ? ' (but in "all games" this may partly be an outcome of already being ahead — confirm it in the even-or-behind view)'
      : ' — since these games started on level terms, this is genuinely something you did to win them'
    return `You post a ${dir} number when you win${causal}. A real lever to lean into.`
  }
  return 'This is worse in your wins than losses, which usually means it scales with game length rather than being a weakness — read the trend, not the gap alone.'
}

// Wins vs losses as two plain bars on a shared scale: the gap IS the story.
// Wins carry the color; losses are the muted comparison (emphasis, not alarm).
function PairedBars({ m }: { m: ProfileMetric }) {
  // Bar length encodes non-negative magnitude only, clamped to the track:
  // lead metrics can average negative in BOTH wins and losses (a level-6
  // deficit either way), and dividing by a negative max sends widths past
  // 300% and out of the card. Zero-length bars + the printed values are the
  // honest rendering of "behind in both".
  const max = Math.max(m.avgWins ?? 0, m.avgLosses ?? 0, 0) || 1
  const width = (v: number | null) => Math.max(0, Math.min(100, Math.round((100 * (v ?? 0)) / max)))
  const rows = [
    { who: 'wins', v: m.avgWins, cls: 'w' },
    { who: 'losses', v: m.avgLosses, cls: 'l' },
  ] as const
  return (
    <div className="pair-bars">
      {rows.map(r => (
        <div key={r.who} className="pair-row">
          <span className="who">{r.who}</span>
          <span className="pair-bar"><span className={`fill ${r.cls}`} style={{ width: `${width(r.v)}%` }} /></span>
          <span className="val">{fmt(r.v, m.unit)}</span>
        </div>
      ))}
    </div>
  )
}

function MetricDetail({ m, state }: { m: ProfileMetric; state: string }) {
  return (
    <div className="profile-detail">
      <p style={{ margin: '0 0 12px' }}>{m.description}</p>
      <div className="grid tiles" style={{ marginBottom: 12 }}>
        <div className="tile"><div className="label">In your wins</div><div className="value win">{fmt(m.avgWins, m.unit)}</div></div>
        <div className="tile"><div className="label">In your losses</div><div className="value loss">{fmt(m.avgLosses, m.unit)}</div></div>
        <div className="tile"><div className="label">Overall avg</div><div className="value">{fmt(m.avg, m.unit)}</div><div className="sub">{m.games} games · {m.higherIsBetter ? 'higher is better' : 'lower is better'}</div></div>
      </div>
      <div className="sub-h">Trend across these games</div>
      <Sparkline values={m.recent} unit={m.unit} />
      <p className="mut sm-text" style={{ margin: '10px 0 0' }}>{verdict(m, state)}</p>
    </div>
  )
}

export default function ProfileCard({ profile, windowLabel }: { profile: { all: ProfileGroup; evenBehind: ProfileGroup; ahead: ProfileGroup }; windowLabel: string }) {
  const [cat, setCat] = useState<(typeof CATEGORIES)[number]>('All')
  const [state, setState] = useState<(typeof STATES)[number]['key']>('evenBehind')
  const [open, setOpen] = useState<string | null>(null)

  const group = profile[state]
  const stateDef = STATES.find(s => s.key === state)!

  const scored = useMemo(
    () => (group?.metrics ?? [])
      .filter(m => m.separationPct !== null)
      .sort((a, b) => (b.separationPct ?? 0) - (a.separationPct ?? 0)),
    [group],
  )

  // The headline: the few metrics where wins and losses genuinely diverge.
  const levers = useMemo(() => scored.filter(m => (m.separationPct ?? 0) >= SEP_FLOOR).slice(0, 4), [scored])
  const drags = useMemo(
    () => scored.filter(m => (m.separationPct ?? 0) <= -SEP_FLOOR).slice(-3).reverse(),
    [scored],
  )

  const explorerRows = useMemo(
    () => scored.filter(m => cat === 'All' || m.category === cat),
    [scored, cat],
  )

  if (!profile.all || profile.all.metrics.length === 0) {
    return <div className="empty">No challenge data yet — reprocess games on the Data page.</div>
  }

  const openLever = levers.find(m => m.key === open)
  const openDrag = drags.find(m => m.key === open)

  return (
    <>
      <div className="filters" style={{ margin: '0 0 6px' }}>
        <div className="seg">
          {STATES.map(sdef => (
            <button key={sdef.key} className={sdef.key === state ? 'on' : ''} onClick={() => { setState(sdef.key); setOpen(null) }}>
              {sdef.label} <span className="mut">({profile[sdef.key].wins}-{profile[sdef.key].games - profile[sdef.key].wins})</span>
            </button>
          ))}
        </div>
      </div>
      <p className="mut sm-text" style={{ margin: '0 0 14px' }}>{stateDef.blurb}</p>

      {group.games < 4 && (
        <div className="empty">Only {group.games} {stateDef.label.toLowerCase()} games in this window — widen the window (top of page) for a reliable read.</div>
      )}

      {levers.length > 0 && (
        <>
          <div className="sub-h" style={{ marginTop: 0 }}>What wins your games <span className="mut" style={{ textTransform: 'none', letterSpacing: 0 }}>— click a card for the meaning + trend</span></div>
          <div className="lever-grid">
            {levers.map(m => (
              <div key={m.key} className={`lever-card ${open === m.key ? 'active' : ''}`} title={m.description}
                onClick={() => setOpen(open === m.key ? null : m.key)}>
                <div className="lever-head">
                  <span className="lever-name">{m.label}</span>
                  <span className="sep-chip good">+{Math.round(Math.min(m.separationPct ?? 0, BAR_CLAMP * 4))}%</span>
                </div>
                <PairedBars m={m} />
              </div>
            ))}
          </div>
          {openLever && <MetricDetail m={openLever} state={state} />}
        </>
      )}

      {drags.length > 0 && (
        <>
          <div className="sub-h">Higher in your losses <span className="mut" style={{ textTransform: 'none', letterSpacing: 0 }}>— often just game length; click before reading it as a weakness</span></div>
          <div className="drag-list">
            {drags.map(m => (
              <div key={m.key} className={`drag-row ${open === m.key ? 'active' : ''}`} onClick={() => setOpen(open === m.key ? null : m.key)}>
                <span className="lever-name">{m.label}</span>
                <span className="drag-vals"><span className="win">{fmt(m.avgWins, m.unit)}</span><span className="mut"> in wins vs </span><span className="loss">{fmt(m.avgLosses, m.unit)}</span><span className="mut"> in losses</span></span>
              </div>
            ))}
          </div>
          {openDrag && <MetricDetail m={openDrag} state={state} />}
        </>
      )}

      <details className="explore">
        <summary>Explore all {scored.length} metrics</summary>
        <div className="filters" style={{ margin: '12px 0' }}>
          <div className="seg">
            {CATEGORIES.map(c => (
              <button key={c} className={c === cat ? 'on' : ''} onClick={() => setCat(c)}>{c}</button>
            ))}
          </div>
          <span className="mut sm-text">avg in wins / losses on the right</span>
        </div>
        <div className="profile-list">
          {explorerRows.map(m => {
            const sep = m.separationPct ?? 0
            const width = (Math.min(Math.abs(sep), BAR_CLAMP) / BAR_CLAMP) * 50
            const good = sep >= 0
            const isOpen = open === m.key
            return (
              <div key={m.key}>
                <div className={`profile-row clickable ${isOpen ? 'active' : ''}`} onClick={() => setOpen(isOpen ? null : m.key)}>
                  <span className="profile-label">
                    <span className="disclosure">{isOpen ? '▾' : '▸'}</span>
                    {m.label}
                    <span className="cat-chip">{m.category}</span>
                  </span>
                  <span className="profile-bar" title={`${sep > 0 ? '+' : ''}${sep}% separation between wins and losses`}>
                    <span className="axis" />
                    <span className={good ? 'fill good' : 'fill bad'}
                      style={good ? { left: '50%', width: `${width}%` } : { right: '50%', width: `${width}%` }} />
                  </span>
                  <span className="profile-vals">
                    <span className="win">{fmt(m.avgWins, m.unit)}</span>
                    <span className="mut"> / </span>
                    <span className="loss">{fmt(m.avgLosses, m.unit)}</span>
                  </span>
                </div>
                {isOpen && <MetricDetail m={m} state={state} />}
              </div>
            )
          })}
        </div>
      </details>

      <p className="mut sm-text" style={{ margin: '12px 0 0' }}>
        {stateDef.label} games from {windowLabel}, wins vs losses. Comparing within a game state
        (not across all games) keeps this from just rewarding the snowball you already had.
      </p>
    </>
  )
}

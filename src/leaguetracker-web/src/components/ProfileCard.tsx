import { useMemo, useState } from 'react'
import type { ProfileMetric } from '../types'

function fmt(v: number | null, unit: string): string {
  if (v === null) return '—'
  if (unit === '%') return `${Math.round(v * 100)}%`
  if (unit === 'sec') return `${Math.round(v / 60)}:${String(Math.round(v % 60)).padStart(2, '0')}`
  if (unit === '/min') return v.toFixed(2)
  return Number.isInteger(v) ? String(v) : v.toFixed(1)
}

const CATEGORIES = ['All', 'Laning', 'Vision', 'Combat', 'Objectives', 'Macro'] as const

// separationPct can spike (a rare metric near a zero baseline); clamp the bar so
// one outlier doesn't flatten the rest. Values still shown as text.
const BAR_CLAMP = 120

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

function verdict(m: ProfileMetric): string {
  const sep = m.separationPct ?? 0
  const dir = m.higherIsBetter ? 'higher' : 'lower'
  if (Math.abs(sep) < 8) return 'About the same in wins and losses — not a deciding factor for you right now.'
  if (sep > 0) return `You post a ${dir} number when you win — this looks like one of your win conditions. Doing it more should move your win rate.`
  return `This is worse in your wins than losses, which usually means it scales with game length rather than being a real strength — read it with the trend, not the gap alone.`
}

export default function ProfileCard({ profile, windowLabel }: { profile: ProfileMetric[]; windowLabel: string }) {
  const [cat, setCat] = useState<(typeof CATEGORIES)[number]>('All')
  const [open, setOpen] = useState<string | null>(null)

  const rows = useMemo(
    () => profile
      .filter(m => cat === 'All' || m.category === cat)
      .filter(m => m.separationPct !== null)
      .sort((a, b) => (b.separationPct ?? 0) - (a.separationPct ?? 0)),
    [profile, cat],
  )

  if (profile.length === 0) {
    return <div className="empty">No challenge data yet — reprocess games on the Data page.</div>
  }

  return (
    <>
      <div className="filters" style={{ margin: '0 0 12px' }}>
        <div className="seg">
          {CATEGORIES.map(c => (
            <button key={c} className={c === cat ? 'on' : ''} onClick={() => setCat(c)}>{c}</button>
          ))}
        </div>
        <span className="mut sm-text">click a row for the meaning + trend · avg in wins / losses on the right</span>
      </div>

      <div className="profile-list">
        {rows.map(m => {
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
              {isOpen && (
                <div className="profile-detail">
                  <p style={{ margin: '0 0 12px' }}>{m.description}</p>
                  <div className="grid tiles" style={{ marginBottom: 12 }}>
                    <div className="tile"><div className="label">In your wins</div><div className="value win">{fmt(m.avgWins, m.unit)}</div></div>
                    <div className="tile"><div className="label">In your losses</div><div className="value loss">{fmt(m.avgLosses, m.unit)}</div></div>
                    <div className="tile"><div className="label">Overall avg</div><div className="value">{fmt(m.avg, m.unit)}</div><div className="sub">{m.games} games · {m.higherIsBetter ? 'higher is better' : 'lower is better'}</div></div>
                  </div>
                  <div className="sub-h">Trend across this window</div>
                  <Sparkline values={m.recent} unit={m.unit} />
                  <p className="mut sm-text" style={{ margin: '10px 0 0' }}>{verdict(m)}</p>
                </div>
              )}
            </div>
          )
        })}
      </div>
      <p className="mut sm-text" style={{ margin: '12px 0 0' }}>
        Averaged over {windowLabel}. Green/right = you do more of it when you win (lean in); red/left = shows up more in losses.
        Riot pre-computes these; raw counts scale a little with game length, so click through and check the trend.
      </p>
    </>
  )
}

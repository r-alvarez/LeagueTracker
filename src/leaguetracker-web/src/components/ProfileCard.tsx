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

// separationPct can spike (a rare metric near zero baseline); clamp the bar so
// one outlier doesn't flatten the rest. Values still shown as text.
const BAR_CLAMP = 120

export default function ProfileCard({ profile }: { profile: ProfileMetric[] }) {
  const [cat, setCat] = useState<(typeof CATEGORIES)[number]>('All')

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
        <span className="mut sm-text">bar = how much this separates your wins from losses</span>
      </div>

      <div className="profile-list">
        {rows.map(m => {
          const sep = m.separationPct ?? 0
          const width = Math.min(Math.abs(sep), BAR_CLAMP) / BAR_CLAMP * 50
          const good = sep >= 0
          return (
            <div key={m.key} className="profile-row">
              <span className="profile-label">
                {m.label}
                <span className="cat-chip">{m.category}</span>
              </span>
              <span className="profile-bar" title={`${sep > 0 ? '+' : ''}${sep}% separation`}>
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
          )
        })}
      </div>
      <p className="mut sm-text" style={{ margin: '10px 0 0' }}>
        Values are average in your wins / losses (last {profile[0]?.games ?? 0}+ games). Bars right/green = you do more of it when you win —
        lean in; left/red = shows up more in losses. Riot pre-computes these; raw counts scale a little with game length.
      </p>
    </>
  )
}

import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { LensCategory, LensResponse, LensSub, LensTile } from '../types'

// Score bands share the validated chart palette: struggling -> strong.
const ringColor = (score: number) =>
  score < 40 ? 'var(--lp-loss)' : score < 60 ? 'var(--series-3)' : score < 80 ? 'var(--series-1)' : 'var(--chart-green)'

function Ring({ score, size = 54 }: { score: number | null; size?: number }) {
  const r = (size - 8) / 2
  const c = 2 * Math.PI * r
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} className="lens-ring">
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="var(--panel-2)" strokeWidth="5" />
      {score !== null && (
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={ringColor(score)} strokeWidth="5"
          strokeDasharray={`${(c * score) / 100} ${c}`} strokeLinecap="round"
          transform={`rotate(-90 ${size / 2} ${size / 2})`} />
      )}
      <text x="50%" y="50%" dy="0.35em" textAnchor="middle" fontSize={size / 3.2} fontWeight="750" fill="var(--ink)">
        {score ?? '—'}
      </text>
    </svg>
  )
}

const fmt = (t: LensTile, v: number | null) =>
  v === null ? '—' : `${v.toLocaleString(undefined, { maximumFractionDigits: t.decimals })}${t.unit === '%' ? '%' : t.unit ? ` ${t.unit}` : ''}`

/// Delta arrow: direction from the numeric change, color from whether that
/// direction is an improvement for this metric.
function Delta({ t }: { t: LensTile }) {
  if (t.value === null || t.old === null || t.value === t.old) return null
  const up = t.value > t.old
  const improved = up === t.higherIsBetter
  return <span className={improved ? 'win' : 'loss'}>{up ? '▲' : '▼'}</span>
}

function TileCard({ t, active, onClick }: { t: LensTile; active: boolean; onClick: () => void }) {
  return (
    <button className={`lens-tile ${active ? 'on' : ''}`} onClick={onClick}>
      <span className="lt-label">{t.label}</span>
      <span className="lt-value">{fmt(t, t.value)} <Delta t={t} /></span>
    </button>
  )
}

function TileDetail({ t, hasBaseline, window }: { t: LensTile; hasBaseline: boolean; window: number }) {
  return (
    <div className="lens-detail">
      <div className="ld-hero">
        <span className="ld-value">{fmt(t, t.value)}</span>
        <span className="ld-label">{t.label}</span>
      </div>
      {hasBaseline && t.old !== null && (
        <div className="ld-compare">
          <span><Delta t={t} /> <strong>{fmt(t, t.value)}</strong> <span className="mut sm-text">LAST {window}</span></span>
          <span className="vs-badge">vs</span>
          <span><strong>{fmt(t, t.old)}</strong> <span className="mut sm-text">BEFORE</span></span>
        </div>
      )}
      <p className="mut sm-text" style={{ margin: 0 }}>
        {t.desc}.{!t.higherIsBetter && ' Lower is better.'}
      </p>
    </div>
  )
}

function TileGrid({ tiles, hasBaseline, window }: { tiles: LensTile[]; hasBaseline: boolean; window: number }) {
  const [selected, setSelected] = useState(tiles[0]?.key)
  const active = tiles.find(t => t.key === selected) ?? tiles[0]
  return (
    <>
      <div className="lens-tiles">
        {tiles.map(t => <TileCard key={t.key} t={t} active={t.key === active?.key} onClick={() => setSelected(t.key)} />)}
      </div>
      {active && <TileDetail t={active} hasBaseline={hasBaseline} window={window} />}
    </>
  )
}

function FightingView({ cat, hasBaseline, window }: { cat: LensCategory; hasBaseline: boolean; window: number }) {
  const subs = cat.subs ?? []
  const [subKey, setSubKey] = useState(subs[0]?.key)
  const sub: LensSub | undefined = subs.find(s => s.key === subKey) ?? subs[0]
  return (
    <div className="lens-split">
      <div className="lens-subrail">
        {subs.map(s => (
          <button key={s.key} className={`lens-subcard ${s.key === sub?.key ? 'on' : ''}`} onClick={() => setSubKey(s.key)}>
            <span className="ls-name">{s.label}<span className="mut sm-text"> {s.desc}</span></span>
            <Ring score={s.score} size={44} />
          </button>
        ))}
      </div>
      <div className="lens-main">
        {sub && <TileGrid tiles={sub.tiles} hasBaseline={hasBaseline} window={window} />}
      </div>
    </div>
  )
}

export default function Coach() {
  const [data, setData] = useState<LensResponse | null | undefined>(undefined)
  const [window, setWindow] = useState(20)
  const [tab, setTab] = useState('overview')

  useEffect(() => {
    api.lens(window).then(setData).catch(() => setData(null))
  }, [window])

  const weakest = useMemo(() => {
    const scored = (data?.categories ?? []).filter(c => c.score !== null)
    return scored.length > 0 ? scored.reduce((a, b) => (a.score! <= b.score! ? a : b)).key : null
  }, [data])

  if (data === undefined) return <div className="empty">Crunching your games…</div>
  if (data === null) return <div className="empty">Not enough ranked games yet for the Lens.</div>

  const current = data.categories.find(c => c.key === tab)

  return (
    <div className="grid" style={{ gap: 14 }}>
      <div className="card">
        <div className="filters" style={{ marginBottom: 0 }}>
          <h2 style={{ margin: 0 }}>Lens <span className="mut" style={{ fontWeight: 400 }}>— last {data.window} games vs your own history</span></h2>
          <div className="seg" style={{ marginLeft: 'auto' }}>
            {[10, 20, 50].map(w => (
              <button key={w} className={window === w ? 'on' : ''} onClick={() => setWindow(w)}>Last {w}</button>
            ))}
          </div>
          <span className="mut sm-text">{data.games} ranked games</span>
        </div>
        <p className="mut sm-text" style={{ margin: '8px 0 0' }}>
          Scores are your recent window's percentile within your own {data.games}-game history — 73 means "better than 73%
          of your past games". The "Vs the ladder" card on the Dashboard remains the external benchmark.
        </p>
      </div>

      <div className="filters" style={{ margin: 0 }}>
        <div className="seg">
          <button className={tab === 'overview' ? 'on' : ''} onClick={() => setTab('overview')}>Overview</button>
          {data.categories.map(c => (
            <button key={c.key} className={tab === c.key ? 'on' : ''} onClick={() => setTab(c.key)}>{c.label}</button>
          ))}
        </div>
      </div>

      {tab === 'overview' && (
        <div className="lens-overview">
          {data.categories.map(c => (
            <button key={c.key} className="card lens-cat" onClick={() => setTab(c.key)}>
              <span className="lc-name">
                {c.label}
                {c.key === weakest && <span className="lc-improve">skill to improve</span>}
              </span>
              <Ring score={c.score} />
            </button>
          ))}
        </div>
      )}

      {current && tab !== 'overview' && (
        <div className="card">
          <div className="sb-head">
            <h2 style={{ margin: 0 }}>{current.label}</h2>
            <Ring score={current.score} size={40} />
          </div>
          {current.subs
            ? <FightingView cat={current} hasBaseline={data.hasBaseline} window={data.window} />
            : <TileGrid tiles={current.tiles ?? []} hasBaseline={data.hasBaseline} window={data.window} />}
        </div>
      )}
    </div>
  )
}

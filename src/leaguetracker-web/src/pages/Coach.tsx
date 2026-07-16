import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { LensCategory, LensResponse, LensSub, LensTile } from '../types'
import { useChampionIcons } from '../champions'
import RoleIcon from '../components/RoleIcon'
import Ring from '../components/Ring'

/// Small circled glyphs for categories and subcategories, drawn inline.
function CatIcon({ cat, size = 26 }: { cat: string; size?: number }) {
  const stroke = { fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const }
  return (
    <span className="cat-icon" style={{ width: size + 12, height: size + 12 }}>
      <svg viewBox="0 0 24 24" width={size} height={size} aria-hidden>
        {cat === 'fighting' && <path {...stroke} d="M4 4l9 9m-2 4l-3-3m0 3l3-3M20 4l-9 9m2 4l3-3m0 3l-3-3" />}
        {cat === 'laning' && <path {...stroke} d="M4 20L20 4M4 11L11 4M13 20l7-7" />}
        {cat === 'objectives' && <path {...stroke} d="M12 3l6 6-6 12L6 9z M6 9h12" />}
        {cat === 'vision' && <path {...stroke} d="M2 12s4-6 10-6 10 6 10 6-4 6-10 6-10-6-10-6z M12 12m-2.5 0a2.5 2.5 0 1 0 5 0 2.5 2.5 0 1 0-5 0" />}
        {cat === 'survivability' && <path {...stroke} d="M12 3l7 3v5c0 5-3.5 8.2-7 10-3.5-1.8-7-5-7-10V6z" />}
        {cat === 'overview' && <path {...stroke} d="M12 3v6m0 6v6M3 12h6m6 0h6" />}
      </svg>
    </span>
  )
}

const SUB_GLYPHS: Record<string, string> = { general: '✦', duels: '1v1', skirmishes: '2v3', teamfights: '5v5', mechanics: 'µ' }

function ChampFace({ name, size = 24 }: { name: string | null; size?: number }) {
  const icons = useChampionIcons()
  const icon = name ? icons(name) : null
  return (
    <span className="lens-face" style={{ width: size, height: size }} title={name ?? undefined}>
      {icon && <img src={icon} alt={name ?? ''} loading="lazy" />}
    </span>
  )
}

const fmt = (t: LensTile, v: number | null) =>
  v === null ? '—' : `${v.toLocaleString(undefined, { maximumFractionDigits: t.decimals })}${t.unit === '%' ? '%' : t.unit ? ` ${t.unit}` : ''}`

function Delta({ t }: { t: LensTile }) {
  if (t.value === null || t.old === null || t.value === t.old) return null
  const up = t.value > t.old
  const improved = up === t.higherIsBetter
  return <span className={`lens-delta ${improved ? 'win' : 'loss'}`}>{up ? '▲' : '▼'}</span>
}

/// Does this stat decide games? The same metric split by result - a number
/// pair, deliberately not a chart.
function WinLossSplit({ t, wins }: { t: LensTile; wins: boolean[] }) {
  const inWins = t.series.filter((v, i) => v !== null && wins[i]) as number[]
  const inLosses = t.series.filter((v, i) => v !== null && !wins[i]) as number[]
  if (inWins.length < 2 || inLosses.length < 2) return null
  const mean = (a: number[]) => Math.round((a.reduce((x, y) => x + y, 0) / a.length) * 100) / 100
  return (
    <span className="wl-split">
      <span className="obj-chip win">in wins <strong>{fmt(t, mean(inWins))}</strong></span>
      <span className="obj-chip loss">in losses <strong>{fmt(t, mean(inLosses))}</strong></span>
    </span>
  )
}

function TileGrid({ tiles, data }: { tiles: LensTile[]; data: LensResponse }) {
  const [selected, setSelected] = useState(tiles[0]?.key)
  useEffect(() => { setSelected(tiles[0]?.key) }, [tiles])
  const active = tiles.find(t => t.key === selected) ?? tiles[0]
  return (
    <div className="lens-main">
      <div className="lens-tiles">
        {tiles.map(t => (
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
            <span className="ld-right">
              <WinLossSplit t={active} wins={data.recentWins} />
              {data.hasBaseline && active.old !== null && (
                <span className="ld-side">
                  <ChampFace name={data.topChampion} /> <Delta t={active} /> <strong>{fmt(active, active.value)}</strong> <span className="mut sm-text">NEW</span>
                  <span className="vs-badge">vs</span>
                  <strong>{fmt(active, active.old)}</strong> <span className="mut sm-text">OLD</span> <ChampFace name={data.topChampionOld} />
                </span>
              )}
            </span>
          </div>
          <p className="mut sm-text" style={{ margin: 0 }}>
            {active.desc}.{!active.higherIsBetter && ' Lower is better.'}
          </p>
        </div>
      )}
    </div>
  )
}

function FightingView({ cat, data }: { cat: LensCategory; data: LensResponse }) {
  const subs = cat.subs ?? []
  const [subKey, setSubKey] = useState(subs[0]?.key)
  const sub: LensSub | undefined = subs.find(s => s.key === subKey) ?? subs[0]
  return (
    <div className="lens-split">
      <div className="lens-subrail">
        {subs.map(s => (
          <button key={s.key} className={`lens-subcard ${s.key === sub?.key ? 'on' : ''}`} onClick={() => setSubKey(s.key)}>
            <span className="ls-head">
              <span className="cat-icon sub">{SUB_GLYPHS[s.key] ?? '•'}</span>
              <span className="ls-name">{s.label}<span className="mut sm-text">{s.desc}</span></span>
            </span>
            <span className="ls-foot">
              <span className="lens-pair">
                <ChampFace name={data.topChampion} size={22} />
                <span className="vs-badge">vs</span>
                <ChampFace name={data.topChampionOld ?? data.topChampion} size={22} />
              </span>
              <Ring score={s.score} size={46} />
            </span>
          </button>
        ))}
      </div>
      {sub && <TileGrid tiles={sub.tiles} data={data} />}
    </div>
  )
}

const ROLE_FILTERS: Array<{ key: string; label: string }> = [
  { key: '', label: 'All roles' }, { key: 'TOP', label: 'Top' }, { key: 'JUNGLE', label: 'Jungle' },
  { key: 'MIDDLE', label: 'Mid' }, { key: 'BOTTOM', label: 'Bot' }, { key: 'UTILITY', label: 'Support' },
]

// Same windows as the Dashboard, so the two pages always talk about the same slice.
const WINDOWS = [
  { key: '7d', label: 'Last 7d', days: 7 },
  { key: '15d', label: '15d', days: 15 },
  { key: '30d', label: '30d', days: 30 },
  { key: '60d', label: '60d', days: 60 },
  { key: '10g', label: 'Last 10', window: 10 },
  { key: '20g', label: '20', window: 20 },
  { key: '30g', label: '30', window: 30 },
  { key: '40g', label: '40', window: 40 },
  { key: '50g', label: '50', window: 50 },
  { key: '100g', label: '100', window: 100 },
  // The API clamps the window to the stored game count, so this means "all".
  { key: 'all', label: 'All', window: 100000 },
] as const

export default function Coach() {
  const [data, setData] = useState<LensResponse | null | undefined>(undefined)
  const [windowKey, setWindowKey] = useState<(typeof WINDOWS)[number]['key']>('20g')
  const [role, setRole] = useState('')
  const [tab, setTab] = useState('overview')

  useEffect(() => {
    setData(undefined)
    const w = WINDOWS.find(x => x.key === windowKey)!
    api.lens({
      window: 'window' in w ? w.window : undefined,
      days: 'days' in w ? w.days : undefined,
      role,
    }).then(setData).catch(() => setData(null))
  }, [windowKey, role])

  const weakest = useMemo(() => {
    const scored = (data?.categories ?? []).filter(c => c.score !== null)
    return scored.length > 0 ? scored.reduce((a, b) => (a.score! <= b.score! ? a : b)).key : null
  }, [data])

  const current = data?.categories.find(c => c.key === tab)

  return (
    <div className="lens-shell">
      <div className="lens-header card">
        <span className="lens-logo"><CatIcon cat="overview" size={20} /> <strong>COACH</strong></span>
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
        {data && (
          <span className="lens-hstats">
            <span><strong>{data.window}</strong> <span className="mut">GAMES</span></span>
            <span><strong className={data.winrate >= 50 ? 'win' : 'loss'}>{data.winrate}%</strong> <span className="mut">WR</span></span>
          </span>
        )}
      </div>

      <div className="lens-tabs">
        <button className={tab === 'overview' ? 'on' : ''} onClick={() => setTab('overview')}>
          <CatIcon cat="overview" size={15} /> Overview
        </button>
        {(data?.categories ?? []).map(c => (
          <button key={c.key} className={tab === c.key ? 'on' : ''} onClick={() => setTab(c.key)}>
            <CatIcon cat={c.key} size={15} /> {c.label}
          </button>
        ))}
      </div>

      {data === undefined && <div className="empty">Crunching your games…</div>}
      {data === null && <div className="empty">Not enough ranked games {role ? `as ${ROLE_FILTERS.find(r => r.key === role)?.label}` : ''} for coaching stats (need 8+).</div>}

      {data && tab === 'overview' && (
        <div className="lens-panel">
          <div className="lens-overview">
            {data.categories.map(c => (
              <button key={c.key} className={`lens-cat ${c.key === weakest ? 'weak' : ''}`} onClick={() => setTab(c.key)}>
                <span className="lc-head">
                  <CatIcon cat={c.key} />
                  <span className="lc-name">
                    {c.label}
                    {c.key === weakest && <span className="lc-improve">skill to improve</span>}
                  </span>
                </span>
                <span className="ls-foot">
                  <span className="lens-pair">
                    <ChampFace name={data.topChampion} size={24} />
                    <span className="vs-badge">vs</span>
                    <ChampFace name={data.topChampionOld ?? data.topChampion} size={24} />
                  </span>
                  <Ring score={c.score} />
                </span>
              </button>
            ))}
          </div>
          <p className="mut sm-text" style={{ margin: '12px 2px 0' }}>
            Scores are your last {data.window} games' percentile within your own {data.games}-game history — 73 means
            "better than 73% of your past games". NEW vs OLD compares the window against everything before it; the
            portraits are each era's most-played champion. The Dashboard's "Vs the ladder" card is the external benchmark.
          </p>
        </div>
      )}

      {data && current && tab !== 'overview' && (
        <div className="lens-panel">
          <div className="lens-cat-head">
            <CatIcon cat={current.key} />
            <h2>{current.label}</h2>
            <Ring score={current.score} size={44} />
          </div>
          {current.subs
            ? <FightingView cat={current} data={data} />
            : <TileGrid tiles={current.tiles ?? []} data={data} />}
        </div>
      )}
    </div>
  )
}

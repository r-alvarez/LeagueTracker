import { Fragment, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { AnalyticsSummary, LpPerGame, LpPoint, SplitRow, Stats, Status } from '../types'
import LpLineChart from '../components/LpLineChart'
import LpPerGameBars from '../components/LpPerGameBars'
import { LaneGoldChart, RollingWinRateChart } from '../components/TrendCharts'
import ChampBadge from '../components/ChampBadge'
import ProfileCard from '../components/ProfileCard'
import ProfileHeader from '../components/ProfileHeader'
import RoleIcon from '../components/RoleIcon'
import { FormDots, WinrateBar } from '../components/Stats'

const QUEUES = ['Solo/Duo', 'Flex'] as const

const WINDOWS = [
  { key: '7d', label: 'Last 7d', days: 7 },
  { key: '15d', label: '15d', days: 15 },
  { key: '30d', label: '30d', days: 30 },
  { key: '60d', label: '60d', days: 60 },
  { key: '10g', label: 'Last 10', lastGames: 10 },
  { key: '20g', label: '20', lastGames: 20 },
  { key: '30g', label: '30', lastGames: 30 },
  { key: '40g', label: '40', lastGames: 40 },
  { key: '50g', label: '50', lastGames: 50 },
  { key: '100g', label: '100', lastGames: 100 },
  { key: 'all', label: 'All' },
] as const

const signed = (v: number | null | undefined) => (v === null || v === undefined ? '—' : `${v > 0 ? '+' : ''}${v}`)
const pct = (v: number) => `${Math.round(v * 100)}%`
const kdaCls = (v: number) => (v >= 5 ? 'kda-5' : v >= 4 ? 'kda-4' : v >= 3 ? 'kda-3' : v < 1 ? 'kda-low' : '')

// No LP column here on purpose: per-champion LP is only ever a PARTIAL sum
// (games missed by live capture carry none), and a partial sum over a biased
// subsample reads as a verdict the winrate column already gives honestly.
type SortKey = 'key' | 'games' | 'winRate' | 'kda' | 'kp' | 'csPerMin' | 'laneGoldAt10' | 'dpm' | 'deathsPerGame'

const fmtClock = (sec: number) => {
  const s = Math.round(sec)
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}
const fmtPlaytime = (sec: number) => {
  const h = Math.floor(sec / 3600)
  const m = Math.round((sec % 3600) / 60)
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}

function KdaCell({ kills, deaths, assists, ratio }: { kills: number; deaths: number; assists: number; ratio: number }) {
  return (
    <span className="kda-cell">
      {kills.toFixed(1)} / <span className="kda-deaths">{deaths.toFixed(1)}</span> / {assists.toFixed(1)}
      <span className={`kda-ratio ${kdaCls(ratio)}`}> ({ratio.toFixed(1)})</span>
    </span>
  )
}

const goldCls = (v: number | null) => (v !== null ? (v >= 0 ? 'win' : 'loss') : '')

// The champion drill-down reads as its own mini-dashboard: a band of stat
// tiles over a scrollable matchup table. Same tile/winrate-bar vocabulary the
// KPI bands and champion rows already speak, so an expanded row never looks
// like a different app grafted into the table.
function MatchupDrill({ row }: { row: SplitRow }) {
  const detail = row.detail!
  const tiles: { label: string; value: string | number; sub?: string; cls?: string }[] = [
    { label: 'KP', value: pct(row.kp) },
    { label: 'DPM', value: Math.round(row.dpm) },
    { label: 'Deaths/game', value: row.deathsPerGame },
    { label: 'CS@10', value: detail.csAt10 },
    { label: 'Solo kills/game', value: detail.soloKillsPerGame },
    { label: 'Vision/min', value: detail.visionPerMin },
    { label: 'Dodges/game', value: detail.skillshotsDodgedPerGame },
    { label: 'Avg game', value: fmtClock(detail.avgGameSec) },
    { label: 'Total played', value: fmtPlaytime(detail.totalGameSec) },
    { label: 'Triple / Quadra / Penta', value: `${detail.triples} / ${detail.quadras} / ${detail.pentas}` },
    {
      label: 'Blue side WR', cls: 'side-blue',
      value: detail.side.blue.games > 0 ? pct(detail.side.blue.winRate) : '—',
      sub: `${detail.side.blue.games} games`,
    },
    {
      label: 'Red side WR', cls: 'side-red',
      value: detail.side.red.games > 0 ? pct(detail.side.red.winRate) : '—',
      sub: `${detail.side.red.games} games`,
    },
  ]
  const opponents = detail.matchups.length

  return (
    <div className="champ-drill">
      <div className="drill-tiles">
        {tiles.map(t => (
          <div key={t.label} className={`mini-tile ${t.cls ?? ''}`}>
            <div className="label">{t.label}</div>
            <div className="value">{t.value}</div>
            {t.sub && <div className="sub">{t.sub}</div>}
          </div>
        ))}
      </div>

      <div className="matchup-block">
        <div className="matchup-head">
          <span>Lane matchups</span>
          {opponents > 0 && <span className="mut">{opponents} {opponents === 1 ? 'opponent' : 'opponents'} faced</span>}
        </div>
        {opponents > 0 ? (
          <div className="matchup-scroll">
            <table className="data matchup-table">
              <thead>
                <tr>
                  <th>Matchup</th><th className="num">Games</th><th>WR</th><th className="num">K / D / A</th>
                  <th className="num col-extra">KP</th><th className="num">G@10</th><th className="num col-extra">Avg game</th>
                </tr>
              </thead>
              <tbody>
                {detail.matchups.map(mu => (
                  <tr key={mu.opponent}>
                    <td><ChampBadge name={mu.opponent} small /></td>
                    <td className="num">{mu.games}</td>
                    <td className="wr-cell">
                      <WinrateBar wins={mu.wins} losses={mu.games - mu.wins} />
                      <span className="wr-rec">{mu.wins}W-{mu.games - mu.wins}L</span>
                    </td>
                    <td className="num"><KdaCell kills={mu.avgKills} deaths={mu.avgDeaths} assists={mu.avgAssists} ratio={mu.kda} /></td>
                    <td className="num col-extra">{pct(mu.kp)}</td>
                    <td className={`num ${goldCls(mu.laneGoldAt10)}`}>{signed(mu.laneGoldAt10)}</td>
                    <td className="num col-extra">{fmtClock(mu.avgGameSec)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="matchup-empty">No lane opponents identified in this window.</div>
        )}
      </div>
    </div>
  )
}

interface SummaryRow {
  games: number
  wins: number
  losses: number
  kda: number
  avgKills: number
  avgDeaths: number
  avgAssists: number
  kp: number
  csPerMin: number
  laneGoldAt10: number | null
  dpm: number
  last5: boolean[]
}

const MIN_GAMES_STEPS = [
  { min: 0, label: 'All' },
  { min: 5, label: '5+ games' },
  { min: 10, label: '10+ games' },
] as const

function SplitTable({ title, rows, champIcons, compact, summary }: {
  title: string; rows: SplitRow[]; champIcons?: boolean; compact?: boolean; summary?: SummaryRow
}) {
  const [open, setOpen] = useState<string | null>(null)
  const [sort, setSort] = useState<{ key: SortKey; dir: 1 | -1 }>({ key: 'games', dir: -1 })
  const [search, setSearch] = useState('')
  const [minGames, setMinGames] = useState(0)

  const query = search.trim().toLowerCase()
  const filtered = useMemo(
    () => rows.filter(r => r.games >= minGames && r.key.toLowerCase().includes(query)),
    [rows, minGames, query],
  )

  // Click a header to sort by it; same header again flips the direction.
  // Rows without a value (LP unknown, no lane opponent) always sink to the end.
  const sorted = useMemo(() => [...filtered].sort((a, b) => {
    if (sort.key === 'key') return sort.dir * a.key.localeCompare(b.key)
    const av = a[sort.key], bv = b[sort.key]
    if (av === null) return bv === null ? 0 : 1
    if (bv === null) return -1
    return sort.dir * (av - bv)
  }), [filtered, sort])

  const Th = ({ k, label, num, extra }: { k: SortKey; label: string; num?: boolean; extra?: boolean }) => (
    <th className={`sortable ${num ? 'num' : ''} ${extra ? 'col-extra' : ''} ${sort.key === k ? 'sorted' : ''}`}
      onClick={() => setSort(s => (s.key === k ? { key: k, dir: -s.dir as 1 | -1 } : { key: k, dir: k === 'key' ? 1 : -1 }))}>
      {label}<span className="sort-arrow">{sort.key === k ? (sort.dir === -1 ? '▾' : '▴') : ''}</span>
    </th>
  )

  const WrCell = ({ wins, losses, record }: { wins: number; losses: number; record?: boolean }) => (
    <td className="wr-cell">
      <WinrateBar wins={wins} losses={losses} />
      {record && <span className="wr-rec">{wins}W-{losses}L</span>}
    </td>
  )

  const columns = compact ? 5 : 9

  return (
    <div className="card">
      <h2>{title}{champIcons && <span className="mut" style={{ fontWeight: 400 }}> — click a row for matchups</span>}</h2>
      {champIcons && rows.length > 0 && (
        <div className="table-filters">
          <input className="text search" type="search" placeholder="Find champion" aria-label="Find champion"
            value={search} onChange={e => setSearch(e.target.value)} />
          <div className="seg mini">
            {MIN_GAMES_STEPS.map(s => (
              <button key={s.min} className={minGames === s.min ? 'on' : ''} onClick={() => setMinGames(s.min)}>{s.label}</button>
            ))}
          </div>
          {filtered.length !== rows.length && <span className="mut sm-text">{filtered.length} of {rows.length}</span>}
        </div>
      )}
      {rows.length === 0 ? <div className="empty">No games in this window.</div> : (
        <div className="table-scroll tall">
          <table className="data">
            <thead>
              <tr>
                <Th k="key" label={champIcons ? 'Champion' : 'Role'} /><Th k="games" label="Games" num /><Th k="winRate" label="WR" />
                <Th k="kda" label="KDA" num />
                {compact
                  ? <Th k="deathsPerGame" label="Deaths" num extra />
                  : <>
                    <Th k="kp" label="KP" num extra /><Th k="csPerMin" label="CS/m" num extra />
                    <Th k="laneGoldAt10" label="G@10" num extra /><Th k="dpm" label="DPM" num extra />
                    <th className="last5-col">Last 5</th>
                  </>}
              </tr>
            </thead>
            <tbody>
              {summary && !compact && (
                <tr className="all-row">
                  <td><span className="champ sm"><span className="all-star">✳</span> <span className="champ-name">All champions</span></span></td>
                  <td className="num">{summary.games}</td>
                  <WrCell wins={summary.wins} losses={summary.losses} record />
                  <td className="num"><KdaCell kills={summary.avgKills} deaths={summary.avgDeaths} assists={summary.avgAssists} ratio={summary.kda} /></td>
                  <td className="num col-extra">{pct(summary.kp)}</td>
                  <td className="num col-extra">{summary.csPerMin}</td>
                  <td className={`num col-extra ${goldCls(summary.laneGoldAt10)}`}>{signed(summary.laneGoldAt10)}</td>
                  <td className="num col-extra">{Math.round(summary.dpm)}</td>
                  <td className="last5-col"><FormDots results={summary.last5} /></td>
                </tr>
              )}
              {sorted.map(r => (
                <Fragment key={r.key}>
                  <tr onClick={() => r.detail && setOpen(open === r.key ? null : r.key)}
                    style={r.detail ? { cursor: 'pointer' } : undefined}>
                    <td>{champIcons
                      ? <ChampBadge name={r.key} small />
                      : <span className="champ sm"><RoleIcon role={r.key} /> <span className="champ-name">{r.key}</span></span>}</td>
                    <td className="num">{r.games}</td>
                    <WrCell wins={r.wins} losses={r.games - r.wins} record={!compact} />
                    <td className="num"><KdaCell kills={r.avgKills} deaths={r.avgDeaths} assists={r.avgAssists} ratio={r.kda} /></td>
                    {compact
                      ? <td className="num col-extra">{r.deathsPerGame}</td>
                      : <>
                        <td className="num col-extra">{pct(r.kp)}</td>
                        <td className="num col-extra">{r.csPerMin}</td>
                        <td className={`num col-extra ${goldCls(r.laneGoldAt10)}`}>{signed(r.laneGoldAt10)}</td>
                        <td className="num col-extra">{Math.round(r.dpm)}</td>
                        <td className="last5-col"><FormDots results={r.last5} /></td>
                      </>}
                  </tr>
                  {open === r.key && r.detail && (
                    <tr className="drill">
                      <td colSpan={columns}>
                        <MatchupDrill row={r} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
              {sorted.length === 0 && (
                <tr><td colSpan={columns} className="empty-cell">No champions match the filter.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

export default function Dashboard() {
  const [status, setStatus] = useState<Status | null>(null)
  const [windowKey, setWindowKey] = useState<(typeof WINDOWS)[number]['key']>('30g')
  const [stats, setStats] = useState<Stats | null>(null)
  const [queue, setQueue] = useState<(typeof QUEUES)[number]>('Solo/Duo')
  const [lpPoints, setLpPoints] = useState<LpPoint[]>([])
  const [lpGames, setLpGames] = useState<LpPerGame[]>([])
  const [deaths, setDeaths] = useState<AnalyticsSummary | null>(null)
  const [kpiOpen, setKpiOpen] = useState(false)

  // Freshly captured games should show up without a manual reload, so every
  // loader refetches on a quiet interval alongside its trigger.
  useEffect(() => {
    const load = () => {
      api.status().then(setStatus).catch(console.error)
      api.lpPerGame().then(setLpGames).catch(console.error)
      api.analytics(20).then(setDeaths).catch(console.error)
    }
    load()
    const id = setInterval(load, 60_000)
    return () => clearInterval(id)
  }, [])

  useEffect(() => {
    const w = WINDOWS.find(x => x.key === windowKey)!
    const load = () =>
      api.stats({ days: 'days' in w ? w.days : undefined, lastGames: 'lastGames' in w ? w.lastGames : undefined })
        .then(setStats).catch(console.error)
    load()
    const id = setInterval(load, 60_000)
    return () => clearInterval(id)
  }, [windowKey])

  useEffect(() => {
    const load = () => api.lpHistory(queue).then(setLpPoints).catch(console.error)
    load()
    const id = setInterval(load, 60_000)
    return () => clearInterval(id)
  }, [queue])

  // The API serves per-game LP newest-first; chronological order here so the
  // window slices below can take "the last N" from the end.
  const queueGames = useMemo(
    () => lpGames
      .filter(g => g.queueName.includes(queue === 'Flex' ? 'Flex' : 'Solo'))
      .sort((a, b) => a.gameEndUtc.localeCompare(b.gameEndUtc)),
    [lpGames, queue],
  )

  // The LP charts follow the same window as everything above. Count windows
  // ("Last 30") count per queue - a queue-scoped ladder chart windowed by
  // games of the other queue would be nonsense.
  const windowedLpGames = useMemo(() => {
    const w = WINDOWS.find(x => x.key === windowKey)!
    if ('days' in w) {
      const cutoff = Date.now() - w.days * 86_400_000
      return queueGames.filter(g => new Date(g.gameEndUtc).getTime() >= cutoff)
    }
    if ('lastGames' in w) return queueGames.slice(-w.lastGames)
    return queueGames
  }, [queueGames, windowKey])

  const windowedLpPoints = useMemo(() => {
    const w = WINDOWS.find(x => x.key === windowKey)!
    const cutoff = 'days' in w
      ? Date.now() - w.days * 86_400_000
      : 'lastGames' in w && windowedLpGames.length > 0
        ? new Date(windowedLpGames[0].gameEndUtc).getTime()
        : null
    if (cutoff === null) return lpPoints
    // Keep the last snapshot before the cutoff: the line enters the window at
    // its pre-window rank, so the first game's movement stays visible.
    const idx = lpPoints.findIndex(p => new Date(p.timestampUtc).getTime() >= cutoff)
    return idx === -1 ? lpPoints.slice(-1) : lpPoints.slice(Math.max(0, idx - 1))
  }, [lpPoints, windowedLpGames, windowKey])

  const o = stats?.overall
  const s = stats?.scope
  const windowLabel = useMemo(() => {
    const w = WINDOWS.find(x => x.key === windowKey)!
    if (w.key === 'all') return `all ${s?.games ?? ''} ranked games`.trim()
    if ('days' in w) return `the last ${w.days} days (${s?.games ?? 0} games)`
    return `your last ${w.lastGames} ranked games`
  }, [windowKey, s?.games])

  return (
    <>
      <ProfileHeader status={status} stats={stats} lpGames={lpGames} />

      <div className="filters">
        <div className="seg" role="tablist" aria-label="Window">
          {WINDOWS.map(w => (
            <button key={w.key} className={w.key === windowKey ? 'on' : ''} onClick={() => setWindowKey(w.key)}>{w.label}</button>
          ))}
        </div>
        {s && <span className="mut">ranked · {s.dateFrom} → {s.dateTo}</span>}
        {status?.apiKeyConfigured === false && <span className="mut">· no API key - live capture paused (see Data & sync)</span>}
      </div>

      {stats && s && o && (
        <>
          {/* Six headline numbers, one calm band; everything second-order sits
              behind the expander so the first read is never a wall of figures.
              LP deltas live in the profile header, so they aren't repeated here. */}
          <div className="card kpi-card" style={{ marginBottom: 16 }}>
            <div className="kpi-band">
              <div className="kpi">
                <div className="label">Record</div>
                <div className="value">{s.wins}-{s.losses}</div>
                <div className="sub">{pct(s.winRate)} WR · {s.champions} champs</div>
              </div>
              <div className="kpi">
                <div className="label">KDA</div>
                <div className="value">{o.kda}</div>
                <div className="sub">KP {pct(o.kp)}</div>
              </div>
              <div className="kpi">
                <div className="label">Damage/min</div>
                <div className="value">{o.dpm}</div>
                <div className="sub">{o.gpm} gold/min</div>
              </div>
              <div className="kpi">
                <div className="label">CS@10</div>
                <div className="value">{o.csAt10}</div>
                <div className="sub">{o.csPerMin} CS/min</div>
              </div>
              <div className="kpi">
                <div className="label">Lane gold@10</div>
                <div className={`value ${o.laneGoldAt10 !== null ? (o.laneGoldAt10 >= 0 ? 'win' : 'loss') : ''}`}>{signed(o.laneGoldAt10)}</div>
                <div className="sub">CS diff {signed(o.laneCsAt10)}</div>
              </div>
              <div className="kpi">
                <div className="label">Deaths/game</div>
                <div className="value">{o.deathsPerGame}</div>
                <div className="sub">{o.deathsPre10} before 10:00</div>
              </div>
            </div>
            <button className="kpi-toggle" onClick={() => setKpiOpen(v => !v)}>
              {kpiOpen ? 'Hide detail ▴' : 'More detail ▾'}
            </button>
            {kpiOpen && (
              <div className="kpi-detail">
                <div className="stat-row">
                  <span className="k">Damage/min by phase<small>early · mid · late game</small></span>
                  <span className="v">{o.dpmEarly} · {o.dpmMid} · {o.dpmLate}</span>
                </div>
                <div className="stat-row">
                  <span className="k">Damage taken<small>per minute</small></span>
                  <span className="v">{o.damageTakenPerMin}</span>
                </div>
                <div className="stat-row">
                  <span className="k">Deaths by phase<small>pre-10 · 10–20 · 20+</small></span>
                  <span className="v">{o.deathsPre10} · {o.deaths10To20} · {o.deathsPost20}</span>
                </div>
                <div className="stat-row">
                  <span className="k">Vision<small>{o.controlWardsPerGame} control wards/game</small></span>
                  <span className="v">{o.visionPerMin}/min</span>
                </div>
                <div className="stat-row">
                  <span className="k">Solo kills/game<small>multikills {o.triples} triple · {o.quadras} quadra · {o.pentas} penta</small></span>
                  <span className="v">{o.soloKillsPerGame}</span>
                </div>
                <div className="stat-row">
                  <span className="k">Skillshots/game<small>dodged · hit</small></span>
                  <span className="v">{o.skillshotsDodgedPerGame} · {o.skillshotsHitPerGame}</span>
                </div>
              </div>
            )}
          </div>

          {stats.observations.length > 0 && (
            <div className="card" style={{ marginBottom: 16 }}>
              <h2>Key observations</h2>
              <div className="obs-grid">
                {stats.observations.map(obs => <div key={obs} className="obs-item">{obs}</div>)}
              </div>
            </div>
          )}

          <div className="card" style={{ marginBottom: 16 }}>
            <h2>Strengths &amp; weaknesses <span className="mut" style={{ fontWeight: 400 }}>— what separates your wins from losses</span></h2>
            <ProfileCard profile={stats.profile} windowLabel={windowLabel} />
          </div>

          {stats.followIn.totalDeaths > 0 && (
            <div className="card kpi-card" style={{ marginBottom: 16 }}>
              <h2>Death context <span className="mut" style={{ fontWeight: 400 }}>— following teammates in</span></h2>
              {/* Rates lead, counts are context - raw counts just scale with the
                  window and read meaningless under "All". */}
              <div className="kpi-band cols-5">
                <div className="kpi">
                  <div className="label">Follow-in deaths</div>
                  <div className="value">{pct(stats.followIn.rate)}</div>
                  <div className="sub">{stats.followIn.followIns} of {stats.followIn.totalDeaths} deaths</div>
                </div>
                <div className="kpi">
                  <div className="label">Got nothing back</div>
                  <div className="value">{stats.followIn.followIns > 0 ? pct(stats.followIn.pureLoss / stats.followIn.followIns) : '—'}</div>
                  <div className="sub">{stats.followIn.pureLoss} follow-ins · no enemy fell within 10s</div>
                </div>
                <div className="kpi">
                  <div className="label">Already-lost fights</div>
                  <div className="value">{stats.followIn.followIns > 0 ? pct(stats.followIn.twoPlusDown / stats.followIn.followIns) : '—'}</div>
                  <div className="sub">{stats.followIn.twoPlusDown} with 2+ allies already down</div>
                </div>
                <div className="kpi">
                  <div className="label">While behind</div>
                  {(() => {
                    const gs = stats.followIn.goldState
                    const total = gs.behind + gs.even + gs.ahead
                    return <>
                      <div className="value">{total > 0 ? pct(gs.behind / total) : '—'}</div>
                      <div className="sub">{gs.behind}↓ {gs.even}= {gs.ahead}↑ team gold (±1500)</div>
                    </>
                  })()}
                </div>
                <div className="kpi">
                  <div className="label">Followed in after</div>
                  <div className="value">{stats.followIn.byRole[0]?.key ?? '—'}</div>
                  <div className="sub">
                    {stats.followIn.byRole.slice(0, 3).map(r => `${r.key} ${r.count}`).join(' · ') || 'teammate role'}
                  </div>
                </div>
              </div>
            </div>
          )}

          <div className="grid two-col" style={{ marginBottom: 16 }}>
            <div className="card">
              <h2>Form <span className="mut" style={{ fontWeight: 400 }}>— rolling win rate</span></h2>
              <RollingWinRateChart series={stats.series} />
            </div>
            <div className="card">
              <h2>Laning <span className="mut" style={{ fontWeight: 400 }}>— gold vs your laner at 10:00</span></h2>
              <LaneGoldChart series={stats.series} />
            </div>
          </div>

          <div className="grid two-col" style={{ marginBottom: 16 }}>
            <div className="card">
              <h2>Lane state <span className="mut" style={{ fontWeight: 400 }}>— gold vs your laner, ±500</span></h2>
              <div className="table-scroll">
              <table className="data">
                <thead>
                  <tr>
                    <th>State</th>
                    <th className="num">@10 W-L</th><th className="num">WR</th>
                    <th className="num">@15 W-L</th><th className="num">WR</th>
                  </tr>
                </thead>
                <tbody>
                  {([
                    ['Ahead', 'win', stats.winrateByLaneState.ahead, stats.winrateByLaneState.at15.ahead],
                    ['Even', '', stats.winrateByLaneState.even, stats.winrateByLaneState.at15.even],
                    ['Behind', 'loss', stats.winrateByLaneState.behind, stats.winrateByLaneState.at15.behind],
                  ] as const).map(([label, cls, b10, b15]) => (
                    <tr key={label}>
                      <td className={cls}>{label}</td>
                      <td className="num">{b10.wins}-{b10.games - b10.wins}</td>
                      <td className="num">{b10.games > 0 ? pct(b10.winRate) : '—'}</td>
                      <td className="num">{b15.wins}-{b15.games - b15.wins}</td>
                      <td className="num">{b15.games > 0 ? pct(b15.winRate) : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              </div>
              <div className="stat-list" style={{ marginTop: 10 }}>
                <div className="stat-row">
                  <span className="k">Leads held to 20:00<small>still ≥ +500 vs your laner</small></span>
                  <span className="v">{stats.winrateByLaneState.trajectory.leadsHeldAt20.held}<span className="mut"> / {stats.winrateByLaneState.trajectory.leadsHeldAt20.of}</span></span>
                </div>
                <div className="stat-row">
                  <span className="k">Deficits recovered by 20:00<small>back above −500</small></span>
                  <span className="v">{stats.winrateByLaneState.trajectory.deficitsRecoveredAt20.recovered}<span className="mut"> / {stats.winrateByLaneState.trajectory.deficitsRecoveredAt20.of}</span></span>
                </div>
                <div className="stat-row">
                  <span className="k">Leads at 20:00 → wins<small>any game ≥ +500 vs your laner at 20:00</small></span>
                  <span className="v">{stats.winrateByLaneState.trajectory.leadsAt20Won.won}<span className="mut"> / {stats.winrateByLaneState.trajectory.leadsAt20Won.of}</span></span>
                </div>
              </div>
            </div>
            <div className="card">
              <h2>Where you die</h2>
              {stats.deathZones.length === 0 ? <div className="empty">No death data in this window.</div> : (
                <div className="table-scroll">
                  <table className="data">
                    <thead><tr><th>Zone</th><th className="num">Deaths</th><th className="num">Share</th></tr></thead>
                    <tbody>
                      {stats.deathZones.map(z => (
                        <tr key={z.key}><td>{z.key}</td><td className="num">{z.count}</td><td className="num">{pct(z.share)}</td></tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>

          {/* Full-width so the dpm.lol-style column set (KDA line, DPM, form
              dots) fits without horizontal scroll; role + collapse pair below. */}
          <div style={{ marginBottom: 16 }}>
            <SplitTable title="Champion performance" rows={stats.byChampion} champIcons
              summary={{
                games: s.games, wins: s.wins, losses: s.losses,
                kda: o.kda, avgKills: o.avgKills, avgDeaths: o.avgDeaths, avgAssists: o.avgAssists,
                kp: o.kp, csPerMin: o.csPerMin, laneGoldAt10: o.laneGoldAt10, dpm: o.dpm,
                last5: stats.series.slice(-5).map(p => p.win),
              }} />
          </div>
          <div className="grid two-col" style={{ marginBottom: 16, alignItems: 'start' }}>
            <SplitTable title="Role performance" rows={stats.byRole} compact />
            {deaths && deaths.games > 0 && (
              <div className="card">
                <h2>Collapse profile <span className="mut" style={{ fontWeight: 400 }}>— last {deaths.games} ranked, {deaths.totalDeaths} deaths</span></h2>
                <div className="stat-list">
                  <div className="stat-row">
                    <span className="k">Collapse deaths<small>3+ enemies actually there · avg {deaths.avgEnemiesNearDeath ?? '—'} near each death</small></span>
                    <span className="v">{deaths.collapseDeaths}</span>
                  </div>
                  <div className="stat-row">
                    <span className="k">No ally in range<small>nearest ally avg {deaths.avgNearestAllyDistAtDeath ?? '—'} units away</small></span>
                    <span className="v">{deaths.isolatedDeaths}</span>
                  </div>
                  <div className="stat-row">
                    <span className="k">Right after an objective<small>within 90s of your team taking one</small></span>
                    <span className="v">{deaths.postObjectiveDeaths}</span>
                  </div>
                  <div className="stat-row">
                    <span className="k">Burst vs whittled<small>one source ≥70% of the damage vs ground down</small></span>
                    <span className="v">{deaths.burstDeaths} / {deaths.totalDeaths - deaths.burstDeaths}</span>
                  </div>
                  <div className="stat-row">
                    <span className="k">Time in enemy half<small>nearest ally all game: {deaths.avgNearestAllyDistOverall.toFixed(0)} units</small></span>
                    <span className="v">{deaths.avgTimeInEnemyHalfPct.toFixed(0)}%</span>
                  </div>
                </div>
                <p className="mut sm-text" style={{ margin: '10px 0 0' }}>
                  Positions between the 60s frames are interpolated - estimates, not gospel.
                </p>
              </div>
            )}
          </div>
        </>
      )}

      {status && !status.hideLp && (
        <>
          <div className="filters">
            <div className="seg" role="tablist" aria-label="Queue">
              {QUEUES.map(q => (
                <button key={q} className={q === queue ? 'on' : ''} onClick={() => setQueue(q)}>{q}</button>
              ))}
            </div>
          </div>
          <div className="grid two-col">
            <div className="card">
              <h2>LP over time — {queue}</h2>
              <LpLineChart points={windowedLpPoints} />
            </div>
            <div className="card">
              <h2>LP per day — {queue} <span className="mut" style={{ fontWeight: 400 }}>— hover a day for its games</span></h2>
              <LpPerGameBars games={windowedLpGames} points={windowedLpPoints} />
            </div>
          </div>
        </>
      )}
    </>
  )
}

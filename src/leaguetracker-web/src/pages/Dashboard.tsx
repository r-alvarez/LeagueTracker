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
import { WinrateBar } from '../components/Stats'

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
type SortKey = 'key' | 'games' | 'winRate' | 'kda' | 'kp' | 'csPerMin' | 'laneGoldAt10' | 'deathsPerGame'

// The champion drill-down reads as its own mini-dashboard: a band of stat
// tiles over a scrollable column of matchup widgets. Same tile/winrate-bar
// vocabulary the KPI bands and champion rows already speak, so an expanded row
// never looks like a different app grafted into the table.
function MatchupDrill({ detail, dpm }: { detail: NonNullable<SplitRow['detail']>; dpm: number }) {
  const tiles: { label: string; value: string | number }[] = [
    { label: 'Avg K / D / A', value: `${detail.avgKills} / ${detail.avgDeaths} / ${detail.avgAssists}` },
    { label: 'CS@10', value: detail.csAt10 },
    { label: 'Solo kills/game', value: detail.soloKillsPerGame },
    { label: 'Vision/min', value: detail.visionPerMin },
    { label: 'Dodges/game', value: detail.skillshotsDodgedPerGame },
    { label: 'DPM', value: Math.round(dpm) },
  ]
  const opponents = detail.matchups.length

  return (
    <div className="champ-drill">
      <div className="drill-tiles">
        {tiles.map(t => (
          <div key={t.label} className="mini-tile">
            <div className="label">{t.label}</div>
            <div className="value">{t.value}</div>
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
            {detail.matchups.map(mu => {
              const wins = Math.round(mu.winRate * mu.games)
              return (
                <div key={mu.opponent} className="matchup-row">
                  <div className="mu-champ"><ChampBadge name={mu.opponent} small /></div>
                  <span className="mu-games">{mu.games}G</span>
                  <WinrateBar wins={wins} losses={mu.games - wins} />
                  <span className="mu-metric">G@10<b className={mu.laneGoldAt10 !== null ? (mu.laneGoldAt10 >= 0 ? 'win' : 'loss') : ''}>{signed(mu.laneGoldAt10)}</b></span>
                  <span className="mu-metric">KDA<b>{mu.kda}</b></span>
                </div>
              )
            })}
          </div>
        ) : (
          <div className="matchup-empty">No lane opponents identified in this window.</div>
        )}
      </div>
    </div>
  )
}

function SplitTable({ title, rows, champIcons, compact }: { title: string; rows: SplitRow[]; champIcons?: boolean; compact?: boolean }) {
  const [open, setOpen] = useState<string | null>(null)
  const [sort, setSort] = useState<{ key: SortKey; dir: 1 | -1 }>({ key: 'games', dir: -1 })

  // Click a header to sort by it; same header again flips the direction.
  // Rows without a value (LP unknown, no lane opponent) always sink to the end.
  const sorted = useMemo(() => [...rows].sort((a, b) => {
    if (sort.key === 'key') return sort.dir * a.key.localeCompare(b.key)
    const av = a[sort.key], bv = b[sort.key]
    if (av === null) return bv === null ? 0 : 1
    if (bv === null) return -1
    return sort.dir * (av - bv)
  }), [rows, sort])

  const Th = ({ k, label, num }: { k: SortKey; label: string; num?: boolean }) => (
    <th className={`sortable ${num ? 'num' : ''} ${sort.key === k ? 'sorted' : ''}`}
      onClick={() => setSort(s => (s.key === k ? { key: k, dir: -s.dir as 1 | -1 } : { key: k, dir: k === 'key' ? 1 : -1 }))}>
      {label}<span className="sort-arrow">{sort.key === k ? (sort.dir === -1 ? '▾' : '▴') : ''}</span>
    </th>
  )

  return (
    <div className="card">
      <h2>{title}{champIcons && <span className="mut" style={{ fontWeight: 400 }}> — click a row for matchups</span>}</h2>
      {rows.length === 0 ? <div className="empty">No games in this window.</div> : (
        <div className="table-scroll tall">
          <table className="data">
            <thead>
              <tr>
                <Th k="key" label={champIcons ? 'Champion' : 'Role'} /><Th k="games" label="Games" num /><Th k="winRate" label="WR" />
                <Th k="kda" label="KDA" num />
                {!compact && <><Th k="kp" label="KP" num /><Th k="csPerMin" label="CS/m" num /><Th k="laneGoldAt10" label="G@10" num /></>}
                <Th k="deathsPerGame" label="Deaths" num />
              </tr>
            </thead>
            <tbody>
              {sorted.map(r => (
                <Fragment key={r.key}>
                  <tr onClick={() => r.detail && setOpen(open === r.key ? null : r.key)}
                    style={r.detail ? { cursor: 'pointer' } : undefined}>
                    <td>{champIcons
                      ? <ChampBadge name={r.key} small />
                      : <span className="champ sm"><RoleIcon role={r.key} /> <span className="champ-name">{r.key}</span></span>}</td>
                    <td className="num">{r.games}</td>
                    <td><WinrateBar wins={r.wins} losses={r.games - r.wins} /></td>
                    <td className="num"><span className={`kda-ratio ${kdaCls(r.kda)}`} style={{ fontSize: 13 }}>{r.kda.toFixed(2)}</span></td>
                    {!compact && <>
                      <td className="num">{pct(r.kp)}</td>
                      <td className="num">{r.csPerMin}</td>
                      <td className="num">{signed(r.laneGoldAt10)}</td>
                    </>}
                    <td className="num">{r.deathsPerGame}</td>
                  </tr>
                  {open === r.key && r.detail && (
                    <tr className="drill">
                      <td colSpan={compact ? 6 : 8}>
                        <MatchupDrill detail={r.detail} dpm={r.dpm} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
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

  const queueGames = useMemo(
    () => lpGames.filter(g => g.queueName.includes(queue === 'Flex' ? 'Flex' : 'Solo')),
    [lpGames, queue],
  )

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
        {status && !status.apiKeyConfigured && <span className="mut">· no API key - live capture paused (see Data & sync)</span>}
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

          <div className="grid two-col" style={{ marginBottom: 16 }}>
            <SplitTable title="Champion performance" rows={stats.byChampion} champIcons />
            <div className="grid" style={{ alignContent: 'start' }}>
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
              <LpLineChart points={lpPoints} />
            </div>
            <div className="card">
              <h2>LP per day — {queue} <span className="mut" style={{ fontWeight: 400 }}>— hover a day for its games</span></h2>
              <LpPerGameBars games={queueGames} points={lpPoints} />
            </div>
          </div>
        </>
      )}
    </>
  )
}

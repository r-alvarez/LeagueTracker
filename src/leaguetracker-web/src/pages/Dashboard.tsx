import { Fragment, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { AnalyticsSummary, LpPerGame, LpPoint, SplitRow, Stats, Status } from '../types'
import LpLineChart from '../components/LpLineChart'
import LpPerGameBars from '../components/LpPerGameBars'
import { LaneGoldChart, RollingWinRateChart } from '../components/TrendCharts'
import ChampBadge from '../components/ChampBadge'
import ProfileCard from '../components/ProfileCard'
import RoleIcon from '../components/RoleIcon'

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

function SplitTable({ title, rows, champIcons, compact }: { title: string; rows: SplitRow[]; champIcons?: boolean; compact?: boolean }) {
  const [open, setOpen] = useState<string | null>(null)
  return (
    <div className="card">
      <h2>{title}{champIcons && <span className="mut" style={{ fontWeight: 400 }}> — click a row for matchups</span>}</h2>
      {rows.length === 0 ? <div className="empty">No games in this window.</div> : (
        <div className="table-scroll">
          <table className="data">
            <thead>
              <tr>
                <th>{champIcons ? 'Champion' : 'Role'}</th><th className="num">Games</th><th className="num">WR</th>
                <th className="num">KDA</th>
                {!compact && <><th className="num">KP</th><th className="num">CS/m</th><th className="num">G@10</th></>}
                <th className="num">Deaths</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(r => (
                <Fragment key={r.key}>
                  <tr onClick={() => r.detail && setOpen(open === r.key ? null : r.key)}
                    style={r.detail ? { cursor: 'pointer' } : undefined}>
                    <td>{champIcons
                      ? <ChampBadge name={r.key} small />
                      : <span className="champ sm"><RoleIcon role={r.key} /> <span className="champ-name">{r.key}</span></span>}</td>
                    <td className="num">{r.games}</td>
                    <td className="num">
                      <span className="meter" aria-hidden="true"><span style={{ width: `${Math.round(r.winRate * 100)}%` }} /></span>
                      {pct(r.winRate)}
                    </td>
                    <td className="num">{r.kda}</td>
                    {!compact && <>
                      <td className="num">{pct(r.kp)}</td>
                      <td className="num">{r.csPerMin}</td>
                      <td className="num">{signed(r.laneGoldAt10)}</td>
                    </>}
                    <td className="num">{r.deathsPerGame}</td>
                  </tr>
                  {open === r.key && r.detail && (
                    <tr className="drill">
                      <td colSpan={8}>
                        <div className="drill-chips obj-chips">
                          <span className="obj-chip">{r.detail.avgKills} / {r.detail.avgDeaths} / {r.detail.avgAssists} <span className="mut">avg score</span></span>
                          <span className="obj-chip">{r.detail.csAt10} <span className="mut">CS@10</span></span>
                          <span className="obj-chip">{r.detail.soloKillsPerGame} <span className="mut">solo kills/g</span></span>
                          <span className="obj-chip">{r.detail.visionPerMin} <span className="mut">vision/m</span></span>
                          <span className="obj-chip">{r.detail.skillshotsDodgedPerGame} <span className="mut">dodges/g</span></span>
                          <span className="obj-chip">{Math.round(r.dpm)} <span className="mut">DPM</span></span>
                        </div>
                        {r.detail.matchups.length > 0 ? (
                          <table className="data" style={{ marginTop: 8 }}>
                            <thead>
                              <tr><th>Lane matchup (2+ games)</th><th className="num">Games</th><th className="num">WR</th><th className="num">G@10</th><th className="num">KDA</th></tr>
                            </thead>
                            <tbody>
                              {r.detail.matchups.map(mu => (
                                <tr key={mu.opponent}>
                                  <td><ChampBadge name={mu.opponent} small /></td>
                                  <td className="num">{mu.games}</td>
                                  <td className={`num ${mu.winRate >= 0.5 ? 'win' : 'loss'}`}>{pct(mu.winRate)}</td>
                                  <td className="num">{signed(mu.laneGoldAt10)}</td>
                                  <td className="num">{mu.kda}</td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        ) : <p className="mut" style={{ margin: '8px 0 0' }}>No repeated lane matchups in this window.</p>}
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

  useEffect(() => {
    api.status().then(setStatus).catch(console.error)
    api.lpPerGame().then(setLpGames).catch(console.error)
    api.analytics(20).then(setDeaths).catch(console.error)
  }, [])

  useEffect(() => {
    const w = WINDOWS.find(x => x.key === windowKey)!
    api.stats({ days: 'days' in w ? w.days : undefined, lastGames: 'lastGames' in w ? w.lastGames : undefined })
      .then(setStats).catch(console.error)
  }, [windowKey])

  useEffect(() => {
    api.lpHistory(queue).then(setLpPoints).catch(console.error)
  }, [queue])

  const queueGames = useMemo(
    () => lpGames.filter(g => g.queueName.includes(queue === 'Flex' ? 'Flex' : 'Solo')),
    [lpGames, queue],
  )

  const o = stats?.overall
  const s = stats?.scope
  const soloDelta = stats?.lpDeltas.find(d => d.queue === 'Solo/Duo')

  return (
    <>
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
          <div className="grid tiles" style={{ marginBottom: 16 }}>
            <div className="card tile">
              <div className="label">Record</div>
              <div className="value">{s.wins}-{s.losses}</div>
              <div className="sub">
                {pct(s.winRate)} · {s.champions} champs
                {soloDelta && (soloDelta.last30 !== null || soloDelta.last7 !== null) &&
                  ` · LP 30d ${signed(soloDelta.last30)} / 7d ${signed(soloDelta.last7)}`}
              </div>
            </div>
            <div className="card tile">
              <div className="label">KDA</div>
              <div className="value">{o.kda}</div>
              <div className="sub">KP {pct(o.kp)}</div>
            </div>
            <div className="card tile">
              <div className="label">DPM / GPM</div>
              <div className="value">{o.dpm}</div>
              <div className="sub">{o.gpm} gpm · early {o.dpmEarly} · mid {o.dpmMid} · late {o.dpmLate}</div>
            </div>
            <div className="card tile">
              <div className="label">CS@10</div>
              <div className="value">{o.csAt10}</div>
              <div className="sub">{o.csPerMin} cs/min</div>
            </div>
            <div className="card tile">
              <div className="label">Lane gold@10</div>
              <div className="value">{signed(o.laneGoldAt10)}</div>
              <div className="sub">cs diff {signed(o.laneCsAt10)}</div>
            </div>
            <div className="card tile">
              <div className="label">Vision/min</div>
              <div className="value">{o.visionPerMin}</div>
              <div className="sub">{o.controlWardsPerGame} control/game</div>
            </div>
            <div className="card tile">
              <div className="label">Deaths/game</div>
              <div className="value">{o.deathsPerGame}</div>
              <div className="sub">pre10 {o.deathsPre10} · 20+ {o.deathsPost20}</div>
            </div>
            <div className="card tile">
              <div className="label">Solo kills/game</div>
              <div className="value">{o.soloKillsPerGame}</div>
              <div className="sub">multikills: {o.triples}×3 {o.quadras}×4 {o.pentas}×5</div>
            </div>
            <div className="card tile">
              <div className="label">Skillshots dodged / hit</div>
              <div className="value">{o.skillshotsDodgedPerGame} / {o.skillshotsHitPerGame}</div>
              <div className="sub">per game · dmg taken {o.damageTakenPerMin}/min</div>
            </div>
          </div>

          {stats.observations.length > 0 && (
            <div className="card" style={{ marginBottom: 16 }}>
              <h2>Key observations</h2>
              <ul style={{ margin: '4px 0', paddingLeft: 18 }}>
                {stats.observations.map(obs => <li key={obs} style={{ margin: '4px 0' }}>{obs}</li>)}
              </ul>
            </div>
          )}

          <div className="card" style={{ marginBottom: 16 }}>
            <h2>Strengths &amp; weaknesses <span className="mut" style={{ fontWeight: 400 }}>— what separates your wins from losses</span></h2>
            <ProfileCard profile={stats.profile} />
          </div>

          {stats.followIn.totalDeaths > 0 && (
            <details className="card" style={{ marginBottom: 16 }}>
              <summary style={{ cursor: 'pointer', fontWeight: 650 }}>
                Death context — following teammates in ({stats.followIn.followIns} of {stats.followIn.totalDeaths} deaths, {pct(stats.followIn.rate)})
              </summary>
              <div className="grid tiles" style={{ marginTop: 12 }}>
                <div className="tile">
                  <div className="label">Follow-in deaths</div>
                  <div className="value">{stats.followIn.followIns}</div>
                  <div className="sub">{pct(stats.followIn.rate)} of all deaths</div>
                </div>
                <div className="tile">
                  <div className="label">Got nothing back</div>
                  <div className="value">{stats.followIn.pureLoss}</div>
                  <div className="sub">no enemy fell from the trigger to 10s after</div>
                </div>
                <div className="tile">
                  <div className="label">2+ allies already down</div>
                  <div className="value">{stats.followIn.twoPlusDown}</div>
                  <div className="sub">walked into an already-lost fight</div>
                </div>
                <div className="tile">
                  <div className="label">Team gold state</div>
                  <div className="value">{stats.followIn.goldState.behind}↓ {stats.followIn.goldState.even}= {stats.followIn.goldState.ahead}↑</div>
                  <div className="sub">behind / even / ahead (±1500)</div>
                </div>
                <div className="tile">
                  <div className="label">Followed in after</div>
                  <div className="value" style={{ fontSize: 16 }}>
                    {stats.followIn.byRole.slice(0, 3).map(r => `${r.key} ${r.count}`).join(' · ') || '—'}
                  </div>
                  <div className="sub">teammate role</div>
                </div>
              </div>
            </details>
          )}

          <div className="grid two-col" style={{ marginBottom: 16 }}>
            <div className="card">
              <h2>Rolling win rate (last 10)</h2>
              <RollingWinRateChart series={stats.series} />
            </div>
            <div className="card">
              <h2>Lane gold@10 per game</h2>
              <LaneGoldChart series={stats.series} />
            </div>
          </div>

          <div className="grid two-col" style={{ marginBottom: 16 }}>
            <div className="card">
              <h2>Lane state <span className="mut" style={{ fontWeight: 400 }}>— gold vs your laner, ±500</span></h2>
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
                <table className="data">
                  <thead><tr><th>Zone</th><th className="num">Deaths</th><th className="num">Share</th></tr></thead>
                  <tbody>
                    {stats.deathZones.map(z => (
                      <tr key={z.key}><td>{z.key}</td><td className="num">{z.count}</td><td className="num">{pct(z.share)}</td></tr>
                    ))}
                  </tbody>
                </table>
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
          <h2>LP per game — {queue}</h2>
          <LpPerGameBars games={queueGames} />
        </div>
      </div>
    </>
  )
}

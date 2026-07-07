import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { AnalyticsSummary, LpPerGame, LpPoint, SplitRow, Stats, Status } from '../types'
import LpLineChart from '../components/LpLineChart'
import LpPerGameBars from '../components/LpPerGameBars'
import { LaneGoldChart, RollingWinRateChart } from '../components/TrendCharts'
import ChampBadge from '../components/ChampBadge'

const QUEUES = ['Solo/Duo', 'Flex'] as const

const WINDOWS = [
  { key: '7d', label: 'Last 7d', days: 7 },
  { key: '15d', label: '15d', days: 15 },
  { key: '30d', label: '30d', days: 30 },
  { key: '60d', label: '60d', days: 60 },
  { key: '10g', label: 'Last 10', lastGames: 10 },
  { key: '20g', label: '20', lastGames: 20 },
  { key: '30g', label: '30', lastGames: 30 },
  { key: 'all', label: 'All' },
] as const

const signed = (v: number | null | undefined) => (v === null || v === undefined ? '—' : `${v > 0 ? '+' : ''}${v}`)
const pct = (v: number) => `${Math.round(v * 100)}%`

function SplitTable({ title, rows, champIcons }: { title: string; rows: SplitRow[]; champIcons?: boolean }) {
  return (
    <div className="card">
      <h2>{title}</h2>
      {rows.length === 0 ? <div className="empty">No games in this window.</div> : (
        <div className="table-scroll">
          <table className="data">
            <thead>
              <tr>
                <th>{champIcons ? 'Champion' : 'Role'}</th><th className="num">Games</th><th className="num">WR</th>
                <th className="num">KDA</th><th className="num">KP</th><th className="num">CS/m</th>
                <th className="num">G@10</th><th className="num">Deaths</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(r => (
                <tr key={r.key}>
                  <td>{champIcons ? <ChampBadge name={r.key} small /> : r.key}</td>
                  <td className="num">{r.games}</td>
                  <td className="num">
                    <span className="meter" aria-hidden="true"><span style={{ width: `${Math.round(r.winRate * 100)}%` }} /></span>
                    {pct(r.winRate)}
                  </td>
                  <td className="num">{r.kda}</td>
                  <td className="num">{pct(r.kp)}</td>
                  <td className="num">{r.csPerMin}</td>
                  <td className="num">{signed(r.laneGoldAt10)}</td>
                  <td className="num">{r.deathsPerGame}</td>
                </tr>
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
          <div className="grid tiles" style={{ marginBottom: 14 }}>
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
            <div className="card" style={{ marginBottom: 14 }}>
              <h2>Key observations</h2>
              <ul style={{ margin: '4px 0', paddingLeft: 18 }}>
                {stats.observations.map(obs => <li key={obs} style={{ margin: '4px 0' }}>{obs}</li>)}
              </ul>
            </div>
          )}

          {stats.followIn.totalDeaths > 0 && (
            <details className="card" style={{ marginBottom: 14 }}>
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

          <div className="grid two-col" style={{ marginBottom: 14 }}>
            <div className="card">
              <h2>Rolling win rate (last 10)</h2>
              <RollingWinRateChart series={stats.series} />
            </div>
            <div className="card">
              <h2>Lane gold@10 per game</h2>
              <LaneGoldChart series={stats.series} />
            </div>
          </div>

          <div className="grid two-col" style={{ marginBottom: 14 }}>
            <div className="card">
              <h2>Win rate by lane state @10</h2>
              <table className="data">
                <thead><tr><th>State</th><th className="num">Games</th><th className="num">Win rate</th></tr></thead>
                <tbody>
                  <tr><td className="win">Ahead (≥ +500)</td><td className="num">{stats.winrateByLaneState.ahead.games}</td><td className="num">{pct(stats.winrateByLaneState.ahead.winRate)}</td></tr>
                  <tr><td>Even (±500)</td><td className="num">{stats.winrateByLaneState.even.games}</td><td className="num">{pct(stats.winrateByLaneState.even.winRate)}</td></tr>
                  <tr><td className="loss">Behind (≤ −500)</td><td className="num">{stats.winrateByLaneState.behind.games}</td><td className="num">{pct(stats.winrateByLaneState.behind.winRate)}</td></tr>
                </tbody>
              </table>
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

          <div className="grid two-col" style={{ marginBottom: 14 }}>
            <SplitTable title="Champion performance" rows={stats.byChampion} champIcons />
            <SplitTable title="Role performance" rows={stats.byRole} />
          </div>
        </>
      )}

      {deaths && deaths.games > 0 && (
        <div className="card" style={{ marginBottom: 14 }}>
          <h2>Collapse profile — last {deaths.games} ranked games ({deaths.totalDeaths} deaths)</h2>
          <div className="grid tiles">
            <div className="tile">
              <div className="label">Collapse deaths (3+ enemies actually there)</div>
              <div className="value">{deaths.collapseDeaths}</div>
              <div className="sub">avg {deaths.avgEnemiesNearDeath ?? '—'} enemies near each death</div>
            </div>
            <div className="tile">
              <div className="label">Died with no ally in range</div>
              <div className="value">{deaths.isolatedDeaths}</div>
              <div className="sub">nearest ally avg {deaths.avgNearestAllyDistAtDeath ?? '—'} units away</div>
            </div>
            <div className="tile">
              <div className="label">Within 90s of taking an objective</div>
              <div className="value">{deaths.postObjectiveDeaths}</div>
              <div className="sub">overstayed to force more</div>
            </div>
            <div className="tile">
              <div className="label">Burst vs whittled</div>
              <div className="value">{deaths.burstDeaths} / {deaths.totalDeaths - deaths.burstDeaths}</div>
              <div className="sub">one source ≥70% of the damage vs ground down</div>
            </div>
            <div className="tile">
              <div className="label">Time in enemy half</div>
              <div className="value">{deaths.avgTimeInEnemyHalfPct.toFixed(0)}%</div>
              <div className="sub">avg nearest ally all game: {deaths.avgNearestAllyDistOverall.toFixed(0)} units</div>
            </div>
          </div>
          <p className="mut" style={{ marginBottom: 0 }}>
            Positions between the 60s timeline frames are interpolated - treat near-counts as estimates, not gospel.
          </p>
        </div>
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

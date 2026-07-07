import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { AnalyticsSummary, LpPerGame, LpPoint, MatchSummary, Status } from '../types'
import LpLineChart from '../components/LpLineChart'
import LpPerGameBars from '../components/LpPerGameBars'

const QUEUES = ['Solo/Duo', 'Flex'] as const

export default function Dashboard() {
  const [status, setStatus] = useState<Status | null>(null)
  const [queue, setQueue] = useState<(typeof QUEUES)[number]>('Solo/Duo')
  const [lpPoints, setLpPoints] = useState<LpPoint[]>([])
  const [lpGames, setLpGames] = useState<LpPerGame[]>([])
  const [recent, setRecent] = useState<MatchSummary[]>([])
  const [deaths, setDeaths] = useState<AnalyticsSummary | null>(null)

  useEffect(() => {
    api.status().then(setStatus).catch(console.error)
    api.lpPerGame().then(setLpGames).catch(console.error)
    api.matches(1, 20, true).then(p => setRecent(p.items)).catch(console.error)
    api.analytics(20).then(setDeaths).catch(console.error)
  }, [])

  useEffect(() => {
    api.lpHistory(queue).then(setLpPoints).catch(console.error)
  }, [queue])

  const queueGames = useMemo(
    () => lpGames.filter(g => g.queueName.includes(queue === 'Flex' ? 'Flex' : 'Solo')),
    [lpGames, queue],
  )
  const recentWins = recent.filter(m => m.win).length

  return (
    <>
      <div className="grid tiles" style={{ marginBottom: 14 }}>
        {status?.ranks.map(r => (
          <div className="card tile" key={r.queue}>
            <div className="label">{r.queue}</div>
            <div className="value">{r.label}</div>
            <div className="sub">{r.wins}W / {r.losses}L this split</div>
          </div>
        ))}
        <div className="card tile">
          <div className="label">Last {recent.length} ranked</div>
          <div className="value">{recent.length > 0 ? `${Math.round((100 * recentWins) / recent.length)}%` : '—'}</div>
          <div className="sub">{recentWins}W / {recent.length - recentWins}L</div>
        </div>
        <div className="card tile">
          <div className="label">Games tracked</div>
          <div className="value">{status?.matches ?? '—'}</div>
          <div className="sub">{status?.deaths ?? 0} deaths analysed</div>
        </div>
      </div>

      <div className="filters">
        <div className="seg" role="tablist" aria-label="Queue">
          {QUEUES.map(q => (
            <button key={q} className={q === queue ? 'on' : ''} onClick={() => setQueue(q)}>{q}</button>
          ))}
        </div>
        {status && !status.apiKeyConfigured && (
          <span className="mut">No API key configured - live capture is paused. See the Data page.</span>
        )}
      </div>

      <div className="grid two-col">
        <div className="card">
          <h2>LP over time — {queue}</h2>
          <LpLineChart points={lpPoints} />
        </div>
        <div className="card">
          <h2>LP per game — {queue} (last {Math.min(30, queueGames.filter(g => g.lpChange !== null).length)})</h2>
          <LpPerGameBars games={queueGames} />
        </div>
      </div>

      {deaths && deaths.games > 0 && (
        <div className="card" style={{ marginTop: 14 }}>
          <h2>Death profile — last {deaths.games} ranked games ({deaths.totalDeaths} deaths, {deaths.deathsPerGame}/game)</h2>
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
            <div className="tile">
              <div className="label">Skillshots dodged / hit per game</div>
              <div className="value">{deaths.avgSkillshotsDodged} / {deaths.avgSkillshotsHit}</div>
              <div className="sub">Riot's per-game counters</div>
            </div>
          </div>
          <p className="mut" style={{ marginBottom: 0 }}>
            Positions between the 60s timeline frames are interpolated - treat near-counts as estimates, not gospel.
          </p>
        </div>
      )}
    </>
  )
}

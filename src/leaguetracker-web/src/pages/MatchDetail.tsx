import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api'
import type { DeathEvent, MatchDetail as Detail, Participant } from '../types'
import { keystone, summonerSpell } from '../lookups'
import ChampBadge from '../components/ChampBadge'

function TeamTable({ title, players }: { title: string; players: Participant[] }) {
  return (
    <div className="card" style={{ marginBottom: 14 }}>
      <h2>{title}</h2>
      <div className="table-scroll">
        <table className="data">
          <thead>
            <tr>
              <th>Player</th><th>Champion</th><th>Loadout</th><th className="num">K/D/A</th>
              <th className="num">CS</th><th className="num">Gold</th><th className="num">Dmg</th>
              <th className="num">Vision</th><th className="num">SS hit/dodged</th><th>Rank</th><th className="num">Winrate</th>
            </tr>
          </thead>
          <tbody>
            {players.map(p => (
              <tr key={p.participantId} className={p.isMe ? 'me-row' : ''}>
                <td>{p.riotId}{p.isMe && <span className="mut"> (me)</span>}</td>
                <td><ChampBadge name={p.champion} small sub={p.position.toLowerCase()} /></td>
                <td className="mut">{summonerSpell(p.summoner1Id)}/{summonerSpell(p.summoner2Id)} · {keystone(p.keystoneId)}</td>
                <td className="num">{p.kills}/{p.deaths}/{p.assists}</td>
                <td className="num">{p.cs}</td>
                <td className="num">{p.gold.toLocaleString()}</td>
                <td className="num">{p.damageToChampions.toLocaleString()}</td>
                <td className="num">{p.visionScore}</td>
                <td className="num">{p.skillshotsHit !== null ? `${p.skillshotsHit}/${p.skillshotsDodged}` : <span className="mut">—</span>}</td>
                <td>{p.rankLabel ?? <span className="mut">Unranked</span>}</td>
                <td className="num">{p.winratePct !== null ? `${p.winratePct}%` : <span className="mut">—</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function damageSummary(d: DeathEvent): string {
  if (d.damageInstanceCount === null || d.topSourceShare === null) return '—'
  const style = d.topSourceShare >= 0.7 ? 'burst' : 'whittled'
  return `${d.damageInstanceCount} hits, ${Math.round(d.topSourceShare * 100)}% ${d.topSource} (${style})`
}

export default function MatchDetail() {
  const { id } = useParams()
  const [detail, setDetail] = useState<Detail | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (id) api.match(id).then(setDetail).catch(e => setError(String(e)))
  }, [id])

  if (error) return <div className="empty">Failed to load match: {error}</div>
  if (!detail) return <div className="empty">Loading…</div>

  const { summary: m, participants, deaths } = detail
  const allies = participants.filter(p => p.isAlly)
  const enemies = participants.filter(p => !p.isAlly)

  return (
    <>
      <div style={{ marginBottom: 14 }}>
        <Link to="/matches" className="mut" style={{ textDecoration: 'none' }}>← Matches</Link>
      </div>

      <div className="grid tiles" style={{ marginBottom: 14 }}>
        <div className="card tile">
          <div className="label">{new Date(m.date).toLocaleString()} · {m.queueName}</div>
          <div className="value">
            <span className={m.win ? 'win' : 'loss'}>{m.win ? 'Victory' : 'Defeat'}</span> — {m.champion}
          </div>
          <div className="sub">
            {m.kills}/{m.deaths}/{m.assists} · {m.durationMin.toFixed(0)} min · {m.cs} CS
            {m.opponentChampion && ` · vs ${m.opponentChampion}`}
            {m.laneGoldDiff10 !== null && ` · ${m.laneGoldDiff10 > 0 ? '+' : ''}${m.laneGoldDiff10}g @10`}
          </div>
        </div>
        <div className="card tile">
          <div className="label">Team ranks {detail.ranksAtGameTime ? '(at game time)' : '(as of capture)'}</div>
          <div className="value">{m.avgAllyRank ?? '—'} <span className="mut">vs</span> {m.avgEnemyRank ?? '—'}</div>
          <div className="sub">
            known: {m.allyRanksKnown}/5 allies, {m.enemyRanksKnown}/5 enemies
            {m.rankGapLp !== null && ` · gap ${m.rankGapLp > 0 ? '+' : ''}${m.rankGapLp} LP`}
          </div>
        </div>
        {m.lpChange !== null && (
          <div className="card tile">
            <div className="label">LP</div>
            <div className="value">
              <span className={m.lpChange >= 0 ? 'win' : 'loss'}>{m.lpChange >= 0 ? '+' : ''}{m.lpChange}</span>
            </div>
            <div className="sub">{m.lpBefore} → {m.lpAfter}</div>
          </div>
        )}
      </div>

      <TeamTable title={`My team ${allies[0]?.win ? '(won)' : '(lost)'}`} players={allies} />
      <TeamTable title={`Enemy team ${enemies[0]?.win ? '(won)' : '(lost)'}`} players={enemies} />

      <div className="card" style={{ marginBottom: 14 }}>
        <h2>My deaths ({deaths.length})</h2>
        {deaths.length === 0 ? (
          <div className="empty">{m.hasTimeline ? 'Deathless game.' : 'No timeline captured for this game.'}</div>
        ) : (
          <div className="table-scroll">
            <table className="data">
              <thead>
                <tr>
                  <th>Time</th><th>Zone</th><th>Killed by</th><th>Damage taken</th>
                  <th className="num">Enemies near</th><th className="num">Allies near</th>
                  <th>Follow-in</th><th>After objective</th><th className="num">My level</th><th className="num">My gold</th>
                </tr>
              </thead>
              <tbody>
                {deaths.map(d => (
                  <tr key={d.timeSec}>
                    <td className="num">{d.gameTime}</td>
                    <td className="mut">{d.zone || '—'}</td>
                    <td>{d.killedBy}{d.assistedBy && <span className="mut"> +{d.assistedBy}</span>}</td>
                    <td>{damageSummary(d)}</td>
                    <td className="num">{d.enemiesNearDeath !== null
                      ? <span className={d.enemiesNearDeath >= 3 ? 'loss' : ''}>{d.enemiesNearDeath}</span>
                      : <span className="mut">—</span>}</td>
                    <td className="num">{d.alliesNearDeath ?? <span className="mut">—</span>}</td>
                    <td>{d.followTeammate
                      ? <span className={d.followPureLoss ? 'loss' : ''}>after {d.followTeammate} +{d.followSecondsAfter}s{d.followPureLoss ? ' (nothing back)' : ''}</span>
                      : <span className="mut">—</span>}</td>
                    <td>{d.secondsAfterObjective !== null
                      ? <span className="loss">{d.objectiveBefore} +{d.secondsAfterObjective}s</span>
                      : <span className="mut">—</span>}</td>
                    <td className="num">{d.myLevel ?? '—'}</td>
                    <td className="num">{d.myTotalGold?.toLocaleString() ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {deaths.length > 0 && (
          <p className="mut" style={{ marginBottom: 0 }}>
            "Near" = within 2000 units, interpolated between the 60s timeline frames - an estimate. Red enemies-near means
            3+ converged; red objective means this death came right after your team took it.
          </p>
        )}
      </div>

      {detail.objectives.length > 0 && (
        <div className="card">
          <h2>Objective timeline</h2>
          <div className="table-scroll">
            <table className="data">
              <thead>
                <tr><th>Time</th><th>Objective</th><th>Taken by</th><th>Player</th></tr>
              </thead>
              <tbody>
                {detail.objectives.map(o => (
                  <tr key={`${o.timeSec}-${o.kind}`}>
                    <td className="num">{o.gameTime}</td>
                    <td>{o.kind}{o.subKind && <span className="mut"> {o.subKind}</span>}</td>
                    <td className={o.byMyTeam ? 'win' : 'loss'}>{o.byMyTeam ? 'My team' : 'Enemy'}</td>
                    <td>{o.killer ?? <span className="mut">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </>
  )
}

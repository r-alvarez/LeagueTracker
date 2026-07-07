import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api'
import type { DeathEvent, MatchDetail as Detail, Participant, Perks, TeamObjectiveCounts } from '../types'
import { STAT_SHARDS, useChampionIcons, useLoadoutIcons } from '../champions'
import Loadout from '../components/Loadout'

type Tab = 'general' | 'details' | 'runes' | 'timeline'

const signed = (v: number | null) => (v === null ? '—' : `${v > 0 ? '+' : ''}${v}`)
const parsePerks = (json: string): Perks | null => {
  try { return json ? (JSON.parse(json) as Perks) : null } catch { return null }
}

function ChampIcon({ name, size = 34, level }: { name: string; size?: number; level?: number }) {
  const icon = useChampionIcons()(name)
  return (
    <span className="champ-frame" style={{ width: size, height: size }}>
      {icon
        ? <img src={icon} alt={name} title={name} loading="lazy" />
        : <span className="champ-mono" style={{ width: size, height: size }}>{name.slice(0, 2).toUpperCase()}</span>}
      {level !== undefined && <span className="lvl">{level}</span>}
    </span>
  )
}

function RunePair({ perks }: { perks: Perks | null }) {
  const icons = useLoadoutIcons()
  if (!perks || perks.styles.length === 0) return <span className="mut">—</span>
  const keystoneId = perks.styles[0]?.selections[0]?.perk ?? 0
  const subStyleId = perks.styles[1]?.style ?? 0
  const keystone = icons.rune(keystoneId)
  const sub = icons.rune(subStyleId)
  return (
    <span className="rune-pair">
      <span className="slot round" title={keystone?.name}>{keystone && <img src={keystone.icon} alt="" loading="lazy" />}</span>
      <span className="slot round sm" title={sub?.name}>{sub && <img src={sub.icon} alt="" loading="lazy" />}</span>
    </span>
  )
}

function ObjChips({ o }: { o: TeamObjectiveCounts }) {
  const chips: Array<[string, number]> = [
    ['Towers', o.towers], ['Inhibs', o.inhibitors], ['Drakes', o.dragons],
    ['Barons', o.barons], ['Heralds', o.heralds], ['Grubs', o.grubs],
  ]
  if (o.atakhan > 0) chips.push(['Atakhan', o.atakhan])
  return (
    <span className="obj-chips">
      {chips.map(([label, count]) => (
        <span key={label} className={`obj-chip ${count > 0 ? '' : 'zero'}`}>{count} <span className="mut">{label}</span></span>
      ))}
    </span>
  )
}

function Scoreboard({ title, side, won, players, objectives, maxDamage, durationMin }: {
  title: string
  side: string
  won: boolean
  players: Participant[]
  objectives: TeamObjectiveCounts
  maxDamage: number
  durationMin: number
}) {
  const icons = useLoadoutIcons()
  return (
    <div className="card" style={{ marginBottom: 14 }}>
      <div className="sb-head">
        <h2 style={{ margin: 0 }}>
          <span className={won ? 'win' : 'loss'}>{title}</span> <span className="mut">({side} side)</span>
        </h2>
        <ObjChips o={objectives} />
      </div>
      <div className="table-scroll">
        <table className="data sb">
          <tbody>
            {players.map(p => {
              const perks = parsePerks(p.perksJson)
              const dpm = Math.round(p.damageToChampions / durationMin)
              return (
                <tr key={p.participantId} className={p.isMe ? 'me-row' : ''}>
                  <td>
                    <span className="sb-player">
                      <ChampIcon name={p.champion} level={p.champLevel} />
                      <span className="sb-name">
                        <span>{p.riotId}{p.isMe && <span className="mut"> (me)</span>}</span>
                        <span className="mut sm-text">{p.tier ? `${p.tier} ${p.division}` : 'Unranked'}</span>
                      </span>
                    </span>
                  </td>
                  <td><RunePair perks={perks} /></td>
                  <td>
                    <span className="slots">
                      {[p.summoner1Id, p.summoner2Id].map((id, i) => (
                        <span key={i} className="slot">{id > 0 && icons.spell(id) && <img src={icons.spell(id)!} alt="" loading="lazy" />}</span>
                      ))}
                    </span>
                  </td>
                  <td><Loadout items={p.items} summoner1Id={null} summoner2Id={null} /></td>
                  <td className="num">{p.kills} / <span className="loss">{p.deaths}</span> / {p.assists}</td>
                  <td className="num">{p.killParticipation !== null ? `${Math.round(p.killParticipation * 100)}%` : '—'} <span className="mut sm-text">KP</span></td>
                  <td className="num">{(p.cs / durationMin).toFixed(1)} <span className="mut sm-text">CS/m</span></td>
                  <td className="dmg-cell">
                    <span className="dmgbar"><span style={{ width: `${Math.max(3, Math.round((100 * p.damageToChampions) / Math.max(1, maxDamage)))}%` }} /></span>
                    <span className="sm-text">{(p.damageToChampions / 1000).toFixed(1)}K <span className="mut">({dpm}/m)</span></span>
                  </td>
                  <td className="num">{p.visionScore} <span className="mut sm-text">vis</span></td>
                </tr>
              )
            })}
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

function DetailsTab({ detail }: { detail: Detail }) {
  const icons = useLoadoutIcons()
  const me = detail.participants.find(p => p.isMe)
  const m = detail.summary
  const l = detail.laning

  const pings = useMemo(() => {
    try { return me?.pingsJson ? (Object.entries(JSON.parse(me.pingsJson)) as Array<[string, number]>) : [] } catch { return [] }
  }, [me?.pingsJson])

  // Build order grouped by minute; sells and undos stay visible but marked.
  const buildGroups = useMemo(() => {
    const groups: Array<{ min: number; entries: Array<{ id: number; kind: string }> }> = []
    for (const ev of detail.itemEvents) {
      const min = Math.floor(ev.timeSec / 60)
      const last = groups[groups.length - 1]
      const entry = { id: ev.itemId, kind: ev.kind }
      if (last && last.min === min) last.entries.push(entry)
      else groups.push({ min, entries: [entry] })
    }
    return groups
  }, [detail.itemEvents])

  if (!me) return null

  const skillNames = ['Q', 'W', 'E', 'R']
  const spellCasts: Array<[string, number]> = [
    ['Q', me.spell1Casts], ['W', me.spell2Casts], ['E', me.spell3Casts], ['R', me.spell4Casts],
  ]

  return (
    <>
      <div className="grid tiles" style={{ marginBottom: 14 }}>
        <div className="card tile">
          <div className="label">Laning at 15 {l.laneGoldDiff15 === null && '(at 10)'}</div>
          <div className="value">
            {signed(l.laneGoldDiff15 ?? l.laneGoldDiff10)} <span className="mut" style={{ fontSize: 14 }}>gold</span>
          </div>
          <div className="sub">
            cs {signed(l.laneCsDiff15 ?? l.laneCsDiff10)} · xp {signed(l.laneXpDiff15 ?? l.laneXpDiff10)}
            {l.firstToLevel2 !== null && ` · first to lvl 2: ${l.firstToLevel2 ? 'yes' : 'no'}`}
          </div>
        </div>
        <div className="card tile">
          <div className="label">Wards</div>
          <div className="value">{detail.wards.wardsPlaced}</div>
          <div className="sub">{detail.wards.wardsKilled} killed · {detail.wards.controlWards} control</div>
        </div>
        <div className="card tile">
          <div className="label">Global stats</div>
          <div className="value">{(m.cs / m.durationMin).toFixed(1)} <span className="mut" style={{ fontSize: 14 }}>CS/m</span></div>
          <div className="sub">
            {(m.visionScore / m.durationMin).toFixed(2)} VS/m · {Math.round(m.damageToChampions / m.durationMin)} DMG/m · {Math.round(m.gold / m.durationMin)} gold/m
          </div>
        </div>
      </div>

      <div className="card" style={{ marginBottom: 14 }}>
        <h2>Build order</h2>
        {buildGroups.length === 0 ? <div className="empty">No timeline for this game.</div> : (
          <div className="build-order">
            {buildGroups.map((g, gi) => (
              <span key={gi} className="build-group">
                <span className="slots">
                  {g.entries.map((e, i) => (
                    <span key={i} className={`slot ${e.kind === 'SOLD' ? 'sold' : ''} ${e.kind === 'UNDO' ? 'undo' : ''}`}
                      title={`${e.kind.toLowerCase()} · minute ${g.min}`}>
                      {icons.item(e.id) && <img src={icons.item(e.id)!} alt="" loading="lazy" />}
                    </span>
                  ))}
                </span>
                <span className="mut sm-text">{g.min} min</span>
              </span>
            ))}
          </div>
        )}
      </div>

      <div className="grid two-col" style={{ marginBottom: 14 }}>
        <div className="card">
          <h2>Skill order</h2>
          {detail.skillOrder.length === 0 ? <div className="empty">No timeline for this game.</div> : (
            <div className="skill-grid">
              {skillNames.map((name, slotIdx) => (
                <div key={name} className="skill-row">
                  <span className={`skill-key ${name === 'R' ? 'ult' : ''}`}>{name}</span>
                  {detail.skillOrder.map((slot, i) => (
                    <span key={i} className={`skill-cell ${slot === slotIdx + 1 ? (name === 'R' ? 'taken ult' : 'taken') : ''}`}>
                      {slot === slotIdx + 1 ? i + 1 : ''}
                    </span>
                  ))}
                </div>
              ))}
            </div>
          )}
        </div>
        <div className="card">
          <h2>Casts & pings</h2>
          <div className="cast-row">
            {spellCasts.map(([k, v]) => (
              <span key={k} className="cast"><span className="skill-key">{k}</span><strong>{v}</strong><span className="mut sm-text">casts</span></span>
            ))}
            {[[me.summoner1Id, me.summoner1Casts], [me.summoner2Id, me.summoner2Casts]].map(([id, casts], i) => (
              <span key={`s${i}`} className="cast">
                <span className="slot">{id > 0 && icons.spell(id as number) && <img src={icons.spell(id as number)!} alt="" />}</span>
                <strong>{casts}</strong><span className="mut sm-text">casts</span>
              </span>
            ))}
          </div>
          <div style={{ marginTop: 12 }}>
            {pings.length === 0 ? <span className="mut">No pings recorded.</span> : (
              <span className="obj-chips">
                {pings.sort((a, b) => b[1] - a[1]).map(([k, v]) => (
                  <span key={k} className="obj-chip">{v} <span className="mut">{k}</span></span>
                ))}
              </span>
            )}
          </div>
        </div>
      </div>
    </>
  )
}

function RunePage({ p }: { p: Participant }) {
  const icons = useLoadoutIcons()
  const perks = parsePerks(p.perksJson)
  const primary = perks?.styles[0]
  const secondary = perks?.styles[1]
  const shards = perks ? [perks.statPerks.offense, perks.statPerks.flex, perks.statPerks.defense] : []
  const runeImg = (id: number, cls = '') => {
    const r = icons.rune(id)
    return (
      <span key={id + cls} className={`slot round ${cls}`} title={r?.name}>
        {r && <img src={r.icon} alt="" loading="lazy" />}
      </span>
    )
  }
  return (
    <div className="rune-page">
      <div className="rune-champ"><ChampIcon name={p.champion} size={42} /><span className="sm-text mut">{p.isMe ? 'me' : p.riotId.split('#')[0]}</span></div>
      {perks ? (
        <>
          <div className="slots">{primary?.selections.map((s, i) => runeImg(s.perk, i === 0 ? 'keystone' : ''))}</div>
          <div className="slots">
            {secondary && runeImg(secondary.style, 'sm')}
            {secondary?.selections.map(s => runeImg(s.perk))}
          </div>
          <div className="obj-chips">
            {shards.map((id, i) => <span key={i} className="obj-chip">{STAT_SHARDS[id] ?? `#${id}`}</span>)}
          </div>
        </>
      ) : <span className="mut">No rune data.</span>}
    </div>
  )
}

export default function MatchDetail() {
  const { id } = useParams()
  const [detail, setDetail] = useState<Detail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('general')

  useEffect(() => {
    if (id) api.match(id).then(setDetail).catch(e => setError(String(e)))
  }, [id])

  if (error) return <div className="empty">Failed to load match: {error}</div>
  if (!detail) return <div className="empty">Loading…</div>

  const { summary: m, participants, deaths } = detail
  const allies = participants.filter(p => p.isAlly)
  const enemies = participants.filter(p => !p.isAlly)
  const maxDamage = Math.max(...participants.map(p => p.damageToChampions))
  const enemySide = detail.mySide === 'Blue' ? 'Red' : 'Blue'

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

      <div className="filters">
        <div className="seg" role="tablist" aria-label="Match view">
          {(['general', 'details', 'runes', 'timeline'] as Tab[]).map(t => (
            <button key={t} className={t === tab ? 'on' : ''} onClick={() => setTab(t)}>
              {t === 'general' ? 'General' : t === 'details' ? 'Details' : t === 'runes' ? 'Runes' : 'Deaths & objectives'}
            </button>
          ))}
        </div>
      </div>

      {tab === 'general' && (
        <>
          <Scoreboard title={allies[0]?.win ? 'Victory' : 'Defeat'} side={detail.mySide} won={allies[0]?.win ?? false}
            players={allies} objectives={detail.teamObjectives.ally} maxDamage={maxDamage} durationMin={m.durationMin} />
          <Scoreboard title={enemies[0]?.win ? 'Victory' : 'Defeat'} side={enemySide} won={enemies[0]?.win ?? false}
            players={enemies} objectives={detail.teamObjectives.enemy} maxDamage={maxDamage} durationMin={m.durationMin} />
        </>
      )}

      {tab === 'details' && <DetailsTab detail={detail} />}

      {tab === 'runes' && (
        <div className="rune-grid">
          {[...allies, ...enemies].map(p => <RunePage key={p.participantId} p={p} />)}
        </div>
      )}

      {tab === 'timeline' && (
        <>
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
      )}
    </>
  )
}

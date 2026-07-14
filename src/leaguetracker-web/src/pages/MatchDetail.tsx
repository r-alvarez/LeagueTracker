import { Fragment, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api'
import type { ClipInfo, DeathEvent, FullGameStatus, MatchDetail as Detail, Participant, Perks, TeamObjectiveCounts } from '../types'
import { sourceLabel, unitKind, useAbilityLabels, useChampionIcons, useLoadoutIcons } from '../champions'
import Loadout from '../components/Loadout'
import { ItemIcon, PerkIcon, UnitGlyph } from '../components/GameIcons'
import { RelTime, tierClass } from '../components/Stats'

type Tab = 'general' | 'details' | 'runes' | 'timeline'

// dpm.lol-style carry score: each player's stats normalized against the game's
// best, weighted toward damage and KDA. Purely relative within this one match.
function carryScores(players: Participant[]): Record<number, { score: number; ord: number }> {
  const max = (f: (p: Participant) => number) => Math.max(1, ...players.map(f))
  const kda = (p: Participant) => (p.kills + p.assists) / Math.max(1, p.deaths)
  const maxes = { kda: max(kda), dmg: max(p => p.damageToChampions), kp: max(p => p.killParticipation ?? 0), vis: max(p => p.visionScore), cs: max(p => p.cs), gold: max(p => p.gold) }
  const raw = players.map(p => ({
    id: p.participantId,
    score: 100 * (
      0.28 * (p.damageToChampions / maxes.dmg) + 0.24 * (kda(p) / maxes.kda) + 0.18 * ((p.killParticipation ?? 0) / maxes.kp) +
      0.10 * (p.visionScore / maxes.vis) + 0.10 * (p.cs / maxes.cs) + 0.10 * (p.gold / maxes.gold)),
  }))
  const sorted = [...raw].sort((a, b) => b.score - a.score)
  return Object.fromEntries(raw.map(r => [r.id, { score: Math.round(r.score), ord: sorted.findIndex(s => s.id === r.id) + 1 }]))
}

function ScoreRing({ score, ord, won }: { score: number; ord: number; won: boolean }) {
  const r = 12
  const c = 2 * Math.PI * r
  const label = ord === 1 && won ? 'MVP' : ord === 1 ? 'ACE' : `${ord}${ord === 2 ? 'nd' : ord === 3 ? 'rd' : 'th'}`
  return (
    <span className="score-ring" title={`Carry score ${score}/100 (relative to this game)`}>
      <svg width="30" height="30" viewBox="0 0 30 30">
        <circle cx="15" cy="15" r={r} fill="none" stroke="var(--panel-2)" strokeWidth="3" />
        <circle cx="15" cy="15" r={r} fill="none" stroke={ord === 1 ? 'var(--warn)' : 'var(--series-1)'} strokeWidth="3"
          strokeDasharray={`${(c * score) / 100} ${c}`} strokeLinecap="round" transform="rotate(-90 15 15)" />
        <text x="15" y="19" textAnchor="middle" fontSize="10" fontWeight="700" fill="var(--ink)">{score}</text>
      </svg>
      <span className={`ord ${ord === 1 ? 'mvp' : ''}`}>{label}</span>
    </span>
  )
}

const signed = (v: number | null) => (v === null ? '—' : `${v > 0 ? '+' : ''}${v}`)
const parsePerks = (json: string): Perks | null => {
  try { return json ? (JSON.parse(json) as Perks) : null } catch { return null }
}

function VisionIcon() {
  return (
    <svg className="vis-icon" viewBox="0 0 24 24" width="14" height="14" aria-label="Vision score" role="img">
      <title>Vision score</title>
      <path
        d="M12 5C6.5 5 2.6 9.4 1.3 11.4a1.1 1.1 0 0 0 0 1.2C2.6 14.6 6.5 19 12 19s9.4-4.4 10.7-6.4a1.1 1.1 0 0 0 0-1.2C21.4 9.4 17.5 5 12 5Zm0 11a4 4 0 1 1 0-8 4 4 0 0 1 0 8Zm0-2.2a1.8 1.8 0 1 0 0-3.6 1.8 1.8 0 0 0 0 3.6Z"
        fill="currentColor"
      />
    </svg>
  )
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
  if (!perks || perks.styles.length === 0) return <span className="mut">—</span>
  return (
    <span className="rune-pair">
      <PerkIcon id={perks.styles[0]?.selections[0]?.perk ?? 0} />
      <PerkIcon id={perks.styles[1]?.style ?? 0} className="sm" />
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

function Scoreboard({ title, side, won, players, objectives, maxDamage, durationMin, scores }: {
  title: string
  side: string
  won: boolean
  players: Participant[]
  objectives: TeamObjectiveCounts
  maxDamage: number
  durationMin: number
  scores: Record<number, { score: number; ord: number }>
}) {
  const icons = useLoadoutIcons()
  // No rank line at all when nobody has one (ranks hidden or all unranked) -
  // a column of "Unranked" tags would just be noise.
  const anyRanked = players.some(p => p.tier)
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
                  <td>{scores[p.participantId] && <ScoreRing score={scores[p.participantId].score} ord={scores[p.participantId].ord} won={won} />}</td>
                  <td>
                    <span className="sb-player">
                      <ChampIcon name={p.champion} level={p.champLevel} />
                      <span className="sb-name">
                        <span>{p.riotId}{p.isMe && <span className="mut"> (me)</span>}</span>
                        {anyRanked && <span className={`sm-text ${p.tier ? tierClass(p.tier) : 'mut'}`}>{p.tier ? `${p.tier} ${p.division}` : 'Unranked'}</span>}
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
                  <td className="num">{p.visionScore} <VisionIcon /></td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// Lane-context classification from exact kill data: a solo kill is the lane
// opponent alone (no assists, nobody else actually near); a gank is the enemy
// jungler taking part in the kill during the laning/mid phase.
function deathTag(d: DeathEvent, opponent: string | null, jungler: string | null): 'solo' | 'gank' | null {
  if (jungler && d.timeSec <= 1200 && (d.killedBy === jungler || d.damageFrom.split(', ').includes(jungler))) return 'gank'
  if (opponent && d.killedBy === opponent && !d.assistedBy && (d.enemiesNearDeath ?? 1) <= 1) return 'solo'
  return null
}

function damageSummary(d: DeathEvent): string {
  if (d.damageInstanceCount === null || d.topSourceShare === null) return '—'
  const style = d.topSourceShare >= 0.7 ? 'burst' : 'whittled'
  return `${d.damageInstanceCount} hits, ${Math.round(d.topSourceShare * 100)}% ${d.topSource} (${style})`
}

/// In-game-style death recap: the final ~10s of damage grouped by source
/// champion, split physical / magic / true.
function DeathRecap({ d }: { d: DeathEvent }) {
  const sourceNames = useMemo(() => [...new Set(d.damageInstances.map(i => i.source))], [d])
  const abilityLabel = useAbilityLabels(sourceNames)
  const bySource = new Map<string, { phys: number; magic: number; tru: number; spells: Map<string, number> }>()
  for (const i of d.damageInstances) {
    const s = bySource.get(i.source) ?? { phys: 0, magic: 0, tru: 0, spells: new Map<string, number>() }
    s.phys += i.physical
    s.magic += i.magic
    s.tru += i.trueDamage
    s.spells.set(i.spellName, (s.spells.get(i.spellName) ?? 0) + i.total)
    bySource.set(i.source, s)
  }
  const sources = [...bySource.entries()]
    .map(([source, s]) => ({ source, total: s.phys + s.magic + s.tru, ...s }))
    .sort((a, b) => b.total - a.total)
  const grandTotal = Math.max(1, sources.reduce((acc, s) => acc + s.total, 0))

  return (
    <div className="recap">
      <div className="sub-h" style={{ marginTop: 0 }}>
        Death recap — {grandTotal.toLocaleString()} damage · {d.damageInstances.length} hits in the final ~10s
        <span className="recap-legend">
          <span className="dmg-phys">■ physical</span> <span className="dmg-magic">■ magic</span> <span className="dmg-true">■ true</span>
        </span>
      </div>
      {sources.map(s => (
        <div key={s.source} className="recap-row">
          {unitKind(s.source) !== null
            ? <UnitGlyph kind={unitKind(s.source)!} />
            : <ChampIcon name={sourceLabel(s.source)} size={28} />}
          <span className="recap-name">
            <span>{sourceLabel(s.source)} <strong>{s.total.toLocaleString()}</strong> <span className="mut sm-text">({Math.round((100 * s.total) / grandTotal)}%)</span></span>
            <span className="recap-bar">
              {s.phys > 0 && <span className="seg phys" style={{ width: `${(100 * s.phys) / grandTotal}%` }} />}
              {s.magic > 0 && <span className="seg magic" style={{ width: `${(100 * s.magic) / grandTotal}%` }} />}
              {s.tru > 0 && <span className="seg tru" style={{ width: `${(100 * s.tru) / grandTotal}%` }} />}
            </span>
          </span>
          <span className="recap-spells">
            {[...s.spells.entries()].sort((a, b) => b[1] - a[1]).map(([spell, dmg]) => (
              <span key={spell} className="obj-chip">{abilityLabel(s.source, spell)} <strong>{dmg.toLocaleString()}</strong></span>
            ))}
          </span>
        </div>
      ))}
    </div>
  )
}

const DRAGON_NAMES: Record<string, string> = {
  FIRE: 'Infernal', EARTH: 'Mountain', AIR: 'Cloud', WATER: 'Ocean',
  HEXTECH: 'Hextech', CHEMTECH: 'Chemtech', ELDER: 'Elder',
}

const objectiveLabel = (kind: string, subKind: string): string => {
  if (kind === 'DRAGON') return `${DRAGON_NAMES[subKind.toUpperCase()] ?? subKind} Dragon`
  if (kind === 'TOWER') return `${subKind ? subKind[0] + subKind.slice(1).toLowerCase() + ' ' : ''}Tower`
  if (kind === 'HERALD') return 'Rift Herald'
  if (kind === 'GRUBS') return 'Void Grub'
  if (kind === 'BARON') return 'Baron Nashor'
  if (kind === 'ATAKHAN') return 'Atakhan'
  return kind === 'INHIBITOR' ? 'Inhibitor' : kind
}

const OBJ_KIND_CLASS: Record<string, string> = {
  DRAGON: 'obj-dragon', BARON: 'obj-baron', HERALD: 'obj-herald',
  GRUBS: 'obj-grubs', TOWER: 'obj-tower', INHIBITOR: 'obj-tower', ATAKHAN: 'obj-baron',
}

const mmss = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
const leadFmt = (s: number | null) => (s === null ? '—' : `${s > 0 ? '+' : ''}${s}s`)
const leadClass = (s: number | null) => (s === null ? 'mut' : s >= 0 ? 'win' : 'loss')

function DetailsTab({ detail }: { detail: Detail }) {
  const icons = useLoadoutIcons()
  const me = detail.participants.find(p => p.isMe)
  const m = detail.summary
  const l = detail.laning
  const macro = detail.macro

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
      <div className="grid two-col" style={{ marginBottom: 14 }}>
        <div className="card">
          <h2 style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <ChampIcon name={m.champion} size={24} />
            <span className="mut" style={{ fontWeight: 400 }}>vs</span>
            {m.opponentChampion ? <ChampIcon name={m.opponentChampion} size={24} /> : null}
            <span>Laning{m.opponentChampion ? ` vs ${m.opponentChampion}` : ''}</span>
            {l.firstToLevel2 !== null && (
              <span className={`obj-chip ${l.firstToLevel2 ? 'win' : 'loss'}`} style={{ fontWeight: 600 }}>
                {l.firstToLevel2 ? 'first to level 2' : 'second to level 2'}
              </span>
            )}
          </h2>
          {l.checkpoints && l.checkpoints.length > 0 ? (
            <>
              <div className="table-scroll">
              <table className="data">
                <thead>
                  <tr>
                    <th>At</th><th className="num">Gold diff</th><th className="num">XP diff</th>
                    <th className="num">CS diff</th><th className="num">Level diff</th>
                    <th className="num">My K/D</th><th className="num">{m.opponentChampion ?? 'Opp'} K/D</th>
                    <th className="num">My CS (lvl)</th><th className="num">{m.opponentChampion ?? 'Opp'} CS (lvl)</th>
                  </tr>
                </thead>
                <tbody>
                  {/* Milestones only (dense early, sparser late) - the every-3-min
                      series feeds the item race below. */}
                  {l.checkpoints.filter(c => [10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90].includes(c.min)).map(c => (
                    <tr key={c.min}>
                      <td className="num">{c.min}:00</td>
                      <td className={`num ${c.gold >= 0 ? 'win' : 'loss'}`}>{signed(c.gold)}</td>
                      <td className={`num ${c.xp >= 0 ? 'win' : 'loss'}`}>{signed(c.xp)}</td>
                      <td className={`num ${c.cs >= 0 ? 'win' : 'loss'}`}>{signed(c.cs)}</td>
                      <td className={`num ${c.level >= 0 ? 'win' : 'loss'}`}>{signed(c.level)}</td>
                      <td className="num">
                        <span className={c.myKills - c.myDeaths > 0 ? 'win' : c.myKills - c.myDeaths < 0 ? 'loss' : ''}>{c.myKills ?? 0}/{c.myDeaths ?? 0}</span>
                      </td>
                      <td className="num mut">{c.oppKills ?? 0}/{c.oppDeaths ?? 0}</td>
                      <td className="num">{c.myCs} <span className="mut sm-text">({c.myLevel})</span></td>
                      <td className="num mut">{c.oppCs} <span className="sm-text">({c.oppLevel})</span></td>
                    </tr>
                  ))}
                </tbody>
              </table>
              </div>
              <div className="sub-h">Item race</div>
              <div className="item-race">
                {l.checkpoints.map(c => (
                  <div key={c.min} className="item-race-row">
                    <span className={`sm-text item-race-min ${c.gold >= 0 ? 'win' : 'loss'}`}>{c.min}:00</span>
                    <span className="slots">
                      {c.myItems.map((id, i) => <ItemIcon key={i} id={id} />)}
                    </span>
                    <span className="mut sm-text">vs</span>
                    <span className="slots">
                      {c.oppItems.map((id, i) => <ItemIcon key={i} id={id} dim />)}
                    </span>
                    <span className={`sm-text ${c.gold >= 0 ? 'win' : 'loss'}`} style={{ marginLeft: 'auto' }}>{signed(c.gold)}g</span>
                  </div>
                ))}
              </div>
              <p className="mut sm-text" style={{ margin: '8px 0 0' }}>Inventories replayed from the item event log - consumables and undos accounted for.</p>
            </>
          ) : (
            <div className="empty">
              {l.laneGoldDiff10 !== null
                ? `@10 only: ${signed(l.laneGoldDiff10)} gold · ${signed(l.laneCsDiff10)} cs · ${signed(l.laneXpDiff10)} xp`
                : 'No lane opponent or timeline for this game.'}
            </div>
          )}
        </div>
        <div className="grid" style={{ alignContent: 'start' }}>
          {(() => {
            const solo = detail.deaths.filter(d => deathTag(d, m.opponentChampion, m.enemyJungler) === 'solo').length
            const gank = detail.deaths.filter(d => deathTag(d, m.opponentChampion, m.enemyJungler) === 'gank').length
            return (solo > 0 || gank > 0) && (
              <div className="card tile">
                <div className="label">Lane deaths</div>
                <div className="value">
                  {solo > 0 && <span className="loss">×{solo} solo</span>}
                  {solo > 0 && gank > 0 && <span className="mut"> · </span>}
                  {gank > 0 && <span>×{gank} ganked</span>}
                </div>
                <div className="sub">tagged per death in Deaths & objectives</div>
              </div>
            )
          })()}
          <div className="card">
            <h2>My numbers</h2>
            <div className="stat-list">
              <div className="stat-row"><span className="k">CS per minute</span><span className="v">{(m.cs / m.durationMin).toFixed(1)}</span></div>
              <div className="stat-row"><span className="k">Damage per minute</span><span className="v">{Math.round(m.damageToChampions / m.durationMin)}</span></div>
              <div className="stat-row"><span className="k">Gold per minute</span><span className="v">{Math.round(m.gold / m.durationMin)}</span></div>
              <div className="stat-row"><span className="k">Vision per minute<small>{m.visionScore} total vision score</small></span><span className="v">{(m.visionScore / m.durationMin).toFixed(2)}</span></div>
              <div className="stat-row"><span className="k">Wards placed<small>{detail.wards.wardsKilled} killed · {detail.wards.controlWards} control</small></span><span className="v">{detail.wards.wardsPlaced}</span></div>
            </div>
          </div>

          <div className="card">
            <h2>Macro, vision &amp; spikes</h2>
            <div className="stat-list">
              <div className="stat-row">
                <span className="k">Gold left unspent<small>avg carried without spending{macro.maxUnspentGold ? ` · peaked at ${macro.maxUnspentGold}` : ''}</small></span>
                <span className={`v ${(macro.avgUnspentGold ?? 0) > 800 ? 'loss' : ''}`}>{macro.avgUnspentGold ?? '—'}</span>
              </div>
              <div className="stat-row">
                <span className="k">First control ward<small>{macro.wardsFirst10} wards placed in first 10 min</small></span>
                <span className="v">{macro.firstControlWardSec !== null ? mmss(macro.firstControlWardSec) : <span className="mut">none</span>}</span>
              </div>
              <div className="stat-row">
                <span className="k">Level 6 lead vs lane<small>+ = you hit your ult first</small></span>
                <span className={`v ${leadClass(macro.level6LeadSec)}`}>{leadFmt(macro.level6LeadSec)}</span>
              </div>
              <div className="stat-row">
                <span className="k">Level 11 / 16 lead</span>
                <span className={`v ${leadClass(macro.level11LeadSec)}`} style={{ fontSize: 15 }}>{leadFmt(macro.level11LeadSec)} <span className="mut">/</span> <span className={leadClass(macro.level16LeadSec)}>{leadFmt(macro.level16LeadSec)}</span></span>
              </div>
              <div className="stat-row">
                <span className="k">Objective presence<small>epic objectives you were near when your team took them</small></span>
                <span className="v">{macro.friendlyEpicObjectives > 0 ? `${macro.objectivesPresentFor}/${macro.friendlyEpicObjectives}` : <span className="mut">none taken</span>}</span>
              </div>
            </div>
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
                    <span key={i} className={e.kind === 'SOLD' ? 'slot-sold-wrap' : undefined}
                      style={e.kind === 'UNDO' ? { opacity: 0.4 } : undefined}>
                      <ItemIcon id={e.id} />
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
          {(me.skillshotsHit !== null || me.skillshotsDodged !== null) && (
            <div className="obj-chips" style={{ marginTop: 12 }}>
              <span className="obj-chip">{me.skillshotsHit ?? 0} <span className="mut">skillshots hit</span></span>
              <span className="obj-chip">{me.skillshotsDodged ?? 0} <span className="mut">dodged</span></span>
              {(() => {
                const casts = me.spell1Casts + me.spell2Casts + me.spell3Casts + me.spell4Casts
                return casts > 0 && me.skillshotsHit !== null && (
                  <span className="obj-chip" title="Riot doesn't expose skillshot attempts, so a true accuracy % isn't computable - this is hits relative to all ability casts.">
                    {Math.round((100 * me.skillshotsHit) / casts)} <span className="mut">hits per 100 casts</span>
                  </span>
                )
              })()}
            </div>
          )}
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
  const perks = parsePerks(p.perksJson)
  const primary = perks?.styles[0]
  const secondary = perks?.styles[1]
  const shards = perks ? [perks.statPerks.offense, perks.statPerks.flex, perks.statPerks.defense] : []
  return (
    <div className="rune-page">
      <div className="rune-champ"><ChampIcon name={p.champion} size={42} /><span className="sm-text mut">{p.isMe ? 'me' : p.riotId.split('#')[0]}</span></div>
      {perks ? (
        <>
          <div className="slots">{primary?.selections.map((s, i) => <PerkIcon key={i} id={s.perk} className={i === 0 ? 'keystone' : ''} />)}</div>
          <div className="slots">
            {secondary && <PerkIcon id={secondary.style} className="sm" />}
            {secondary?.selections.map((s, i) => <PerkIcon key={i} id={s.perk} />)}
          </div>
          <div className="slots">
            {shards.map((id, i) => <PerkIcon key={i} id={id} className="shard" />)}
          </div>
        </>
      ) : <span className="mut">No rune data.</span>}
    </div>
  )
}

const fmtClock = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`

export default function MatchDetail() {
  const { id } = useParams()
  const [detail, setDetail] = useState<Detail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('general')
  const [clips, setClips] = useState<ClipInfo[]>([])
  const [fullGame, setFullGame] = useState<FullGameStatus | null>(null)
  const [recapAt, setRecapAt] = useState<number | null>(null)
  const clipRefs = useRef<Record<number, HTMLVideoElement | null>>({})

  useEffect(() => {
    if (!id) return
    api.match(id).then(setDetail).catch(e => setError(String(e)))
    api.clips(id).then(setClips).catch(() => setClips([]))
    api.fullGameStatus(id).then(setFullGame).catch(() => setFullGame(null))
  }, [id])

  // Jump the covering clip to ~5s before the moment and play it.
  const playMoment = (timeSec: number) => {
    const clip = clips.find(c => c.ready && timeSec >= c.startSec && timeSec <= c.endSec)
    const el = clip ? clipRefs.current[clip.index] : null
    if (!el) return
    el.currentTime = Math.max(0, timeSec - (clip?.startSec ?? 0) - 5)
    el.scrollIntoView({ behavior: 'smooth', block: 'center' })
    void el.play()
  }

  const clipFor = (timeSec: number) => clips.find(c => c.ready && timeSec >= c.startSec && timeSec <= c.endSec)

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
          <div className="label"><RelTime date={m.gameEndUtc} /> · {m.queueName} · {m.durationMin.toFixed(0)} min</div>
          <div className="mh-row">
            <span className="mr-duel">
              <ChampIcon name={m.champion} size={46} level={m.champLevel} />
              {m.opponentChampion && (
                <>
                  <span className="vs-badge">vs</span>
                  <ChampIcon name={m.opponentChampion} size={36} />
                </>
              )}
            </span>
            <div className="mh-main">
              <div className="value" style={{ marginTop: 0 }}>
                {m.isRemake
                  ? <span className="mut">Remake</span>
                  : <span className={m.win ? 'win' : 'loss'}>{m.win ? 'Victory' : 'Defeat'}</span>} — {m.champion}
              </div>
              <div className="sub">
                <span>{m.kills}/<span className="loss">{m.deaths}</span>/{m.assists}</span> · {m.cs} CS
                {m.laneGoldDiff10 !== null && <> · <span className={m.laneGoldDiff10 >= 0 ? 'win' : 'loss'}>{m.laneGoldDiff10 > 0 ? '+' : ''}{m.laneGoldDiff10}g</span> @10</>}
                {m.hasReplay && (
                  <>
                    {' · '}
                    <a href={`leaguereplay://${window.location.host}/${m.id}`}
                      title="Launch in the League client — needs the replay launcher registered on this PC (LeagueTracker.ReplayLauncher --register)">watch replay ▶</a>
                    {' · '}
                    <a href={`/api/matches/${m.id}/replay`} download
                      title="Official .rofl — plays in the client while this patch is live">⬇︎</a>
                  </>
                )}
              </div>
            </div>
          </div>
        </div>
        {(m.avgAllyRank !== null || m.avgEnemyRank !== null) && (
        <div className="card tile">
          <div className="label">Team ranks {detail.ranksAtGameTime ? '(at game time)' : '(as of capture)'}</div>
          <div className="rank-duel">
            <div className="side">
              <span className="side-label win">My team</span>
              <span className={`rank-big ${tierClass(m.avgAllyRank)}`}>{m.avgAllyRank ?? '—'}</span>
              <span className="sub">{m.allyRanksKnown}/5 known</span>
            </div>
            <span className="vs-badge">vs</span>
            <div className="side">
              <span className="side-label loss">Enemy</span>
              <span className={`rank-big ${tierClass(m.avgEnemyRank)}`}>{m.avgEnemyRank ?? '—'}</span>
              <span className="sub">{m.enemyRanksKnown}/5 known</span>
            </div>
          </div>
          {m.rankGapLp !== null && m.rankGapLp !== 0 && (
            <div className="sub">
              <span className={m.rankGapLp > 0 ? 'loss' : 'win'}>
                {m.rankGapLp > 0 ? `enemy favored by ${m.rankGapLp} LP` : `your side favored by ${-m.rankGapLp} LP`}
              </span>
            </div>
          )}
        </div>
        )}
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

      {fullGame && (fullGame.state !== 'none' || m.hasReplay) && (
        <div className="card" style={{ marginBottom: 14 }}>
          <h2>
            Full game <span className="mut" style={{ fontWeight: 400 }}>— the whole match as one video, camera on you</span>
          </h2>
          {fullGame.state === 'done' && (
            <>
              <video src={`/api/matches/${id}/fullgame`} controls preload="metadata"
                style={{ width: '100%', maxWidth: 960, borderRadius: 8, background: '#000' }} />
              <p className="mut sm-text" style={{ margin: '8px 0 0' }}>
                {fullGame.sizeMb} MB{fullGame.renderedUtc && ` · rendered ${new Date(fullGame.renderedUtc).toLocaleDateString()}`}
                {fullGame.keep ? ' · kept forever' : ' · auto-deleted after the retention window'}
                {' · '}
                <button className="action" style={{ padding: '0 8px' }}
                  onClick={() => id && api.toggleFullGameKeep(id).then(setFullGame)}>
                  {fullGame.keep ? 'unkeep' : 'keep'}
                </button>
                {' '}
                <button className="action" style={{ padding: '0 8px' }}
                  onClick={() => { if (id && window.confirm('Delete this render? The replay may no longer be re-renderable on a newer patch.')) { void api.deleteFullGame(id).then(() => api.fullGameStatus(id).then(setFullGame)) } }}>
                  delete
                </button>
              </p>
            </>
          )}
          {(fullGame.state === 'requested' || fullGame.state === 'rendering') && (
            <p className="mut" style={{ margin: 0 }}>
              {fullGame.state === 'requested' ? 'Queued — waiting for the render agent on the gaming PC.' : 'Rendering now on the gaming PC…'}
            </p>
          )}
          {fullGame.state === 'failed' && (
            <p style={{ margin: 0 }}>
              <span className="loss">Render failed:</span> <span className="mut">{fullGame.error}</span>{' '}
              <button className="action" onClick={() => id && api.retryRender(id, 'full').then(() => api.fullGameStatus(id).then(setFullGame))}>Retry</button>
            </p>
          )}
          {fullGame.state === 'none' && (
            <p className="mut" style={{ margin: 0 }}>
              <button className="action" onClick={() => id && api.requestFullGame(id).then(setFullGame)}>Render full game</button>
              {' '}~500 MB and a real-time render on the gaming PC — worth it for games you want to study start to finish.
              Unkept renders are deleted automatically after the retention window; the clips below stay forever.
            </p>
          )}
        </div>
      )}

      {clips.length > 0 && (
        <div className="card" style={{ marginBottom: 14 }}>
          <h2>
            Clips <span className="mut" style={{ fontWeight: 400 }}>— your kills & deaths, rendered from the official replay</span>
          </h2>
          {clips.every(c => !c.ready) ? (
            <p className="mut" style={{ margin: 0 }}>
              {clips.length} fight window{clips.length === 1 ? '' : 's'} planned — waiting for the render agent on the gaming PC.
            </p>
          ) : (
            <div className="grid two-col">
              {clips.map(c => (
                <div key={c.index}>
                  <div className="sub-h" style={{ marginTop: 0 }}>
                    {c.label} · {fmtClock(c.startSec)}–{fmtClock(c.endSec)}
                    <span className="mut"> · {c.events.map(e => `${e.kind} ${fmtClock(e.timeSec)}`).join(', ')}</span>
                    {c.ready && (
                      <button className="action" style={{ padding: '0 8px', marginLeft: 8 }}
                        title="Delete this clip and queue just this window for a fresh render on the gaming PC"
                        onClick={() => {
                          if (id && window.confirm('Delete this clip? The render agent will re-create it from the replay (needs the replay still playable on the current patch).')) {
                            void api.deleteClip(id, c.index).then(() => api.clips(id).then(setClips))
                          }
                        }}>
                        ✕ re-render
                      </button>
                    )}
                  </div>
                  {c.ready ? (
                    <video
                      ref={el => { clipRefs.current[c.index] = el }}
                      src={c.url} controls preload="metadata"
                      style={{ width: '100%', borderRadius: 8, background: '#000' }}
                    />
                  ) : (
                    <div className="empty">queued for render</div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      <div className="filters">
        <div className="seg" role="tablist" aria-label="Match view">
          {(['general', 'details', 'runes', 'timeline'] as Tab[]).map(t => (
            <button key={t} className={t === tab ? 'on' : ''} onClick={() => setTab(t)}>
              {t === 'general' ? 'General' : t === 'details' ? 'Details' : t === 'runes' ? 'Runes' : 'Deaths & objectives'}
            </button>
          ))}
        </div>
      </div>

      {tab === 'general' && (() => {
        const scores = carryScores(participants)
        return (
          <>
            <Scoreboard title={m.isRemake ? 'Remake' : allies[0]?.win ? 'Victory' : 'Defeat'} side={detail.mySide} won={!m.isRemake && (allies[0]?.win ?? false)}
              players={allies} objectives={detail.teamObjectives.ally} maxDamage={maxDamage} durationMin={m.durationMin} scores={scores} />
            <Scoreboard title={m.isRemake ? 'Remake' : enemies[0]?.win ? 'Victory' : 'Defeat'} side={enemySide} won={!m.isRemake && (enemies[0]?.win ?? false)}
              players={enemies} objectives={detail.teamObjectives.enemy} maxDamage={maxDamage} durationMin={m.durationMin} scores={scores} />
          </>
        )
      })()}

      {tab === 'details' && <DetailsTab detail={detail} />}

      {tab === 'runes' && (
        <>
          <div className="card" style={{ marginBottom: 14 }}>
            <h2>
              <span className={m.isRemake ? 'mut' : allies[0]?.win ? 'win' : 'loss'}>{m.isRemake ? 'Remake' : allies[0]?.win ? 'Victory' : 'Defeat'}</span>{' '}
              <span className="mut" style={{ fontWeight: 400 }}>({detail.mySide} side — my team)</span>
            </h2>
            <div className="rune-grid">
              {allies.map(p => <RunePage key={p.participantId} p={p} />)}
            </div>
          </div>
          <div className="card">
            <h2>
              <span className={m.isRemake ? 'mut' : enemies[0]?.win ? 'win' : 'loss'}>{m.isRemake ? 'Remake' : enemies[0]?.win ? 'Victory' : 'Defeat'}</span>{' '}
              <span className="mut" style={{ fontWeight: 400 }}>({enemySide} side)</span>
            </h2>
            <div className="rune-grid">
              {enemies.map(p => <RunePage key={p.participantId} p={p} />)}
            </div>
          </div>
        </>
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
                      <Fragment key={d.timeSec}>
                      <tr style={{ cursor: 'pointer' }} title="Click for the death recap"
                        onClick={() => setRecapAt(recapAt === d.timeSec ? null : d.timeSec)}>
                        <td style={{ whiteSpace: 'nowrap' }}>
                          <span className="disclosure">{recapAt === d.timeSec ? '▾' : '▸'}</span>
                          {d.gameTime}
                          {clipFor(d.timeSec) && (
                            <button className="action" style={{ marginLeft: 6, padding: '0 6px' }}
                              title="Watch this death" onClick={e => { e.stopPropagation(); playMoment(d.timeSec) }}>▶</button>
                          )}
                        </td>
                        <td className="mut">{d.zone || '—'}</td>
                        <td>
                          {d.killedBy}{d.assistedBy && <span className="mut"> +{d.assistedBy}</span>}
                          {deathTag(d, m.opponentChampion, m.enemyJungler) === 'solo' && <span className="badge loss" style={{ marginLeft: 6 }}>solo-killed</span>}
                          {deathTag(d, m.opponentChampion, m.enemyJungler) === 'gank' && <span className="badge remake" style={{ marginLeft: 6 }}>gank</span>}
                        </td>
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
                      {recapAt === d.timeSec && (
                        <tr className="drill">
                          <td colSpan={10}>
                            {d.damageInstances.length > 0
                              ? <DeathRecap d={d} />
                              : <span className="mut">No damage detail recorded for this death.</span>}
                          </td>
                        </tr>
                      )}
                      </Fragment>
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
                      <tr key={`${o.timeSec}-${o.kind}`} className={o.byMyTeam ? 'obj-row-win' : 'obj-row-loss'}>
                        <td style={{ whiteSpace: 'nowrap' }}>{o.gameTime}</td>
                        <td><span className={`obj-kind ${OBJ_KIND_CLASS[o.kind] ?? ''}`}>{objectiveLabel(o.kind, o.subKind)}</span></td>
                        <td className={o.byMyTeam ? 'win' : 'loss'}>{o.byMyTeam ? 'My team' : 'Enemy'}</td>
                        <td>{o.killer
                          ? <span className="sb-player" style={{ gap: 7 }}><ChampIcon name={o.killer} size={22} />{o.killer}</span>
                          : <span className="mut">—</span>}</td>
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

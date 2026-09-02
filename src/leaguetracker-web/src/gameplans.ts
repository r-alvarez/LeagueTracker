import type { GameplanPhase, PointStatus, RuleSpec } from './types'

export const PHASES: GameplanPhase[] = ['early', 'mid', 'late']
export const PHASE_LABEL: Record<GameplanPhase, string> = { early: 'Lane phase', mid: 'Mid game', late: 'Late game' }
export const PHASE_HINT: Record<GameplanPhase, string> = { early: 'to 14:00', mid: '14:00 – 25:00', late: 'after 25:00' }

export const STATUS_LABEL: Record<PointStatus, string> = { met: 'Met', missed: 'Missed', na: 'N/A', pending: 'Pending' }

export type ParamUnit = 'clock' | 'units' | 'pct' | 'item' | 'level' | 'count' | 'minute' | 'toggle'

export interface RuleParamMeta { key: string; label: string; unit: ParamUnit }

export interface RuleKindMeta {
  kind: string
  label: string
  desc: string
  params: RuleParamMeta[]
}

/// Mirrors GameplanRules.Defaults on the server: it owns the numbers, this side the words.
export const RULE_KINDS: RuleKindMeta[] = [
  {
    kind: 'level_window_fight', label: 'At level N, fight with the jungler',
    desc: 'Met when a fight you were in, with your jungler on its kill ledger or beside it, starts within the window after you hit the level - or when you two grouped (within 1.5k) in that window, since a 2v2 the enemy walks away from leaves no kill to see. N/A when the jungler never came within 4k. Default window 5:00: the median gap from 6 to the first such fight is ~4:53 over your history.',
    params: [
      { key: 'level', label: 'Level', unit: 'level' },
      { key: 'windowSec', label: 'Window after hitting it', unit: 'clock' },
      { key: 'withJungler', label: 'Jungler must be there', unit: 'toggle' },
    ],
  },
  {
    kind: 'objective_arrival', label: 'Early to contested neutrals',
    desc: 'For every dragon / herald / grubs / baron / Atakhan both teams were at (2+ of yours, 1+ of theirs), whether you were already within range this long before it fell. Positions are interpolated between minute frames.',
    params: [
      { key: 'leadSec', label: 'Be there this early', unit: 'clock' },
      { key: 'nearUnits', label: 'Within', unit: 'units' },
      { key: 'fromSec', label: 'Count objectives after', unit: 'clock' },
      { key: 'toSec', label: 'Until (0:00 = end)', unit: 'clock' },
      { key: 'minPct', label: 'Met when early to at least', unit: 'pct' },
    ],
  },
  {
    kind: 'picks', label: 'Generate picks',
    desc: 'Kills you took part in where no other enemy stood within the isolation range of the victim, outside teamfight-sized fights.',
    params: [
      { key: 'minPicks', label: 'Picks wanted', unit: 'count' },
      { key: 'fromSec', label: 'Count kills after', unit: 'clock' },
      { key: 'isolationUnits', label: 'Isolation range', unit: 'units' },
    ],
  },
  {
    kind: 'item_by', label: 'Buy an item by a time',
    desc: 'The first purchase of the item against the target clock - the wave-clear breakpoint, the first component that changes how you lane.',
    params: [
      { key: 'itemId', label: 'Item', unit: 'item' },
      { key: 'bySec', label: 'By', unit: 'clock' },
    ],
  },
  {
    kind: 'level_by', label: 'Reach a level by a time',
    desc: 'Your own level clock against the target - a spike you should be hitting on time.',
    params: [
      { key: 'level', label: 'Level', unit: 'level' },
      { key: 'bySec', label: 'By', unit: 'clock' },
    ],
  },
  {
    kind: 'jungler_proximity', label: 'Play near the jungler',
    desc: 'Share of the minute frames in the window where you were within range of your jungler.',
    params: [
      { key: 'fromSec', label: 'From', unit: 'clock' },
      { key: 'toSec', label: 'To', unit: 'clock' },
      { key: 'nearUnits', label: 'Within', unit: 'units' },
      { key: 'minPct', label: 'Met at', unit: 'pct' },
    ],
  },
  {
    kind: 'early_wards', label: 'Wards in the first 10 minutes',
    desc: 'Vision wards you placed before 10:00 (trinket and control wards; the sweeper is not vision). Riot gives no ward positions, so this counts, it does not place.',
    params: [
      { key: 'minWards', label: 'Wards wanted', unit: 'count' },
    ],
  },
  {
    kind: 'early_skirmish_deaths', label: 'Careful in early skirmishes',
    desc: 'Deaths before the cut-off where the fight\'s kill ledger names two or more enemies, or the enemy jungler was on you (a gank is a 1v2). 1v1 deaths are a different failure and do not count; skirmishes entered and lost show as context. Default allows one: on 167 local Viktor games, 0 / 1 / 2+ such deaths ran 61% / 55% / 27% win rate.',
    params: [
      { key: 'maxDeaths', label: 'Allowed', unit: 'count' },
      { key: 'includeGanks', label: 'Ganks count', unit: 'toggle' },
      { key: 'untilSec', label: 'Until', unit: 'clock' },
    ],
  },
  {
    kind: 'numbers_fights', label: 'Create man advantages',
    desc: 'Fights you joined after the start time where your side outnumbered theirs and you arrived from at least the travel distance away - the shove-and-move made visible. Two or more ran 58% / 59% wins vs 35% / 33% below on your Ahri / Viktor games.',
    params: [
      { key: 'minFights', label: 'Fights wanted', unit: 'count' },
      { key: 'fromSec', label: 'Count fights after', unit: 'clock' },
      { key: 'toSec', label: 'Until (0:00 = end)', unit: 'clock' },
      { key: 'movedUnits', label: 'Travelled at least', unit: 'units' },
    ],
  },
  {
    kind: 'duels_taken', label: 'Take 1v1s',
    desc: 'Duels (a fight with one on each side of the kill ledger) you were in after the start time, with the record as context. Willingness, not skill: the split carries no win signal on your games.',
    params: [
      { key: 'minDuels', label: 'Duels wanted', unit: 'count' },
      { key: 'fromSec', label: 'Count duels after', unit: 'clock' },
    ],
  },
  {
    kind: 'jungler_fights', label: 'Fight beside the jungler',
    desc: 'Share of the fights you were in after the start time that had your jungler on the kill ledger or beside it - playing off the jungler as fights, not as distance. N/A with fewer fights than the minimum.',
    params: [
      { key: 'minPct', label: 'Met at', unit: 'pct' },
      { key: 'fromSec', label: 'Count fights after', unit: 'clock' },
      { key: 'minFights', label: 'Needs at least', unit: 'count' },
    ],
  },
  {
    kind: 'farm_rate', label: 'Keep farm up',
    desc: 'Your CS per minute between two lane-diff checkpoints (3-minute steps plus 10 / 15 / 20 / 25 / 30). N/A when the game ended first or has no same-role opponent to checkpoint against. 8 is your mid-game median across Ahri and Viktor.',
    params: [
      { key: 'fromMin', label: 'From minute', unit: 'minute' },
      { key: 'toMin', label: 'To minute', unit: 'minute' },
      { key: 'minPerMin', label: 'CS per minute wanted', unit: 'count' },
    ],
  },
  {
    kind: 'caught_out', label: 'Not caught alone',
    desc: 'Deaths after the start time with nobody interpolated near you and outside a fight the enemy committed three or more to - the same fog-pick test the Discipline verdict uses.',
    params: [
      { key: 'maxDeaths', label: 'Allowed', unit: 'count' },
      { key: 'fromSec', label: 'Count deaths after', unit: 'clock' },
    ],
  },
]

export const ruleMeta = (kind: string) => RULE_KINDS.find(r => r.kind === kind) ?? null

export const clock = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`

export function parseClock(text: string): number | null {
  const t = text.trim()
  if (/^\d+$/.test(t)) return parseInt(t, 10)
  const m = /^(\d+):([0-5]?\d)$/.exec(t)
  return m ? parseInt(m[1], 10) * 60 + parseInt(m[2], 10) : null
}

const units = (u: number) => u >= 1000 ? `${(u / 1000).toFixed(1).replace(/\.0$/, '')}k` : String(u)

export function describeRule(rule: RuleSpec, itemName: (id: number) => string | null): string {
  const p = rule.params
  switch (rule.kind) {
    case 'level_window_fight':
      return `at level ${p.level}, ${p.withJungler ? 'fight with the jungler' : 'take a fight'} within ${clock(p.windowSec)}`
    case 'objective_arrival':
      return `within ${units(p.nearUnits)} of a contested neutral ${p.leadSec}s before it falls (${p.minPct}% of them${p.toSec > 0 ? `, ${clock(p.fromSec)}–${clock(p.toSec)}` : p.fromSec > 0 ? `, after ${clock(p.fromSec)}` : ''})`
    case 'picks':
      return `${p.minPicks} isolated kill${p.minPicks === 1 ? '' : 's'} after ${clock(p.fromSec)}`
    case 'item_by':
      return `${p.itemId > 0 ? (itemName(p.itemId) ?? `item ${p.itemId}`) : 'an item'} by ${clock(p.bySec)}`
    case 'level_by':
      return `level ${p.level} by ${clock(p.bySec)}`
    case 'jungler_proximity':
      return `within ${units(p.nearUnits)} of the jungler ${p.minPct}% of ${clock(p.fromSec)}–${clock(p.toSec)}`
    case 'early_wards':
      return `${p.minWards} ward${p.minWards === 1 ? '' : 's'} before 10:00`
    case 'numbers_fights':
      return `${p.minFights} fight${p.minFights === 1 ? '' : 's'} with numbers ${p.toSec > 0 ? `${clock(p.fromSec)}–${clock(p.toSec)}` : `after ${clock(p.fromSec)}`}, arriving from ${units(p.movedUnits)}+`
    case 'farm_rate':
      return `${p.minPerMin} cs/min between ${p.fromMin}:00 and ${p.toMin}:00`
    case 'duels_taken':
      return `${p.minDuels} 1v1${p.minDuels === 1 ? '' : 's'} after ${clock(p.fromSec)}`
    case 'jungler_fights':
      return `${p.minPct}% of your fights after ${clock(p.fromSec)} beside the jungler`
    case 'early_skirmish_deaths':
      return `at most ${p.maxDeaths} outnumbered death${p.maxDeaths === 1 ? '' : 's'} before ${clock(p.untilSec)}${p.includeGanks ? ', ganks included' : ''}`
    case 'caught_out':
      return p.maxDeaths === 0 ? `never caught alone after ${clock(p.fromSec)}` : `caught alone at most ${p.maxDeaths}× after ${clock(p.fromSec)}`
    default:
      return rule.kind
  }
}

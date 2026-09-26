import { clockMapper } from './vodClock'
import type { DeathEvent, FightCluster, KillMoment, MapMoment, MatchTrack, VodStatus } from './types'

// What an ult bought is read off the seconds after it: the takedowns the
// player was part of, and whether they lived through it. A cast that led to
// nothing is not always a waste (an escape, a wave) - which is why the label
// says what happened and leaves the judging to the footage.
const PAYOFF_SEC = 12

export function ultMoments(vod: VodStatus | null, kills: KillMoment[], deaths: DeathEvent[], fights: FightCluster[] | null, track: MatchTrack | null): MapMoment[] {
  const ults = vod?.apm?.ults ?? []
  if (ults.length === 0) return []
  const toGame = clockMapper(vod?.meta?.clockMap)
  return ults.flatMap(u => {
    const t = toGame('videoSec', 'gameSec', u.videoSec) ?? u.videoSec
    // Before the game clock started: loading-screen key mashing.
    if (t < 0) return []
    const inWindow = (s: number) => s >= t - 1 && s <= t + PAYOFF_SEC
    // The track knows assists too; without it only the player's own kills count.
    const takedowns = track
      ? track.kills.filter(k => inWindow(k.t) && (k.killer === track.myPid || k.assists.includes(track.myPid))).length
      : kills.filter(k => inWindow(k.timeSec)).length
    const died = deaths.some(d => inWindow(d.timeSec))
    const fight = (fights ?? []).find(f => t >= f.startSec - 5 && t <= f.endSec)
    const payoff = takedowns > 0
      ? `${takedowns} takedown${takedowns === 1 ? '' : 's'}${died ? ', then died' : ''}`
      : died ? 'died, no takedown' : 'no takedown'
    const where = fight ? ` · ${fight.kind} ${fight.allies}v${fight.enemies}` : ''
    return [{
      kind: 'ult' as const,
      timeSec: Math.round(t),
      endSec: Math.round(t) + 10,
      label: `${u.confirmed ? 'ult' : 'R pressed'} → ${payoff}${where}`,
      tone: (takedowns > 0 && !died ? 'win' : died && takedowns === 0 ? 'loss' : 'neutral') as MapMoment['tone'],
    }]
  })
}

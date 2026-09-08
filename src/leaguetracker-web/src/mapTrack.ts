import type { MapMoment, TrackFrame, TrackKill } from './types'

// Summoner's Rift's coordinate space as Riot publishes it for map 11:
// x -120..14870, y -120..14980, y growing northward - so it flips onto an
// image whose origin is the top-left corner.
export const RIFT = { minX: -120, minY: -120, maxX: 14870, maxY: 14980 }
export const MAP_SIZE = 512

export type Point = [number, number]

export function toMap(x: number, y: number, size = MAP_SIZE): { px: number; py: number } {
  return {
    px: ((x - RIFT.minX) / (RIFT.maxX - RIFT.minX)) * size,
    py: size - ((y - RIFT.minY) / (RIFT.maxY - RIFT.minY)) * size,
  }
}

export interface TimeWindow { start: number; end: number }

// A fight is watched from its approach; a single kill or death from the
// seconds that led to it. Clamped to the game so the scrubber never runs
// past the last frame.
export function windowFor(m: MapMoment, durationSec: number): TimeWindow {
  const [before, after] = m.kind === 'fight' ? [20, 10] : [15, 8]
  const start = Math.max(0, m.timeSec - before)
  const end = Math.min(Math.max(durationSec, start + 1), (m.endSec ?? m.timeSec) + after)
  return { start, end: Math.max(end, start + 1) }
}

export interface Anchor { t: number; p: Point }

// A champion's known positions in time order: Riot's 60-second samples plus
// every kill they dealt or took, which pins them to the second at the exact
// spot. Assists are left out - a global ult earns one from across the map.
export function pathFor(frames: TrackFrame[], kills: TrackKill[], pid: number): Anchor[] {
  const anchors: Anchor[] = []
  for (const f of frames) {
    const p = f.p[pid - 1]
    if (p) anchors.push({ t: f.t, p: [p[0], p[1]] })
  }
  for (const k of kills) {
    if (k.killer === pid || k.victim === pid) anchors.push({ t: k.t, p: [k.x, k.y] })
  }
  return anchors.sort((a, b) => a.t - b.t)
}

// Straight-line interpolation between known positions: enough to show who
// approached from where, never the footwork inside the fight.
export function positionAt(path: Anchor[], t: number): Point | null {
  if (path.length === 0) return null
  const first = path[0]
  const last = path[path.length - 1]
  if (t <= first.t) return [first.p[0], first.p[1]]
  if (t >= last.t) return [last.p[0], last.p[1]]
  let i = 1
  while (path[i].t < t) i++
  const a = path[i - 1]
  const b = path[i]
  if (b.t === a.t) return [b.p[0], b.p[1]]
  const f = (t - a.t) / (b.t - a.t)
  return [a.p[0] + (b.p[0] - a.p[0]) * f, a.p[1] + (b.p[1] - a.p[1]) * f]
}

const dist = (a: Point, b: Point) => Math.hypot(a[0] - b[0], a[1] - b[1])

// Riot exposes no respawn timer. A champion killed at k is held at the kill
// spot until the first proof of life: a later sample far from it (respawn
// is a teleport to the fountain, and a dead champion's samples freeze at
// the spot) or a later kill they dealt or assisted - the ledger runs to
// the second, the samples to the minute.
export function deathSpotAt(kills: TrackKill[], frames: TrackFrame[], pid: number, t: number): Point | null {
  let latest: TrackKill | null = null
  for (const k of kills) {
    if (k.t > t) break
    if (k.victim === pid) latest = k
  }
  if (!latest) return null
  const death = latest
  const spot: Point = [death.x, death.y]
  const farSample = frames.find(f => f.t > death.t && f.p[pid - 1] != null && dist(f.p[pid - 1] as Point, spot) > 1500)
  const ledgerProof = kills.find(k => k.t > death.t && (k.killer === pid || k.assists.includes(pid)))
  const revivedAt = Math.min(farSample?.t ?? Infinity, ledgerProof?.t ?? Infinity)
  return revivedAt <= t ? null : spot
}

export const clock = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`

// The chips open on the moment a review most wants seen: a fight the player
// missed (the footage never had it), else their first death, else whatever
// came first.
export function defaultMoment(moments: MapMoment[]): number {
  const missed = moments.findIndex(m => m.kind === 'fight' && m.withoutMe)
  if (missed >= 0) return missed
  const death = moments.findIndex(m => m.kind === 'death')
  return death >= 0 ? death : 0
}

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

const copy = (p: Point | null): Point | null => (p ? [p[0], p[1]] : null)

// Straight-line interpolation between Riot's 60-second samples: enough to
// show who approached from where, never the footwork inside the fight.
export function positionsAt(frames: TrackFrame[], t: number): (Point | null)[] {
  if (frames.length === 0) return []
  const first = frames[0]
  const last = frames[frames.length - 1]
  if (t <= first.t) return first.p.map(copy)
  if (t >= last.t) return last.p.map(copy)
  let i = 1
  while (frames[i].t < t) i++
  const a = frames[i - 1]
  const b = frames[i]
  const f = (t - a.t) / (b.t - a.t)
  return a.p.map((pa, idx) => {
    const pb = b.p[idx] ?? null
    if (!pa || !pb) return copy(pa ?? pb)
    return [pa[0] + (pb[0] - pa[0]) * f, pa[1] + (pb[1] - pa[1]) * f]
  })
}

const dist = (a: Point, b: Point) => Math.hypot(a[0] - b[0], a[1] - b[1])

// Riot exposes no respawn timer. A champion killed at k is held at the
// kill spot until the first later sample that sits far from it: respawn is
// a teleport to the fountain, so that sample is the first proof of life.
export function deathSpotAt(kills: TrackKill[], frames: TrackFrame[], pid: number, t: number): Point | null {
  let latest: TrackKill | null = null
  for (const k of kills) {
    if (k.t > t) break
    if (k.victim === pid) latest = k
  }
  if (!latest) return null
  const spot: Point = [latest.x, latest.y]
  const revived = frames.find(f => f.t > latest.t && f.p[pid - 1] != null && dist(f.p[pid - 1] as Point, spot) > 1500)
  return revived && revived.t <= t ? null : spot
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

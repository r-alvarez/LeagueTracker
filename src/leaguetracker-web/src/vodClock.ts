import type { VodClockPair } from './types'

export type ClockSide = 'videoSec' | 'gameSec'

// Piecewise-linear mapping over the sampled (videoSec, gameSec) pairs. A
// capture restart leaves a gap in the video while the game clock runs on,
// so the two sides of a seam sit at different offsets. Outside the sampled
// range the clocks advance in lockstep. Null when there is no clock map.
export function clockMapper(clockMap: VodClockPair[] | null | undefined) {
  const pairs = [...(clockMap ?? []).filter(p => Number.isFinite(p.videoSec) && Number.isFinite(p.gameSec))]
    .sort((a, b) => a.videoSec - b.videoSec)
  return (from: ClockSide, to: ClockSide, x: number): number | null => {
    if (pairs.length === 0) return null
    const first = pairs[0]
    const last = pairs[pairs.length - 1]
    if (x <= first[from]) return first[to] + (x - first[from])
    if (x >= last[from]) return last[to] + (x - last[from])
    const upper = pairs.findIndex(p => p[from] >= x)
    const lo = pairs[upper - 1]
    const hi = pairs[upper]
    const span = hi[from] - lo[from]
    if (span <= 0) return lo[to] + (x - lo[from])
    return lo[to] + ((x - lo[from]) / span) * (hi[to] - lo[to])
  }
}

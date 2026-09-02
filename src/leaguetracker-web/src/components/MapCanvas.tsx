import { useMemo } from 'react'
import { useChampionIcons, useMinimapUrl } from '../champions'
import { MAP_SIZE, clock, deathSpotAt, positionsAt, toMap } from '../mapTrack'
import type { MatchTrack } from '../types'

const ALLY = '#3d8ef3'
const ENEMY = '#f0556a'
const ME = '#f5c451'
const OBJECTIVE_GLYPH: Record<string, string> = { DRAGON: 'D', BARON: 'B', HERALD: 'H', GRUBS: 'G', TOWER: 'T', INHIBITOR: 'I', ATAKHAN: 'A' }
const KILL_FADE_SEC = 6
const OBJECTIVE_FADE_SEC = 10

// Summoner's Rift at one instant: ten champions on Riot's minimap, kills
// and objectives where they happened. Needs no replay file, no render box
// and no patch match, only the samples the tracker already keeps.
export default function MapCanvas({ track, t, label }: { track: MatchTrack; t: number; label?: string }) {
  const icon = useChampionIcons()
  const minimap = useMinimapUrl()
  const positions = positionsAt(track.frames, t)
  const allyPids = useMemo(() => new Set(track.participants.filter(p => p.isAlly).map(p => p.pid)), [track.participants])
  const sideColor = (pid: number) => (allyPids.has(pid) ? ALLY : ENEMY)

  return (
    <svg viewBox={`0 0 ${MAP_SIZE} ${MAP_SIZE}`} className="map-svg" role="img"
      aria-label={`Summoner's Rift at ${clock(t)}${label ? `: ${label}` : ''}`}>
      <defs><clipPath id="map-icon-clip"><circle r={10} /></clipPath></defs>
      {minimap
        ? <image href={minimap} width={MAP_SIZE} height={MAP_SIZE} />
        : <rect width={MAP_SIZE} height={MAP_SIZE} fill="#101a14" />}

      {track.objectives.filter(o => o.t <= t && t - o.t < OBJECTIVE_FADE_SEC).map(o => {
        const { px, py } = toMap(o.x, o.y)
        return (
          <g key={`o-${o.t}-${o.kind}`} transform={`translate(${px} ${py})`} opacity={1 - (0.7 * (t - o.t)) / OBJECTIVE_FADE_SEC}>
            <title>{`${o.kind.toLowerCase()} · ${o.byMyTeam ? 'my team' : 'enemy'} · ${clock(o.t)}`}</title>
            <rect x={-9} y={-9} width={18} height={18} transform="rotate(45)" fill={o.byMyTeam ? ALLY : ENEMY} stroke="#0b0f14" strokeWidth={1.5} />
            <text y={4} textAnchor="middle" fontSize={10} fontWeight={700} fill="#fff">{OBJECTIVE_GLYPH[o.kind] ?? '?'}</text>
          </g>
        )
      })}

      {track.kills.filter(k => k.t <= t && t - k.t < KILL_FADE_SEC).map(k => {
        const { px, py } = toMap(k.x, k.y)
        const age = (t - k.t) / KILL_FADE_SEC
        return (
          <circle key={`k-${k.t}-${k.victim}`} cx={px} cy={py} r={14 + age * 10} fill="none"
            stroke={sideColor(k.killer)} strokeWidth={2.5} opacity={1 - age} />
        )
      })}

      {track.participants.map(p => {
        const spot = deathSpotAt(track.kills, track.frames, p.pid, t)
        const pos = spot ?? positions[p.pid - 1]
        if (!pos) return null
        const { px, py } = toMap(pos[0], pos[1])
        const src = icon(p.champion)
        return (
          <g key={p.pid} transform={`translate(${px} ${py})`} opacity={spot ? 0.45 : 1}>
            <title>{`${p.champion}${p.isMe ? ' (you)' : ''} · ${clock(t)}${spot ? ' · dead' : ''}`}</title>
            <circle r={12} fill={p.isAlly ? ALLY : ENEMY} />
            {src
              ? <image href={src} x={-10} y={-10} width={20} height={20} clipPath="url(#map-icon-clip)" />
              : <text y={4} textAnchor="middle" fontSize={11} fontWeight={700} fill="#fff">{p.champion.slice(0, 1)}</text>}
            {p.isMe && <circle r={14.5} fill="none" stroke={ME} strokeWidth={2} />}
            {spot && <text y={5} textAnchor="middle" fontSize={15} fontWeight={700} fill="#fff">✕</text>}
          </g>
        )
      })}
    </svg>
  )
}

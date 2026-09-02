import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useChampionIcons, useMinimapUrl } from '../champions'
import { MAP_SIZE, clock, deathSpotAt, defaultMoment, positionsAt, toMap, windowFor } from '../mapTrack'
import type { MapMoment, MatchTrack } from '../types'

const ALLY = '#3d8ef3'
const ENEMY = '#f0556a'
const ME = '#f5c451'
const OBJECTIVE_GLYPH: Record<string, string> = { DRAGON: 'D', BARON: 'B', HERALD: 'H', GRUBS: 'G', TOWER: 'T', INHIBITOR: 'I', ATAKHAN: 'A' }
const KILL_FADE_SEC = 6
const OBJECTIVE_FADE_SEC = 10
const SPEEDS = [4, 8]

type Filter = 'missed' | 'mine' | 'objectives' | 'all'
const FILTERS: { key: Filter; label: string; test: (m: MapMoment) => boolean }[] = [
  { key: 'missed', label: 'Fights without you', test: m => m.kind === 'fight' && !!m.withoutMe },
  { key: 'mine', label: 'Your fights', test: m => (m.kind === 'fight' && !m.withoutMe) || m.kind === 'kill' || m.kind === 'death' },
  { key: 'objectives', label: 'Objectives', test: m => m.kind === 'objective' },
  { key: 'all', label: 'All', test: () => true },
]

// The match on Riot's minimap, scrubbed through one moment at a time. It
// exists for the fights the player's own footage never saw: those need no
// replay file, no render box and no patch match, only the samples the
// tracker already keeps.
export default function MapReplay({ track, moments, jumpTo }: { track: MatchTrack; moments: MapMoment[]; jumpTo: MapMoment | null }) {
  const icon = useChampionIcons()
  const minimap = useMinimapUrl()
  const [selected, setSelected] = useState(() => defaultMoment(moments))
  const [filter, setFilter] = useState<Filter>(() => (moments.some(FILTERS[0].test) ? 'missed' : 'all'))
  const moment = moments[selected] ?? null
  const win = useMemo(
    () => (moment ? windowFor(moment, track.durationSec) : { start: 0, end: track.durationSec }),
    [moment, track.durationSec])
  // At rest the map shows the moment itself; play starts from the approach.
  const [t, setT] = useState(() => moment?.timeSec ?? 0)
  const [playing, setPlaying] = useState(false)
  const [speed, setSpeed] = useState(SPEEDS[0])
  const root = useRef<HTMLDivElement>(null)

  const open = useCallback((idx: number) => {
    setSelected(idx)
    setT(windowFor(moments[idx], track.durationSec).start)
    setPlaying(true)
  }, [moments, track.durationSec])

  useEffect(() => {
    if (!jumpTo) return
    const idx = moments.findIndex(m => m.kind === jumpTo.kind && m.timeSec === jumpTo.timeSec)
    if (idx < 0) return
    setFilter(current => (FILTERS.find(f => f.key === current)?.test(moments[idx]) ? current : 'all'))
    open(idx)
    root.current?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  }, [jumpTo, moments, open])

  useEffect(() => {
    if (!playing) return
    let raf = 0
    let last = performance.now()
    const step = (now: number) => {
      const dt = (now - last) / 1000
      last = now
      setT(prev => Math.min(win.end, prev + dt * speed))
      raf = requestAnimationFrame(step)
    }
    raf = requestAnimationFrame(step)
    return () => cancelAnimationFrame(raf)
  }, [playing, speed, win.end])

  useEffect(() => {
    if (playing && t >= win.end) setPlaying(false)
  }, [playing, t, win.end])

  const positions = positionsAt(track.frames, t)
  const allyPids = useMemo(() => new Set(track.participants.filter(p => p.isAlly).map(p => p.pid)), [track.participants])
  const sideColor = (pid: number) => (allyPids.has(pid) ? ALLY : ENEMY)
  const counts = useMemo(() => FILTERS.map(f => moments.filter(f.test).length), [moments])
  const shown = FILTERS.find(f => f.key === filter) ?? FILTERS[3]

  return (
    <div className="map-replay" ref={root}>
      <div className="map-main">
        <div className="map-stage">
          <svg viewBox={`0 0 ${MAP_SIZE} ${MAP_SIZE}`} className="map-svg" role="img"
            aria-label={`Summoner's Rift at ${clock(t)}${moment ? `: ${moment.label}` : ''}`}>
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
        </div>

        <div className="map-controls">
          <button type="button" className="action primary" onClick={() => {
            if (!playing && t >= win.end - 0.01) setT(win.start)
            setPlaying(p => !p)
          }}>{playing ? 'Pause' : 'Play'}</button>
          <span className="map-clock">{clock(t)}</span>
          <input type="range" min={win.start} max={win.end} step={0.25} value={t} aria-label="Game clock"
            onChange={e => { setPlaying(false); setT(Number(e.target.value)) }} />
          <span className="map-speeds">
            {SPEEDS.map(s => (
              <button key={s} type="button" className={`action${speed === s ? ' primary' : ''}`} onClick={() => setSpeed(s)}>{s}×</button>
            ))}
          </span>
        </div>
        {moment && (
          <div className="map-now">
            <span className={`map-chip-time ${moment.tone ?? 'neutral'}`}>{clock(moment.timeSec)}</span> {moment.label}
            {moment.withoutMe && <span className="map-chip-flag"> · without you</span>}
          </div>
        )}
        <p className="mut sm-text map-caption">
          Positions are Riot's 60-second samples, moved in straight lines between them; kills and objectives sit where they
          happened. A champion stays at their death spot until the first sample that shows them elsewhere.
        </p>
      </div>

      {moments.length > 0 && (
        <div className="map-side">
          <div className="map-filters" role="tablist" aria-label="Which moments">
            {FILTERS.map((f, i) => counts[i] > 0 && (
              <button key={f.key} type="button" role="tab" aria-selected={filter === f.key}
                className={`map-filter${filter === f.key ? ' active' : ''}`} onClick={() => setFilter(f.key)}>
                {f.label} <span className="mut">{counts[i]}</span>
              </button>
            ))}
          </div>
          <div className="map-chips">
            {moments.map((m, i) => shown.test(m) && (
              <button key={`${m.kind}-${m.timeSec}`} type="button"
                className={`map-chip ${m.tone ?? 'neutral'}${i === selected ? ' active' : ''}`} onClick={() => open(i)}>
                <span className="map-chip-time">{clock(m.timeSec)}</span>
                <span className="map-chip-label">{m.label}</span>
                {m.withoutMe && filter !== 'missed' && <span className="map-chip-flag">without you</span>}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

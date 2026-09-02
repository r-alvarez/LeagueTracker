import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { clock, defaultMoment, windowFor } from '../mapTrack'
import ClipView, { clipFor } from './ClipView'
import FootageView, { footageSource } from './FootageView'
import MapCanvas from './MapCanvas'
import type { ClipInfo, FullGameStatus, MapMoment, MatchTrack, VodStatus } from '../types'

const SPEEDS = [4, 8]
type View = 'map' | 'footage' | 'clip'
type Filter = 'missed' | 'mine' | 'objectives' | 'all'
const FILTERS: { key: Filter; label: string; test: (m: MapMoment) => boolean }[] = [
  { key: 'missed', label: 'Fights without you', test: m => m.kind === 'fight' && !!m.withoutMe },
  { key: 'mine', label: 'Your fights', test: m => (m.kind === 'fight' && !m.withoutMe) || m.kind === 'kill' || m.kind === 'death' },
  { key: 'objectives', label: 'Objectives', test: m => m.kind === 'objective' },
  { key: 'all', label: 'All', test: () => true },
]

export interface StageJump { timeSec: number; nonce: number }

// One list of moments, three ways to look at each: the map drawn from the
// timeline, the footage if any exists, the rendered clip if one covers it.
// Every clock on the page lands here through jumpTo.
export default function MatchStage({ matchId, track, moments, durationSec, vod, onVodChange, fullGame, onFullGameChange, clips, onClipsChange, canManage, jumpTo }: {
  matchId: string
  track: MatchTrack | null
  moments: MapMoment[]
  durationSec: number
  vod: VodStatus | null
  onVodChange: (v: VodStatus) => void
  fullGame: FullGameStatus | null
  onFullGameChange: (f: FullGameStatus | null) => void
  clips: ClipInfo[]
  onClipsChange: (c: ClipInfo[]) => void
  canManage: boolean
  jumpTo: StageJump | null
}) {
  const source = footageSource(vod, fullGame)
  const hasFootage = source === 'recorded' || source === 'youtube' || source === 'render'
  const readyClips = clips.filter(c => c.ready).length
  const [view, setView] = useState<View>(() => (track ? 'map' : hasFootage ? 'footage' : 'clip'))
  const [filter, setFilter] = useState<Filter>(() => (moments.some(FILTERS[0].test) ? 'missed' : 'all'))
  const [selected, setSelected] = useState(() => defaultMoment(moments))
  const [adhoc, setAdhoc] = useState<MapMoment | null>(null)
  const moment = adhoc ?? moments[selected] ?? null
  const win = useMemo(() => (moment ? windowFor(moment, durationSec) : { start: 0, end: durationSec }), [moment, durationSec])
  // At rest the map shows the moment itself; play starts from the approach.
  const [t, setT] = useState(() => moment?.timeSec ?? 0)
  const [playing, setPlaying] = useState(false)
  const [speed, setSpeed] = useState(SPEEDS[0])
  const [seekKey, setSeekKey] = useState(0)
  const resting = useRef(true)
  const root = useRef<HTMLDivElement>(null)

  const open = useCallback((m: MapMoment, idx: number | null) => {
    resting.current = false
    setAdhoc(idx === null ? m : null)
    if (idx !== null) setSelected(idx)
    setT(windowFor(m, durationSec).start)
    setPlaying(true)
    setSeekKey(k => k + 1)
    const url = new URL(window.location.href)
    url.searchParams.set('t', String(m.timeSec))
    window.history.replaceState(null, '', url)
  }, [durationSec])

  const jump = useCallback((timeSec: number) => {
    const idx = moments.findIndex(m => Math.abs(m.timeSec - timeSec) <= 2)
    if (idx >= 0) {
      setFilter(current => (FILTERS.find(f => f.key === current)?.test(moments[idx]) ? current : 'all'))
      open(moments[idx], idx)
    } else {
      open({ kind: 'kill', timeSec, label: `the moment at ${clock(timeSec)}`, tone: 'neutral' }, null)
    }
    root.current?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  }, [moments, open])

  useEffect(() => { if (jumpTo) jump(jumpTo.timeSec) }, [jumpTo]) // eslint-disable-line react-hooks/exhaustive-deps

  // A shared link with ?t= opens on that moment, parked, not playing.
  useEffect(() => {
    const t0 = Number(new URLSearchParams(window.location.search).get('t'))
    if (!Number.isFinite(t0) || t0 <= 0) return
    const idx = moments.findIndex(m => Math.abs(m.timeSec - t0) <= 2)
    if (idx >= 0) { setSelected(idx); setT(moments[idx].timeSec) }
    else { setAdhoc({ kind: 'kill', timeSec: t0, label: `the moment at ${clock(t0)}`, tone: 'neutral' }); setT(t0) }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!playing || view !== 'map') return
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
  }, [playing, speed, win.end, view])

  useEffect(() => {
    if (playing && t >= win.end) setPlaying(false)
  }, [playing, t, win.end])

  const counts = useMemo(() => FILTERS.map(f => moments.filter(f.test).length), [moments])
  const shown = FILTERS.find(f => f.key === filter) ?? FILTERS[3]
  const clipHere = moment ? clipFor(clips, moment.timeSec) : null

  if (!track && !hasFootage && clips.length === 0 && source !== 'pending') return null

  const sourceWord = source === 'recorded' ? 'recorded' : source === 'youtube' ? 'YouTube' : source === 'render' ? 'render' : source === 'pending' ? 'uploading' : 'none'

  return (
    <div className="card stage" ref={root}>
      <div className="stage-main">
        <div className="stage-tabs" role="tablist" aria-label="Viewer">
          {track && (
            <button type="button" role="tab" aria-selected={view === 'map'} className={`stage-tab${view === 'map' ? ' active' : ''}`} onClick={() => setView('map')}>Map</button>
          )}
          <button type="button" role="tab" aria-selected={view === 'footage'} className={`stage-tab${view === 'footage' ? ' active' : ''}`} onClick={() => setView('footage')}>
            Footage <span className="mut">· {sourceWord}</span>
          </button>
          <button type="button" role="tab" aria-selected={view === 'clip'} className={`stage-tab${view === 'clip' ? ' active' : ''}`} onClick={() => setView('clip')}>
            Clip <span className="mut">· {clipHere ? 'ready' : readyClips > 0 ? `${readyClips} elsewhere` : clips.length > 0 ? 'queued' : 'none'}</span>
          </button>
        </div>

        {view === 'map' && track && (
          <>
            <div className="map-stage"><MapCanvas track={track} t={t} label={moment?.label} /></div>
            <div className="map-controls">
              <button type="button" className="action primary" onClick={() => {
                if (!playing && (resting.current || t >= win.end - 0.01)) setT(win.start)
                resting.current = false
                setPlaying(p => !p)
              }}>{playing ? 'Pause' : 'Play'}</button>
              <span className="map-clock">{clock(t)}</span>
              <input type="range" min={win.start} max={win.end} step={0.25} value={t} aria-label="Game clock"
                onChange={e => { resting.current = false; setPlaying(false); setT(Number(e.target.value)) }} />
              <span className="map-speeds">
                {SPEEDS.map(s => (
                  <button key={s} type="button" className={`action${speed === s ? ' primary' : ''}`} onClick={() => setSpeed(s)}>{s}×</button>
                ))}
              </span>
            </div>
          </>
        )}
        {view === 'footage' && (
          <FootageView matchId={matchId} vod={vod} onVodChange={onVodChange} fullGame={fullGame} onFullGameChange={onFullGameChange}
            canManage={canManage} moment={moment} seekKey={seekKey} />
        )}
        {view === 'clip' && (
          <ClipView matchId={matchId} clips={clips} onClipsChange={onClipsChange} canManage={canManage} moment={moment} seekKey={seekKey} onJump={jump} />
        )}

        {moment && (
          <div className="map-now">
            <span className={`map-chip-time ${moment.tone ?? 'neutral'}`}>{clock(moment.timeSec)}</span> {moment.label}
            {moment.withoutMe && <span className="map-chip-flag"> · without you</span>}
          </div>
        )}
        {view === 'map' && (
          <p className="mut sm-text map-caption">
            Positions are Riot's 60-second samples, moved in straight lines between them; kills and objectives sit where they
            happened. A champion stays at their death spot until the first sample that shows them elsewhere.
          </p>
        )}
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
                className={`map-chip ${m.tone ?? 'neutral'}${i === selected && !adhoc ? ' active' : ''}`} onClick={() => open(m, i)}>
                <span className="map-chip-time">{clock(m.timeSec)}</span>
                <span className="map-chip-label">{m.label}</span>
                {clipFor(clips, m.timeSec) && <span className="map-chip-clip" title="A rendered clip covers this">▶</span>}
                {m.withoutMe && filter !== 'missed' && <span className="map-chip-flag">without you</span>}
              </button>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

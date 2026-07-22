import { useEffect, useMemo, useRef, useState } from 'react'
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api } from '../api'
import type { ClipEvent, DeathEvent, VodStatus } from '../types'

const fmtClock = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`

interface ApmTooltipProps {
  active?: boolean
  payload?: Array<{ payload: { gameClock: string; apm: number } }>
}

function ApmTooltip({ active, payload }: ApmTooltipProps) {
  if (!active || !payload?.length) return null
  const p = payload[0].payload
  return (
    <div className="viz-tooltip">
      <div className="v">{p.apm} APM</div>
      <div className="l">{p.gameClock} · click to jump</div>
    </div>
  )
}

/// The live-recorded VOD of this game (what was on the monitor, recorded by
/// the agent while playing), with the game's kill/death moments as jump
/// markers and the input telemetry as an APM line - both seek the video.
/// Renders nothing when no VOD exists for the match.
export default function VodReview({ matchId, moments, deaths = [] }: { matchId: string; moments: ClipEvent[]; deaths?: DeathEvent[] }) {
  const [vod, setVod] = useState<VodStatus | null>(null)
  const [duration, setDuration] = useState<number | null>(null)
  const videoRef = useRef<HTMLVideoElement | null>(null)

  useEffect(() => {
    api.vodStatus(matchId).then(setVod).catch(() => setVod(null))
  }, [matchId])

  // The recording starts at the loading screen, so video time and the game
  // clock differ by a constant the sidecar sampled while recording. Median
  // over the samples shrugs off any one bad read.
  const gameToVideoOffset = useMemo(() => {
    const pairs = vod?.meta?.clockMap ?? []
    if (pairs.length === 0) return null
    const offsets = pairs.map(p => p.gameSec - p.videoSec).sort((a, b) => a - b)
    return offsets[Math.floor(offsets.length / 2)]
  }, [vod])

  if (!vod?.exists) return null

  const videoFor = (gameSec: number) =>
    gameToVideoOffset === null ? null : Math.max(0, gameSec - gameToVideoOffset)

  const seekTo = (videoSec: number) => {
    const el = videoRef.current
    if (!el) return
    el.currentTime = videoSec
    void el.play()
  }

  const jumpToMoment = (gameSec: number) => {
    const v = videoFor(gameSec)
    if (v !== null) seekTo(Math.max(0, v - 5)) // 5s of approach context
  }

  const apmData = (vod.apm?.apm ?? []).map((apm, i) => {
    const videoSec = i * (vod.apm?.bucketSec ?? 10)
    return {
      videoSec,
      apm,
      gameClock: gameToVideoOffset === null ? fmtClock(videoSec) : fmtClock(videoSec + gameToVideoOffset),
    }
  })

  // Clip windows carry my kills AND deaths; matches without a clip plan
  // still have the death analytics to mark.
  const allMoments: ClipEvent[] = moments.length > 0
    ? moments
    : deaths.map(d => ({ kind: 'death', timeSec: d.timeSec }))

  const markers = duration === null || gameToVideoOffset === null
    ? []
    : allMoments
        .map(e => ({ ...e, videoSec: videoFor(e.timeSec) }))
        .filter((e): e is ClipEvent & { videoSec: number } => e.videoSec !== null && e.videoSec <= duration)

  return (
    <div className="card" style={{ marginBottom: 14 }}>
      <h2>
        Your VOD <span className="mut" style={{ fontWeight: 400 }}>— the game as you played it, recorded live with your inputs</span>
      </h2>
      <video
        ref={videoRef}
        src={`/api/matches/${matchId}/vod`}
        poster={`/api/matches/${matchId}/vod/thumb`}
        controls
        preload="metadata"
        onLoadedMetadata={e => setDuration(e.currentTarget.duration)}
        style={{ width: '100%', maxWidth: 960, borderRadius: 8, background: '#000' }}
      />

      {markers.length > 0 && duration !== null && (
        <div style={{ position: 'relative', height: 22, maxWidth: 960, margin: '6px 0 0' }} aria-label="Moments">
          {markers.map((e, i) => (
            <button
              key={i}
              className="action"
              title={`${e.kind} at ${fmtClock(e.timeSec)} — click to watch`}
              onClick={() => jumpToMoment(e.timeSec)}
              style={{
                position: 'absolute',
                left: `${(e.videoSec / duration) * 100}%`,
                transform: 'translateX(-50%)',
                padding: '0 4px',
                lineHeight: '20px',
                color: e.kind === 'death' ? 'var(--loss, #e5484d)' : 'var(--win, #30a46c)',
              }}
            >
              {e.kind === 'death' ? '✖' : '⚔'}
            </button>
          ))}
        </div>
      )}

      {apmData.length > 1 && (
        <div style={{ maxWidth: 960 }}>
          <div className="sub-h" style={{ marginBottom: 0 }}>
            Actions per minute <span className="mut">· average {vod.apm?.averageApm} · click the line to jump the video</span>
          </div>
          <ResponsiveContainer width="100%" height={90}>
            <AreaChart
              data={apmData}
              margin={{ top: 4, right: 12, bottom: 0, left: 8 }}
              onClick={state => {
                // recharts' click-state typings lag its runtime shape.
                const index = (state as { activeTooltipIndex?: number } | undefined)?.activeTooltipIndex
                if (index !== undefined && apmData[index]) seekTo(apmData[index].videoSec)
              }}
            >
              <defs>
                <linearGradient id="apmFill" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor="var(--series-1)" stopOpacity={0.22} />
                  <stop offset="100%" stopColor="var(--series-1)" stopOpacity={0.02} />
                </linearGradient>
              </defs>
              <XAxis dataKey="gameClock" tick={{ fill: 'var(--muted)', fontSize: 11 }} stroke="var(--baseline)" tickLine={false} minTickGap={60} />
              <YAxis hide domain={[0, 'dataMax']} />
              <Tooltip content={<ApmTooltip />} cursor={{ stroke: 'var(--baseline)', strokeWidth: 1 }} />
              <Area type="monotone" dataKey="apm" stroke="var(--series-1)" strokeWidth={2} fill="url(#apmFill)"
                strokeLinejoin="round" strokeLinecap="round" isAnimationActive={false} />
            </AreaChart>
          </ResponsiveContainer>
        </div>
      )}

      <p className="mut sm-text" style={{ margin: '8px 0 0' }}>
        {vod.sizeMb} MB · {vod.meta?.width}×{vod.meta?.height}@{vod.meta?.fps} ({vod.meta?.encoder})
        {vod.meta?.activePlayer && <> · played as {vod.meta.activePlayer}</>}
        {' · '}
        <button
          className="action"
          style={{ padding: '0 8px' }}
          onClick={() => {
            if (window.confirm('Delete this VOD from the tracker? The recording on the gaming PC is kept.')) {
              void api.deleteVod(matchId).then(() => setVod({ exists: false, sizeMb: null, meta: null, apm: null }))
            }
          }}
        >
          delete
        </button>
      </p>
    </div>
  )
}

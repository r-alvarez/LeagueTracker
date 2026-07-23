import { useMemo, useRef, useState } from 'react'
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api } from '../api'
import type { ClipEvent, DeathEvent, VodStatus } from '../types'

const fmtClock = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`

const youtubeId = (url: string) => /(?:youtu\.be\/|[?&]v=|shorts\/)([A-Za-z0-9_-]{11})/.exec(url)?.[1] ?? null

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

/// The game as it was played, reviewed in place: video (tracker-hosted mp4
/// OR the player's own YouTube upload - the storage-free mode), kill/death
/// jump markers mapped through the recording's clock map, and the input
/// telemetry as a clickable APM line. Renders nothing when the match has no
/// recording data at all.
export default function VodReview({ matchId, vod, onChange, moments, deaths = [] }: {
  matchId: string
  vod: VodStatus | null
  onChange: (v: VodStatus) => void
  moments: ClipEvent[]
  deaths?: DeathEvent[]
}) {
  const [duration, setDuration] = useState<number | null>(null)
  const [linkDraft, setLinkDraft] = useState('')
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const youtubeRef = useRef<HTMLIFrameElement | null>(null)

  const gameToVideoOffset = useMemo(() => {
    const pairs = vod?.meta?.clockMap ?? []
    if (pairs.length === 0) return null
    const offsets = pairs.map(p => p.gameSec - p.videoSec).sort((a, b) => a - b)
    return offsets[Math.floor(offsets.length / 2)]
  }, [vod])

  if (!vod || (!vod.exists && !vod.youtubeUrl && !vod.meta && !vod.apm)) return null

  const ytId = vod.youtubeUrl ? youtubeId(vod.youtubeUrl) : null
  const hasHostedVideo = vod.exists

  // Without a loaded <video> element (YouTube mode) the recording length
  // comes from the sidecar's own start/end stamps.
  const metaDuration = vod.meta
    ? (new Date(vod.meta.recordingEndUtc).getTime() - new Date(vod.meta.recordingStartUtc).getTime()) / 1000
    : null
  const effectiveDuration = duration ?? metaDuration

  const seekTo = (videoSec: number) => {
    if (hasHostedVideo && videoRef.current) {
      videoRef.current.currentTime = videoSec
      void videoRef.current.play()
      return
    }
    // YouTube's iframe accepts player commands over postMessage when the
    // embed src carries enablejsapi=1 - no SDK script needed for seek/play.
    const target = youtubeRef.current?.contentWindow
    if (!target) return
    for (const func of [['seekTo', [videoSec, true]], ['playVideo', []]] as Array<[string, unknown[]]>) {
      target.postMessage(JSON.stringify({ event: 'command', func: func[0], args: func[1] }), 'https://www.youtube.com')
    }
  }

  const videoFor = (gameSec: number) =>
    gameToVideoOffset === null ? null : Math.max(0, gameSec - gameToVideoOffset)

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

  const allMoments: ClipEvent[] = moments.length > 0
    ? moments
    : deaths.map(d => ({ kind: 'death', timeSec: d.timeSec }))

  const markers = effectiveDuration === null || gameToVideoOffset === null
    ? []
    : allMoments
        .map(e => ({ ...e, videoSec: videoFor(e.timeSec) }))
        .filter((e): e is ClipEvent & { videoSec: number } => e.videoSec !== null && e.videoSec <= effectiveDuration)

  const saveLink = (url: string) => {
    void api.setVodLink(matchId, url).then(status => { onChange(status); setLinkDraft('') })
  }

  const sortedMoments = [...allMoments].sort((a, b) => a.timeSec - b.timeSec)

  return (
    <div className="card" style={{ marginBottom: 14 }}>
      <h2>
        Your VOD <span className="mut" style={{ fontWeight: 400 }}>— the game as you played it, recorded live with your inputs</span>
      </h2>

      {/* Video column + review sidebar; wraps to one column when narrow. */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 16, alignItems: 'flex-start' }}>
        <div style={{ flex: '2 1 560px', minWidth: 0 }}>
          {hasHostedVideo ? (
            <video
              ref={videoRef}
              src={`/api/matches/${matchId}/vod`}
              poster={`/api/matches/${matchId}/vod/thumb`}
              controls
              preload="metadata"
              onLoadedMetadata={e => setDuration(e.currentTarget.duration)}
              style={{ width: '100%', borderRadius: 8, background: '#000' }}
            />
          ) : ytId ? (
            <iframe
              ref={youtubeRef}
              src={`https://www.youtube.com/embed/${ytId}?enablejsapi=1&origin=${encodeURIComponent(window.location.origin)}`}
              title="Game VOD on YouTube"
              allow="autoplay; encrypted-media; picture-in-picture"
              allowFullScreen
              style={{ width: '100%', aspectRatio: '16 / 9', border: 0, borderRadius: 8, background: '#000' }}
            />
          ) : (
            <div className="empty" style={{ aspectRatio: '16 / 9', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              This game was recorded — paste its YouTube link to review it here with jump markers.
            </div>
          )}

          {/* The marker strip and APM line share the video's width, so a
              marker's horizontal position IS its place in the video. */}
          {markers.length > 0 && effectiveDuration !== null && (ytId || hasHostedVideo) && (
            <div style={{ position: 'relative', height: 22, margin: '6px 0 0' }} aria-label="Moments">
              {markers.map((e, i) => (
                <button
                  key={i}
                  className="action"
                  title={`${e.kind} at ${fmtClock(e.timeSec)} — click to watch`}
                  onClick={() => jumpToMoment(e.timeSec)}
                  style={{
                    position: 'absolute',
                    left: `${(e.videoSec / effectiveDuration) * 100}%`,
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
            <div>
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
        </div>

        <aside style={{ flex: '1 1 260px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: 10 }}>
          {!hasHostedVideo && (
            <div>
              <div className="sub-h" style={{ marginTop: 0 }}>{vod.youtubeUrl ? 'YouTube link' : 'Link this game'}</div>
              <div style={{ display: 'flex', gap: 6 }}>
                <input
                  value={linkDraft}
                  onChange={e => setLinkDraft(e.target.value)}
                  placeholder={vod.youtubeUrl ? 'Replace the link…' : 'https://youtu.be/…'}
                  style={{ flex: 1, minWidth: 0 }}
                />
                <button className="action" disabled={!linkDraft.trim()} onClick={() => saveLink(linkDraft.trim())}>
                  {vod.youtubeUrl ? 'replace' : 'link'}
                </button>
                {vod.youtubeUrl && <button className="action" onClick={() => saveLink('')}>unlink</button>}
              </div>
            </div>
          )}

          {sortedMoments.length > 0 && (ytId || hasHostedVideo) && (
            <div>
              <div className="sub-h" style={{ marginTop: 0 }}>Moments</div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 2, maxHeight: 340, overflowY: 'auto' }}>
                {sortedMoments.map((e, i) => (
                  <button
                    key={i}
                    className="action"
                    onClick={() => jumpToMoment(e.timeSec)}
                    style={{ display: 'flex', justifyContent: 'space-between', gap: 8, textAlign: 'left', padding: '2px 8px' }}
                  >
                    <span style={{ color: e.kind === 'death' ? 'var(--loss, #e5484d)' : 'var(--win, #30a46c)' }}>
                      {e.kind === 'death' ? '✖ death' : `⚔ ${e.kind}`}
                    </span>
                    <span className="mut">{fmtClock(e.timeSec)}</span>
                  </button>
                ))}
              </div>
            </div>
          )}

          <p className="mut sm-text" style={{ margin: 0 }}>
            {vod.meta && <>{vod.meta.width}×{vod.meta.height}@{vod.meta.fps} ({vod.meta.encoder})<br /></>}
            {vod.sizeMb !== null && vod.sizeMb !== undefined && <>{vod.sizeMb} MB on the tracker<br /></>}
            {vod.meta?.activePlayer && <>played as {vod.meta.activePlayer}</>}
          </p>
          {hasHostedVideo && (
            <button
              className="action"
              style={{ alignSelf: 'flex-start', padding: '0 8px' }}
              onClick={() => {
                if (window.confirm('Delete this VOD from the tracker? The recording on the gaming PC is kept.')) {
                  void api.deleteVod(matchId).then(() => api.vodStatus(matchId).then(onChange))
                }
              }}
            >
              delete
            </button>
          )}
        </aside>
      </div>
    </div>
  )
}

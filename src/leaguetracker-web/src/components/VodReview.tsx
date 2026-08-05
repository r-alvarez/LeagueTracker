import { useEffect, useMemo, useRef, useState } from 'react'
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api } from '../api'
import type { DeathEvent, VodMoment, VodStatus } from '../types'

const fmtClock = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`

const momentGlyph = (e: VodMoment) => (e.kind === 'death' ? '✖' : e.kind === 'fight' ? '⚡' : '⚔')

// Kills read as wins, deaths as losses; fights carry their own tone (a drawn
// 3v3 is neither) so the strip stays honest about how each fight went.
const momentColor = (e: VodMoment) =>
  e.kind === 'death' || e.tone === 'loss' ? 'var(--loss, #e5484d)'
  : e.tone === 'neutral' ? 'var(--muted, #9aa4af)'
  : 'var(--win, #30a46c)'

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
/// OR the player's own YouTube upload - the storage-free mode), kill/death/
/// fight jump markers mapped through the recording's clock map, and the
/// input telemetry as a clickable APM line. Renders nothing when the match
/// has no recording data at all.
export default function VodReview({ matchId, vod, onChange, moments, deaths = [] }: {
  matchId: string
  vod: VodStatus | null
  onChange: (v: VodStatus) => void
  moments: VodMoment[]
  deaths?: DeathEvent[]
}) {
  const [duration, setDuration] = useState<number | null>(null)
  const [linkDraft, setLinkDraft] = useState('')
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const youtubeRef = useRef<HTMLIFrameElement | null>(null)
  const ytPlayerRef = useRef<{ seekTo: (s: number, allowAhead: boolean) => void; playVideo: () => void } | null>(null)

  // Piecewise-linear mapping over the sampled (videoSec, gameSec) pairs. A
  // capture restart leaves a gap in the video while the game clock runs on,
  // so the two sides of a seam sit at different offsets — one constant (the
  // old median) put every marker after a seam in the wrong place. Both
  // coordinates only ever increase, so one sort serves both directions;
  // outside the sampled range the clocks advance in lockstep (slope 1).
  const clockPairs = useMemo(() => {
    const pairs = (vod?.meta?.clockMap ?? []).filter(p => Number.isFinite(p.videoSec) && Number.isFinite(p.gameSec))
    return [...pairs].sort((a, b) => a.videoSec - b.videoSec)
  }, [vod])

  const interpolate = (from: 'videoSec' | 'gameSec', to: 'videoSec' | 'gameSec', x: number): number | null => {
    if (clockPairs.length === 0) return null
    const first = clockPairs[0]
    const last = clockPairs[clockPairs.length - 1]
    if (x <= first[from]) return first[to] + (x - first[from])
    if (x >= last[from]) return last[to] + (x - last[from])
    const upper = clockPairs.findIndex(p => p[from] >= x)
    const lo = clockPairs[upper - 1]
    const hi = clockPairs[upper]
    const span = hi[from] - lo[from]
    if (span <= 0) return lo[to] + (x - lo[from])
    return lo[to] + ((x - lo[from]) / span) * (hi[to] - lo[to])
  }

  const ytId = vod?.youtubeUrl ? youtubeId(vod.youtubeUrl) : null
  const hasHostedVideo = vod?.exists ?? false

  // Raw postMessage commands are ignored until YouTube's API handshake has
  // happened - the official iframe_api script does it and hands back a
  // player whose seekTo/playVideo actually work.
  useEffect(() => {
    if (!ytId || hasHostedVideo) return
    let cancelled = false
    const w = window as unknown as { YT?: { Player: new (el: HTMLIFrameElement) => unknown }; onYouTubeIframeAPIReady?: () => void }
    const create = () => {
      if (cancelled || !youtubeRef.current || !w.YT) return
      ytPlayerRef.current = new w.YT.Player(youtubeRef.current) as typeof ytPlayerRef.current
    }
    if (w.YT?.Player) {
      create()
    } else {
      const previous = w.onYouTubeIframeAPIReady
      w.onYouTubeIframeAPIReady = () => { previous?.(); create() }
      if (!document.querySelector('script[src*="youtube.com/iframe_api"]')) {
        const script = document.createElement('script')
        script.src = 'https://www.youtube.com/iframe_api'
        document.head.appendChild(script)
      }
    }
    return () => { cancelled = true; ytPlayerRef.current = null }
  }, [ytId, hasHostedVideo])

  if (!vod) return null
  // No recording data at all: the game can still have a YouTube upload (played
  // on another machine, recorded by hand) - offer just the link box, and the
  // full review card takes over once a link is saved.
  const hasAnyData = vod.exists || !!vod.youtubeUrl || !!vod.meta || !!vod.apm

  // Without a loaded <video> element (YouTube mode) the recording length
  // comes from the sidecar. Segment durations are ffprobe-exact and skip the
  // dead time between capture restarts; the wall-clock span (which counts
  // those gaps as footage) is only for sidecars from before segmented
  // recording.
  const metaDuration = vod.meta
    ? (vod.meta.segments && vod.meta.segments.length > 0
        ? vod.meta.segments.reduce((sum, s) => sum + s.videoSec, 0)
        : (new Date(vod.meta.recordingEndUtc).getTime() - new Date(vod.meta.recordingStartUtc).getTime()) / 1000)
    : null
  const effectiveDuration = duration ?? metaDuration

  const seekTo = (videoSec: number) => {
    if (hasHostedVideo && videoRef.current) {
      videoRef.current.currentTime = videoSec
      void videoRef.current.play()
      return
    }
    const player = ytPlayerRef.current
    if (player?.seekTo) {
      player.seekTo(videoSec, true)
      player.playVideo()
    }
  }

  // Without a recording clock map (hand-linked YouTube upload) assume the
  // video starts at game clock 0:00 - approximate jumps beat dead buttons.
  const videoFor = (gameSec: number) => Math.max(0, interpolate('gameSec', 'videoSec', gameSec) ?? gameSec)

  const jumpToMoment = (gameSec: number) => seekTo(Math.max(0, videoFor(gameSec) - 5)) // 5s of approach context

  const apmData = (vod.apm?.apm ?? []).map((apm, i) => {
    const videoSec = i * (vod.apm?.bucketSec ?? 10)
    return {
      videoSec,
      apm,
      gameClock: fmtClock(interpolate('videoSec', 'gameSec', videoSec) ?? videoSec),
    }
  })

  const allMoments: VodMoment[] = moments.length > 0
    ? moments
    : deaths.map(d => ({ kind: 'death', timeSec: d.timeSec }))

  const markers = effectiveDuration === null
    ? []
    : allMoments
        .map(e => ({ ...e, videoSec: videoFor(e.timeSec) }))
        .filter(e => e.videoSec <= effectiveDuration)

  const saveLink = (url: string) => {
    void api.setVodLink(matchId, url).then(status => { onChange(status); setLinkDraft('') })
  }

  const sortedMoments = [...allMoments].sort((a, b) => a.timeSec - b.timeSec)

  if (!hasAnyData) {
    return (
      <div className="card" style={{ marginBottom: 14 }}>
        <h2>
          Your VOD <span className="mut" style={{ fontWeight: 400 }}>— have this game on YouTube? Link it to review it here</span>
        </h2>
        <div style={{ display: 'flex', gap: 6, maxWidth: 480 }}>
          <input
            value={linkDraft}
            onChange={e => setLinkDraft(e.target.value)}
            placeholder="https://youtu.be/…"
            style={{ flex: 1, minWidth: 0 }}
          />
          <button className="action" disabled={!linkDraft.trim()} onClick={() => saveLink(linkDraft.trim())}>link</button>
        </div>
      </div>
    )
  }

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
              src={`https://www.youtube.com/embed/${ytId}?enablejsapi=1&rel=0&playsinline=1&origin=${encodeURIComponent(window.location.origin)}`}
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
                  title={`${e.label ?? e.kind} at ${fmtClock(e.timeSec)} — click to watch`}
                  onClick={() => jumpToMoment(e.timeSec)}
                  style={{
                    position: 'absolute',
                    left: `${(e.videoSec / effectiveDuration) * 100}%`,
                    transform: 'translateX(-50%)',
                    padding: '0 4px',
                    lineHeight: '20px',
                    color: momentColor(e),
                  }}
                >
                  {momentGlyph(e)}
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
                    <span style={{ color: momentColor(e) }}>
                      {momentGlyph(e)} {e.label ?? e.kind}
                    </span>
                    <span className="mut">{fmtClock(e.timeSec)}</span>
                  </button>
                ))}
              </div>
              {clockPairs.length === 0 && (
                <p className="mut sm-text" style={{ margin: '6px 0 0' }}>
                  No recording clock map for this game — jumps assume the video starts at the game's 0:00.
                </p>
              )}
            </div>
          )}

          {vod.sizeMb !== null && vod.sizeMb !== undefined && (
            <p className="mut sm-text" style={{ margin: 0 }}>{vod.sizeMb} MB on the tracker</p>
          )}
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

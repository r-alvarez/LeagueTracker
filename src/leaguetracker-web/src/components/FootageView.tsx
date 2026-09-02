import { useEffect, useMemo, useRef, useState } from 'react'
import { account } from '../account'
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api } from '../api'
import { clock } from './TimeLink'
import type { FullGameStatus, MapMoment, VodStatus } from '../types'

const youtubeId = (url: string) => /(?:youtu\.be\/|[?&]v=|shorts\/)([A-Za-z0-9_-]{11})/.exec(url)?.[1] ?? null

export type FootageSource = 'recorded' | 'youtube' | 'render' | 'pending' | 'none'

// What the Footage tab can show, in the order it prefers: the tracker's own
// mp4, the player's YouTube upload, the replay-rendered full game.
export function footageSource(vod: VodStatus | null, fullGame: FullGameStatus | null): FootageSource {
  if (vod?.exists) return 'recorded'
  if (vod?.youtubeUrl && youtubeId(vod.youtubeUrl)) return 'youtube'
  if (fullGame?.state === 'done') return 'render'
  if (vod?.meta) return 'pending'
  return 'none'
}

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

// The game as it was played: the recording, a hand-linked YouTube upload,
// or the replay render, seeking to whatever moment the stage has selected
// through the recording's clock map. The APM line rides under it.
export default function FootageView({ matchId, vod, onVodChange, fullGame, onFullGameChange, canManage, moment, seekKey }: {
  matchId: string
  vod: VodStatus | null
  onVodChange: (v: VodStatus) => void
  fullGame: FullGameStatus | null
  onFullGameChange: (f: FullGameStatus | null) => void
  canManage: boolean
  moment: MapMoment | null
  seekKey: number
}) {
  const [linkDraft, setLinkDraft] = useState('')
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const youtubeRef = useRef<HTMLIFrameElement | null>(null)
  const ytPlayerRef = useRef<{ seekTo: (s: number, allowAhead: boolean) => void; playVideo: () => void } | null>(null)
  const source = footageSource(vod, fullGame)

  // Piecewise-linear mapping over the sampled (videoSec, gameSec) pairs. A
  // capture restart leaves a gap in the video while the game clock runs on,
  // so the two sides of a seam sit at different offsets. Outside the sampled
  // range the clocks advance in lockstep.
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

  // Raw postMessage commands are ignored until YouTube's API handshake has
  // happened - the official iframe_api script does it and hands back a
  // player whose seekTo/playVideo actually work.
  useEffect(() => {
    if (source !== 'youtube' || !ytId) return
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
  }, [source, ytId])

  // Without a clock map (a hand-linked upload, or the replay render that
  // starts at the game's 0:00) assume video time is game time.
  const videoFor = (gameSec: number) => Math.max(0, interpolate('gameSec', 'videoSec', gameSec) ?? gameSec)

  const seekTo = (videoSec: number) => {
    if (videoRef.current) {
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

  // Five seconds of approach before the moment.
  useEffect(() => {
    if (!moment || seekKey === 0) return
    seekTo(Math.max(0, videoFor(moment.timeSec) - 5))
    // seekKey is the trigger; the rest is read at that instant.
  }, [seekKey]) // eslint-disable-line react-hooks/exhaustive-deps

  const apmData = (vod?.apm?.apm ?? []).map((apm, i) => {
    const videoSec = i * (vod?.apm?.bucketSec ?? 10)
    return { videoSec, apm, gameClock: clock(interpolate('videoSec', 'gameSec', videoSec) ?? videoSec) }
  })

  const saveLink = (url: string) => {
    void api.setVodLink(matchId, url).then(status => { onVodChange(status); setLinkDraft('') })
  }

  return (
    <div className="footage">
      {source === 'recorded' && (
        <video ref={videoRef} src={account.apiUrl(`/api/matches/${matchId}/vod`)} poster={account.apiUrl(`/api/matches/${matchId}/vod/thumb`)}
          controls preload="metadata" className="footage-video" />
      )}
      {source === 'youtube' && ytId && (
        <iframe ref={youtubeRef}
          src={`https://www.youtube.com/embed/${ytId}?enablejsapi=1&rel=0&playsinline=1&origin=${encodeURIComponent(window.location.origin)}`}
          title="Game VOD on YouTube" allow="autoplay; encrypted-media; picture-in-picture" allowFullScreen className="footage-video footage-frame" />
      )}
      {source === 'render' && (
        <video ref={videoRef} src={account.apiUrl(`/api/matches/${matchId}/fullgame`)} controls preload="metadata" className="footage-video" />
      )}
      {source === 'pending' && (
        <div className="stage-placeholder">
          <b>Recorded — the video is on its way.</b>
          It uploads from the player's PC (paced while a game runs) and appears here once processed; the APM line is already in.
          Uploaded it yourself? Paste the link below.
        </div>
      )}
      {source === 'none' && (
        <div className="stage-placeholder">
          <b>No footage for this game.</b>
          {fullGame?.state === 'requested' && 'A full-game render is queued — waiting for the render agent.'}
          {fullGame?.state === 'rendering' && 'Rendering the full game now on the render box…'}
          {fullGame?.state === 'failed' && <span><span className="loss">Render failed:</span> {fullGame.error}</span>}
          {(!fullGame || fullGame.state === 'none') && 'With the agent on the gaming PC, the recording lands here and seeks to the moment; a YouTube upload linked below works the same way. The map never needs it.'}
        </div>
      )}

      {apmData.length > 1 && (
        <div className="footage-apm">
          <div className="sub-h" style={{ marginBottom: 0 }}>
            Actions per minute <span className="mut">· average {vod?.apm?.averageApm} · click the line to jump</span>
          </div>
          <ResponsiveContainer width="100%" height={80}>
            <AreaChart data={apmData} margin={{ top: 4, right: 12, bottom: 0, left: 8 }}
              onClick={state => {
                // recharts' click-state typings lag its runtime shape.
                const index = (state as { activeTooltipIndex?: number } | undefined)?.activeTooltipIndex
                if (index !== undefined && apmData[index]) seekTo(apmData[index].videoSec)
              }}>
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

      <div className="footage-foot mut sm-text">
        {source === 'recorded' && vod?.sizeMb != null && <span>{vod.sizeMb} MB on the tracker</span>}
        {source === 'youtube' && clockPairs.length === 0 && <span>No recording clock map — jumps assume the video starts at the game's 0:00.</span>}
        {source === 'render' && fullGame && (
          <span>
            Replay render · {fullGame.sizeMb} MB{fullGame.renderedUtc && ` · rendered ${new Date(fullGame.renderedUtc).toLocaleDateString()}`}
            {fullGame.keep ? ' · kept' : ' · auto-deleted after the retention window'} · jumps assume the render starts at 0:00
          </span>
        )}
        {canManage && (
          <span className="footage-actions">
            {source !== 'recorded' && (
              <>
                <input value={linkDraft} onChange={e => setLinkDraft(e.target.value)} placeholder={vod?.youtubeUrl ? 'Replace the YouTube link…' : 'https://youtu.be/…'} aria-label="YouTube link" />
                <button className="action" disabled={!linkDraft.trim()} onClick={() => saveLink(linkDraft.trim())}>{vod?.youtubeUrl ? 'replace' : 'link'}</button>
                {vod?.youtubeUrl && <button className="action" onClick={() => saveLink('')}>unlink</button>}
              </>
            )}
            {source === 'recorded' && (
              <button className="action" onClick={() => {
                if (window.confirm('Delete this VOD from the tracker? The recording on the gaming PC is kept.')) {
                  void api.deleteVod(matchId).then(() => api.vodStatus(matchId).then(onVodChange))
                }
              }}>delete</button>
            )}
            {source === 'render' && fullGame && (
              <>
                <button className="action" onClick={() => api.toggleFullGameKeep(matchId).then(onFullGameChange)}>{fullGame.keep ? 'unkeep' : 'keep'}</button>
                <button className="action" onClick={() => {
                  if (window.confirm('Delete this render? The replay may no longer be re-renderable on a newer patch.')) {
                    void api.deleteFullGame(matchId).then(() => api.fullGameStatus(matchId).then(onFullGameChange))
                  }
                }}>delete</button>
              </>
            )}
            {(source === 'none' || source === 'pending') && fullGame?.state === 'none' && (
              <button className="action" title="~500 MB and a real-time render on the render box — for games worth studying start to finish"
                onClick={() => api.requestFullGame(matchId).then(onFullGameChange)}>Render full game</button>
            )}
            {source === 'none' && fullGame?.state === 'failed' && (
              <button className="action" onClick={() => api.retryRender(matchId, 'full').then(() => api.fullGameStatus(matchId).then(onFullGameChange))}>Retry render</button>
            )}
          </span>
        )}
      </div>
    </div>
  )
}

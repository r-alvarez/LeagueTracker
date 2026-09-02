import { useEffect, useRef } from 'react'
import { api } from '../api'
import { TimeLink, clock, type Jump } from './TimeLink'
import type { ClipInfo, MapMoment } from '../types'

// The rendered clip that covers a moment, if the render box has made one.
export const clipFor = (clips: ClipInfo[], timeSec: number) =>
  clips.find(c => c.ready && timeSec >= c.startSec - 1 && timeSec <= c.endSec + 1) ?? null

export default function ClipView({ matchId, clips, onClipsChange, canManage, moment, seekKey, onJump }: {
  matchId: string
  clips: ClipInfo[]
  onClipsChange: (c: ClipInfo[]) => void
  canManage: boolean
  moment: MapMoment | null
  seekKey: number
  onJump: Jump
}) {
  const videoRef = useRef<HTMLVideoElement | null>(null)
  const clip = moment ? clipFor(clips, moment.timeSec) : null

  useEffect(() => {
    if (!clip || !moment || !videoRef.current || seekKey === 0) return
    videoRef.current.currentTime = Math.max(0, moment.timeSec - clip.startSec - 5)
    void videoRef.current.play()
    // seekKey is the trigger; the clip and moment are read at that instant.
  }, [seekKey, clip?.index]) // eslint-disable-line react-hooks/exhaustive-deps

  const ready = clips.filter(c => c.ready)
  const planned = clips.filter(c => !c.ready)

  if (!clip) {
    return (
      <div className="stage-placeholder">
        <b>{moment ? `No rendered clip covers ${clock(moment.timeSec)}.` : 'No rendered clips for this game.'}</b>
        {planned.length > 0 && <span>{planned.length} window{planned.length === 1 ? '' : 's'} planned — waiting for the render box. </span>}
        {ready.length > 0 && (
          <span>
            Clips exist for{' '}
            {ready.map((c, i) => <span key={c.index}>{i > 0 && ', '}<TimeLink t={c.startSec} onJump={onJump} /></span>)}.
          </span>
        )}
        {clips.length === 0 && 'The render box cuts clips from the official replay while it still plays on this patch; the map never needs them.'}
      </div>
    )
  }

  return (
    <div className="footage">
      <video key={clip.index} ref={videoRef} src={clip.url} controls preload="metadata" className="footage-video" />
      <div className="footage-foot mut sm-text">
        <span>
          {clip.label} · {clock(clip.startSec)}–{clock(clip.endSec)}
          {clip.kind === 'fight' && clip.cameraChampion ? ` · from ${clip.cameraChampion}'s view` : ' · rendered from the official replay'}
        </span>
        {canManage && (
          <span className="footage-actions">
            <button className="action" title="Delete this clip and queue just this window for a fresh render"
              onClick={() => {
                if (window.confirm('Delete this clip? The render agent will re-create it from the replay (needs the replay still playable on the current patch).')) {
                  void api.deleteClip(matchId, clip.index).then(() => api.clips(matchId).then(onClipsChange))
                }
              }}>✕ re-render</button>
          </span>
        )}
      </div>
    </div>
  )
}

import { useEffect, useRef, useState } from 'react'
import { useChampionIcons } from '../champions'
import TheaterToggle from './TheaterToggle'
import { clock } from './TimeLink'
import type { ClipInfo } from '../types'

// One player, however many clips: the card must not grow with the fight
// count (seven clips already pushed the scoreboard two screens down).
export default function ClipReel({ clips, title, hint, canManage, onDelete, theater, onToggleTheater, kept = false, onToggleKeep }: {
  clips: ClipInfo[]
  title: string
  hint: string
  canManage: boolean
  onDelete: (index: number) => void
  theater: boolean
  onToggleTheater: () => void
  kept?: boolean
  onToggleKeep?: () => void
}) {
  const icons = useChampionIcons()
  const ready = clips.filter(c => c.ready)
  const [current, setCurrent] = useState(() => ready[0]?.index ?? clips[0]?.index ?? 0)
  const [autoplay, setAutoplay] = useState(false)
  const strip = useRef<HTMLDivElement>(null)
  const clip = clips.find(c => c.index === current) ?? clips[0]

  useEffect(() => {
    strip.current?.querySelector<HTMLElement>('.reel-tile.active')?.scrollIntoView({ block: 'nearest', inline: 'center', behavior: 'smooth' })
  }, [current])

  const step = (dir: 1 | -1) => {
    const at = ready.findIndex(c => c.index === current)
    const next = ready[at + dir]
    if (next) { setCurrent(next.index); setAutoplay(true) }
  }

  if (!clip) return null
  const pending = clips.length - ready.length

  return (
    <div className={`card reel${theater ? ' theater' : ''}`} onKeyDown={e => { if (e.key === 'ArrowRight') step(1); if (e.key === 'ArrowLeft') step(-1) }}>
      <h2 className="reel-head">
        <span>{title} <span className="mut" style={{ fontWeight: 400 }}>— {hint}</span>{kept && <span className="mut" style={{ fontWeight: 400 }}> · kept</span>}</span>
        <span style={{ display: 'inline-flex', gap: 8 }}>
          {ready.length > 0 && canManage && onToggleKeep && (
            <button type="button" className="action sm-action" onClick={onToggleKeep}
              title={kept ? 'Let these clips expire with the patch window again' : 'Hold these clips past the patch window (counts against the kept allowance)'}>
              {kept ? '★ kept' : '☆ keep'}
            </button>
          )}
          {ready.length > 0 && <TheaterToggle on={theater} onToggle={onToggleTheater} what="the clip list" />}
        </span>
      </h2>
      {ready.length === 0 ? (
        <p className="mut" style={{ margin: 0 }}>{clips.length} fight window{clips.length === 1 ? '' : 's'} planned — waiting for the render agent on the gaming PC.</p>
      ) : (
        <div className="reel-body">
          <div className="reel-player">
            {clip.ready
              ? <video key={clip.url} src={clip.url} controls autoPlay={autoplay} preload="metadata" className="footage-video" onEnded={() => step(1)} />
              : <div className="stage-placeholder"><b>Queued for render.</b>This window is planned; the clip lands here once the render box has made it.</div>}
            <div className="reel-caption">
              <span className="reel-caption-main">
                <b>{clip.label}</b> · {clock(clip.startSec)}–{clock(clip.endSec)}
                {clip.cameraChampion ? <span className="mut"> · from {clip.cameraChampion}'s view</span> : null}
              </span>
              <span className="reel-caption-side">
                <span className="mut">{ready.findIndex(c => c.index === current) + 1} of {ready.length}{pending > 0 ? ` · ${pending} queued` : ''}</span>
                <button type="button" className="action sm-action" disabled={ready.findIndex(c => c.index === current) <= 0} onClick={() => step(-1)} aria-label="Previous clip">‹</button>
                <button type="button" className="action sm-action" disabled={ready.findIndex(c => c.index === current) >= ready.length - 1} onClick={() => step(1)} aria-label="Next clip">›</button>
                {clip.ready && canManage && (
                  <button type="button" className="action sm-action" title="Delete this clip and queue just this window for a fresh render on the gaming PC"
                    onClick={() => {
                      if (window.confirm('Delete this clip? The render agent will re-create it from the replay (needs the replay still playable on the current patch).')) onDelete(clip.index)
                    }}>✕ re-render</button>
                )}
              </span>
            </div>
          </div>
          <div className="reel-strip" ref={strip} role="listbox" aria-label="Clips">
            {clips.map(c => {
              const icon = c.cameraChampion ? icons(c.cameraChampion) : null
              const tone = c.label.endsWith('won') ? 'win' : c.label.endsWith('lost') ? 'loss' : 'neutral'
              return (
                <button key={c.index} type="button" role="option" aria-selected={c.index === current}
                  className={`reel-tile ${tone}${c.index === current ? ' active' : ''}${c.ready ? '' : ' queued'}`}
                  onClick={() => { setCurrent(c.index); setAutoplay(true) }}>
                  <span className="reel-thumb">
                    {icon ? <img src={icon} alt="" loading="lazy" /> : <span className="reel-thumb-blank" />}
                    <span className="reel-thumb-clock">{clock(c.startSec)}</span>
                  </span>
                  <span className="reel-tile-label">{c.label}</span>
                  <span className="reel-tile-sub mut">{c.ready ? (c.cameraChampion ? `${c.cameraChampion}'s view` : `${c.events.length} event${c.events.length === 1 ? '' : 's'}`) : 'queued'}</span>
                </button>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
}

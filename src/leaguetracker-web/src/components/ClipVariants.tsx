import { useState } from 'react'
import { clock } from './TimeLink'
import type { ClipInfo } from '../types'

// Review candidates beside ClipReel, reachable with ?clips=grid|carousel
// on the match page so the three can be compared side by side. The grid is
// the layout the deployed page has today.
interface ClipsProps {
  clips: ClipInfo[]
  title: string
  hint: string
  canManage: boolean
  onDelete: (index: number) => void
}

function ClipHeading({ clip, canManage, onDelete }: { clip: ClipInfo; canManage: boolean; onDelete: (index: number) => void }) {
  return (
    <div className="sub-h" style={{ marginTop: 0 }}>
      {clip.label} · {clock(clip.startSec)}–{clock(clip.endSec)}
      {clip.kind === 'fight' && clip.cameraChampion
        ? <span className="mut"> · from {clip.cameraChampion}'s view</span>
        : <span className="mut"> · {clip.events.map(e => `${e.kind} ${clock(e.timeSec)}`).join(', ')}</span>}
      {clip.ready && canManage && (
        <button className="action" style={{ padding: '0 8px', marginLeft: 8 }}
          title="Delete this clip and queue just this window for a fresh render on the gaming PC"
          onClick={() => {
            if (window.confirm('Delete this clip? The render agent will re-create it from the replay (needs the replay still playable on the current patch).')) onDelete(clip.index)
          }}>
          ✕ re-render
        </button>
      )}
    </div>
  )
}

function Planned({ clips }: { clips: ClipInfo[] }) {
  return (
    <p className="mut" style={{ margin: 0 }}>
      {clips.length} fight window{clips.length === 1 ? '' : 's'} planned — waiting for the render agent on the gaming PC.
    </p>
  )
}

export function ClipGrid({ clips, title, hint, canManage, onDelete }: ClipsProps) {
  return (
    <div className="card" style={{ marginBottom: 14 }}>
      <h2>{title} <span className="mut" style={{ fontWeight: 400 }}>— {hint}</span></h2>
      {clips.every(c => !c.ready) ? <Planned clips={clips} /> : (
        <div className="grid two-col">
          {clips.map(c => (
            <div key={c.index}>
              <ClipHeading clip={c} canManage={canManage} onDelete={onDelete} />
              {c.ready
                ? <video src={c.url} controls preload="metadata" style={{ width: '100%', borderRadius: 8, background: '#000' }} />
                : <div className="empty">queued for render</div>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

const PAGE = 2

export function ClipCarousel({ clips, title, hint, canManage, onDelete }: ClipsProps) {
  const [page, setPage] = useState(0)
  const pages = Math.max(1, Math.ceil(clips.length / PAGE))
  const shown = clips.slice(page * PAGE, page * PAGE + PAGE)
  return (
    <div className="card" style={{ marginBottom: 14 }}>
      <h2 className="carousel-head">
        <span>{title} <span className="mut" style={{ fontWeight: 400 }}>— {hint}</span></span>
        <span className="carousel-nav">
          <button type="button" className="action sm-action" disabled={page === 0} onClick={() => setPage(p => p - 1)} aria-label="Previous clips">‹</button>
          <span className="carousel-dots">
            {Array.from({ length: pages }, (_, i) => (
              <button key={i} type="button" className={`carousel-dot${i === page ? ' active' : ''}`} aria-label={`Page ${i + 1}`} onClick={() => setPage(i)} />
            ))}
          </span>
          <button type="button" className="action sm-action" disabled={page >= pages - 1} onClick={() => setPage(p => p + 1)} aria-label="Next clips">›</button>
          <span className="mut sm-text">{page * PAGE + 1}–{Math.min(clips.length, page * PAGE + PAGE)} of {clips.length}</span>
        </span>
      </h2>
      {clips.every(c => !c.ready) ? <Planned clips={clips} /> : (
        <div className="carousel-page">
          {shown.map(c => (
            <div key={c.index}>
              <ClipHeading clip={c} canManage={canManage} onDelete={onDelete} />
              {c.ready
                ? <video src={c.url} controls preload="metadata" style={{ width: '100%', borderRadius: 8, background: '#000' }} />
                : <div className="empty">queued for render</div>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

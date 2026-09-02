import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import { useLoadoutIcons } from '../champions'
import { PHASES, PHASE_HINT, PHASE_LABEL, STATUS_LABEL, describeRule } from '../gameplans'
import type { MatchGameplan, PointEvaluation, PointStatus, SelfStatus } from '../types'

const SELF: SelfStatus[] = ['met', 'missed', 'na']

function Summary({ summary }: { summary: MatchGameplan['summary'] }) {
  const order: PointStatus[] = ['met', 'missed', 'na', 'pending', 'unrated']
  const parts = order.filter(s => (summary[s] ?? 0) > 0)
  if (parts.length === 0) return null
  return (
    <span className="gp-summary">
      {parts.map(s => (
        <span key={s} className={`gp-chip ${s}`}>{summary[s]} {s === 'unrated' ? 'to rate' : STATUS_LABEL[s].toLowerCase()}</span>
      ))}
    </span>
  )
}

function PointRow({ point, index, canManage, busy, onRate }: {
  point: PointEvaluation
  index: number
  canManage: boolean
  busy: boolean
  onRate: (status: SelfStatus | null, note: string | null) => void
}) {
  const [noteOpen, setNoteOpen] = useState(false)
  const [note, setNote] = useState(point.self?.note ?? '')
  useEffect(() => { setNote(point.self?.note ?? '') }, [point.self?.note])
  const { itemInfo } = useLoadoutIcons()

  // The rule's answer stays visible under an override, so it reads as a decision.
  const overruled = point.self && point.auto && point.self.status !== point.auto.status

  return (
    <div className={`gp-row ${point.status}`}>
      <span className="gp-num">{String(index + 1).padStart(2, '0')}</span>
      <div className="gp-body">
        <div className="gp-text">{point.text}</div>
        {point.rule && <div className="gp-rule mut sm-text">rule: {describeRule(point.rule, id => itemInfo(id)?.name ?? null)}</div>}
        {point.auto && (
          <div className={`gp-auto ${overruled ? 'overruled' : ''}`}>
            <span className={`gp-dot ${point.auto.status}`} aria-hidden />
            {point.auto.detail}
            {overruled && <span className="mut"> — you said {STATUS_LABEL[point.self!.status].toLowerCase()}</span>}
          </div>
        )}
        {point.self?.note && !noteOpen && <div className="gp-note">“{point.self.note}”</div>}
        {noteOpen && (
          <form className="gp-note-form" onSubmit={e => { e.preventDefault(); onRate(point.self!.status, note); setNoteOpen(false) }}>
            <input className="text" autoFocus value={note} maxLength={500} placeholder="What you saw on the replay…"
              onChange={e => setNote(e.target.value)} />
            <button className="action sm-action" type="submit" disabled={busy}>save</button>
            <button className="action sm-action" type="button" onClick={() => { setNoteOpen(false); setNote(point.self?.note ?? '') }}>cancel</button>
          </form>
        )}
      </div>
      <div className="gp-rate">
        <span className={`rv-badge gp-status ${point.status}`}>{STATUS_LABEL[point.status]}</span>
        {canManage && (
          <div className="gp-buttons">
            <div className="seg mini">
              {SELF.map(s => (
                <button key={s} className={point.self?.status === s ? 'on' : ''} disabled={busy}
                  title={point.self?.status === s ? 'Clear your rating' : `Mark ${STATUS_LABEL[s].toLowerCase()}`}
                  onClick={() => onRate(point.self?.status === s ? null : s, point.self?.status === s ? null : (point.self?.note ?? null))}>
                  {STATUS_LABEL[s]}
                </button>
              ))}
            </div>
            {point.self && !noteOpen && (
              <button className="action sm-action" onClick={() => setNoteOpen(true)}>{point.self.note ? 'edit note' : '+ note'}</button>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

/// The champion's reference points for this game - the rules' answers, the player's, and the tally.
export default function GameplanCard({ matchId, canManage }: { matchId: string; canManage: boolean }) {
  const [plan, setPlan] = useState<MatchGameplan | null | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setPlan(undefined)
    api.matchGameplan(matchId).then(setPlan).catch(() => setPlan(null))
  }, [matchId])

  if (plan === undefined || plan === null) return null

  if (!plan.hasPlan) {
    if (!canManage) return null
    return (
      <div className="card gameplan-card empty-plan">
        <h2>Reference points <span className="mut">{plan.champion}</span></h2>
        <p className="mut" style={{ margin: 0 }}>
          No gameplan for {plan.champion} yet. <Link to={`/gameplans?champion=${encodeURIComponent(plan.champion)}`}>Write the reference points</Link> and
          every {plan.champion} game gets scored against them - the rules pre-fill what the timeline can see, you rate the rest after the replay.
        </p>
      </div>
    )
  }

  const rate = (pointId: string) => (status: SelfStatus | null, note: string | null) => {
    setBusy(true)
    setError(null)
    api.rateGameplanPoint(matchId, pointId, status, note)
      .then(setPlan)
      .catch(e => setError(String(e instanceof Error ? e.message : e)))
      .finally(() => setBusy(false))
  }

  let index = 0
  return (
    <div className="card gameplan-card">
      <div className="card-head">
        <h2>Reference points <span className="mut">{plan.champion} · your gameplan, game by game</span></h2>
        <span className="card-head-actions">
          <Summary summary={plan.summary} />
          {canManage && <Link to={`/gameplans?champion=${encodeURIComponent(plan.champion)}`} className="action sm-action">edit plan</Link>}
        </span>
      </div>
      {PHASES.filter(ph => plan.points.some(p => p.phase === ph)).map(ph => (
        <div key={ph} className="gp-phase">
          <div className="gp-phase-head">
            <span className="gp-phase-name">{PHASE_LABEL[ph]}</span>
            <span className="mut sm-text">{PHASE_HINT[ph]}</span>
          </div>
          {plan.points.filter(p => p.phase === ph).map(p => (
            <PointRow key={p.id} point={p} index={index++} canManage={canManage} busy={busy} onRate={rate(p.id)} />
          ))}
        </div>
      ))}
      {error && <p className="loss sm-text" style={{ margin: '8px 2px 0' }}>{error}</p>}
      <p className="mut sm-text" style={{ margin: '10px 2px 0' }}>
        Rules read the match timeline - positions interpolated between 60-second frames, no cast or wave data - so they
        pre-fill, they don't judge. Your rating after the replay is the one that counts.
      </p>
    </div>
  )
}

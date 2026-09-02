import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import { useLoadoutIcons } from '../champions'
import { PHASES, PHASE_HINT, PHASE_LABEL, STATUS_LABEL, describeRule } from '../gameplans'
import { linkClocks, type Jump } from './TimeLink'
import type { MatchGameplan, PointEvaluation, PointStatus } from '../types'

// undefined while loading, null when the game cannot be scored.
export function useMatchGameplan(matchId: string): MatchGameplan | null | undefined {
  const [plan, setPlan] = useState<MatchGameplan | null | undefined>(undefined)
  useEffect(() => {
    setPlan(undefined)
    api.matchGameplan(matchId).then(setPlan).catch(() => setPlan(null))
  }, [matchId])
  return plan
}

export function GameplanTally({ summary }: { summary: MatchGameplan['summary'] }) {
  const order: PointStatus[] = ['met', 'missed', 'na', 'pending']
  const parts = order.filter(s => (summary[s] ?? 0) > 0)
  if (parts.length === 0) return null
  return (
    <span className="gp-summary">
      {parts.map(s => <span key={s} className={`gp-chip ${s}`}>{summary[s]} {STATUS_LABEL[s].toLowerCase()}</span>)}
    </span>
  )
}

function PointRow({ point, index, onJump }: { point: PointEvaluation; index: number; onJump: Jump }) {
  const { itemInfo } = useLoadoutIcons()
  return (
    <div className={`gp-row ${point.result.status}`}>
      <span className="gp-num">{String(index + 1).padStart(2, '0')}</span>
      <div className="gp-body">
        <div className="gp-text">{point.text}</div>
        <div className="gp-rule mut sm-text">rule: {describeRule(point.rule, id => itemInfo(id)?.name ?? null)}</div>
        <div className="gp-auto">
          <span className={`gp-dot ${point.result.status}`} aria-hidden />
          <span>{linkClocks(point.result.detail, onJump)}</span>
        </div>
      </div>
      <span className={`rv-badge gp-status ${point.result.status}`}>{STATUS_LABEL[point.result.status]}</span>
    </div>
  )
}

// The champion's reference points, scored for this game, every clock in the
// evidence a way into the stage.
export function GameplanPoints({ plan, canManage, onJump }: { plan: MatchGameplan; canManage: boolean; onJump: Jump }) {
  let index = 0
  return (
    <div className="review-q gp-points">
      <div className="rv-head">
        <span className="rv-question">Reference points <span className="mut" style={{ fontWeight: 400 }}>{plan.champion} · your gameplan, game by game</span></span>
        {canManage && <Link to={`/gameplans?champion=${encodeURIComponent(plan.champion)}`} className="action sm-action">edit plan</Link>}
      </div>
      {PHASES.filter(ph => plan.points.some(p => p.phase === ph)).map(ph => (
        <div key={ph} className="gp-phase">
          <div className="gp-phase-head">
            <span className="gp-phase-name">{PHASE_LABEL[ph]}</span>
            <span className="mut sm-text">{PHASE_HINT[ph]}</span>
          </div>
          {plan.points.filter(p => p.phase === ph).map(p => <PointRow key={p.id} point={p} index={index++} onJump={onJump} />)}
        </div>
      ))}
      <p className="mut sm-text" style={{ margin: '10px 2px 0' }}>
        Read from the match timeline - positions interpolated between 60-second frames, no cast or wave data. Judge the
        call, not just the verdict.
      </p>
    </div>
  )
}

export function GameplanEmpty({ plan }: { plan: MatchGameplan }) {
  return (
    <p className="mut sm-text" style={{ margin: 0 }}>
      No gameplan for {plan.champion} yet. <Link to={`/gameplans?champion=${encodeURIComponent(plan.champion)}`}>Write the reference points</Link> and
      every {plan.champion} game gets scored against them.
    </p>
  )
}

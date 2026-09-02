import { useState } from 'react'
import { CONTEST_LABEL, contestSentence, contestSide } from '../contest'
import { GameplanEmpty, GameplanPoints, GameplanTally, useMatchGameplan } from './GameplanCard'
import { QUESTIONS, QuestionPanel, useMatchReview, verdictOf, type QuestionKey } from './ReviewCard'
import type { Jump } from './TimeLink'

const VERDICT_WORD: Record<string, string> = { yes: 'Yes', mixed: 'Mixed', no: 'No' }

// The whole verdict in one band: the contest and its sentence, a chip per
// question, the plan's tally. A chip opens its ledger right here - several
// at once if wanted - so the review never has to be read anywhere else.
export default function VerdictStrip({ matchId, canManage, onJump }: { matchId: string; canManage: boolean; onJump: Jump }) {
  const review = useMatchReview(matchId)
  const plan = useMatchGameplan(matchId)
  const [open, setOpen] = useState<Set<QuestionKey>>(() => new Set())
  const [planOpen, setPlanOpen] = useState(false)

  if (!review) return null
  const toggle = (key: QuestionKey) => setOpen(current => {
    const next = new Set(current)
    if (next.has(key)) next.delete(key)
    else next.add(key)
    return next
  })
  const hasPlan = plan?.hasPlan ?? false
  const showPlanChip = hasPlan || (plan && canManage)

  return (
    <div className={`card verdict-strip ${contestSide(review.contest) ?? ''}`}>
      <div className="vs-story">
        <span className={`contest-chip ${review.contest ?? 'na'}`}>
          {review.contest ? CONTEST_LABEL[review.contest] : 'No verdict'}
        </span>
        <span className="contest-sentence">{contestSentence(review)}</span>
      </div>
      <div className="vs-chips">
        {QUESTIONS.map(q => {
          const v = verdictOf(review, q.key)
          const isOpen = open.has(q.key)
          return (
            <button key={q.key} type="button" className={`qchip${isOpen ? ' open' : ''}`} aria-expanded={isOpen} onClick={() => toggle(q.key)}>
              {q.short} <b className={v ?? 'na'}>{v ? VERDICT_WORD[v] : 'no data'}</b>
              <span className="caret" aria-hidden>{isOpen ? '▾' : '▸'}</span>
            </button>
          )
        })}
        {showPlanChip && plan && (
          <button type="button" className={`qchip plan-chip${planOpen ? ' open' : ''}`} aria-expanded={planOpen} onClick={() => setPlanOpen(o => !o)}>
            <span className="mut">Plan</span>
            {hasPlan ? <GameplanTally summary={plan.summary} /> : <span className="mut">none yet</span>}
            <span className="caret" aria-hidden>{planOpen ? '▾' : '▸'}</span>
          </button>
        )}
      </div>
      {(open.size > 0 || (planOpen && plan)) && (
        <div className="vs-expand">
          {QUESTIONS.filter(q => open.has(q.key)).map(q => (
            <QuestionPanel key={q.key} review={review} which={q.key} onJump={onJump} />
          ))}
          {planOpen && plan && (hasPlan
            ? <GameplanPoints plan={plan} canManage={canManage} onJump={onJump} />
            : <div className="review-q"><GameplanEmpty plan={plan} /></div>)}
          <p className="mut sm-text vs-caption">
            Positions are interpolated between 60-second frames, and Riot exposes no ward/fog data — so these say where
            people <em>were</em>, not what you could see. Judge the call, not just the verdict.
          </p>
        </div>
      )}
    </div>
  )
}

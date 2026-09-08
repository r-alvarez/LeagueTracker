import { useState } from 'react'
import { CONTEST_LABEL, contestSentence, contestSide } from '../contest'
import { GameplanEmpty, GameplanPoints, GameplanTally, useMatchGameplan } from './GameplanCard'
import { QUESTIONS, QuestionPanel, useMatchReview } from './ReviewCard'
import type { Jump } from './TimeLink'

// The whole verdict in one band: the contest and its sentence, the four
// questions with their ledgers open, and the plan's tally as a chip that
// opens its reference points right here.
export default function VerdictStrip({ matchId, canManage, onJump }: { matchId: string; canManage: boolean; onJump: Jump }) {
  const review = useMatchReview(matchId)
  const plan = useMatchGameplan(matchId)
  const [planOpen, setPlanOpen] = useState(false)

  if (!review) return null
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
      <div className="review-grid vs-questions">
        {QUESTIONS.map(q => <QuestionPanel key={q.key} review={review} which={q.key} onJump={onJump} />)}
      </div>
      {showPlanChip && plan && (
        <div className="vs-chips">
          <button type="button" className={`qchip plan-chip${planOpen ? ' open' : ''}`} aria-expanded={planOpen} onClick={() => setPlanOpen(o => !o)}>
            <span className="mut">Plan</span>
            {hasPlan ? <GameplanTally summary={plan.summary} /> : <span className="mut">none yet</span>}
            <span className="caret" aria-hidden>{planOpen ? '▾' : '▸'}</span>
          </button>
        </div>
      )}
      {planOpen && plan && (
        <div className="vs-expand">
          {hasPlan
            ? <GameplanPoints plan={plan} canManage={canManage} onJump={onJump} />
            : <div className="review-q"><GameplanEmpty plan={plan} /></div>}
        </div>
      )}
      <p className="mut sm-text vs-caption">
        Positions are interpolated between 60-second frames, and Riot exposes no ward/fog data — so these say where
        people <em>were</em>, not what you could see. Judge the call, not just the verdict.
      </p>
    </div>
  )
}

import type { ReviewVerdict } from '../types'

// The four questions, one letter each - process verdicts that never mention
// the game's result.
const QUESTIONS: Array<{ key: 'laneDuel' | 'fights' | 'discipline' | 'stewardship'; letter: string; label: string }> = [
  { key: 'laneDuel', letter: 'L', label: 'Did I out-duel my lane?' },
  { key: 'fights', letter: 'F', label: 'Did I leave my fights alive?' },
  { key: 'discipline', letter: 'D', label: 'Did I account for the enemy before stepping?' },
  { key: 'stewardship', letter: 'S', label: 'Did I keep my lead / recover my deficit?' },
]

export default function ReviewDots({ v }: { v?: { laneDuel: ReviewVerdict; fights: ReviewVerdict; discipline: ReviewVerdict; stewardship: ReviewVerdict } }) {
  if (!v) return null
  return (
    <span className="review-dots">
      {QUESTIONS.map(q => {
        const verdict = v[q.key]
        return (
          <span key={q.key} className={`rv-dot ${verdict ?? 'na'}`} title={`${q.label} — ${verdict ?? 'no data'}`}>
            {q.letter}
          </span>
        )
      })}
    </span>
  )
}

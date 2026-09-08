import { useEffect, useState } from 'react'
import { api } from '../api'
import { TimeLink, type Jump } from './TimeLink'
import type { MatchReview, ReviewVerdict } from '../types'

export type QuestionKey = 'lane' | 'fights' | 'discipline' | 'stewardship'

export const QUESTIONS: { key: QuestionKey; short: string; title: string }[] = [
  { key: 'lane', short: 'Lane', title: 'Did I out-duel my lane?' },
  { key: 'fights', short: 'Fights', title: 'Did I leave my fights alive?' },
  { key: 'discipline', short: 'Discipline', title: 'Did I account for the enemy before stepping?' },
  { key: 'stewardship', short: 'Stewardship', title: 'Did I keep my lead / recover my deficit?' },
]

const VERDICT_WORD: Record<string, string> = { yes: 'Yes', mixed: 'Mixed', no: 'No' }

export function verdictOf(review: MatchReview, key: QuestionKey): ReviewVerdict | null {
  switch (key) {
    case 'lane': return review.laneDuel?.verdict ?? null
    case 'fights': return review.fights.verdict
    case 'discipline': return review.discipline.verdict
    case 'stewardship': return review.stewardship?.verdict ?? null
  }
}

export function VerdictBadge({ v }: { v: ReviewVerdict | null }) {
  return <span className={`rv-badge ${v ?? 'na'}`}>{v ? VERDICT_WORD[v] : 'no data'}</span>
}

// undefined while loading, null when the game has no review.
export function useMatchReview(matchId: string): MatchReview | null | undefined {
  const [review, setReview] = useState<MatchReview | null | undefined>(undefined)
  useEffect(() => {
    setReview(undefined)
    api.review(matchId).then(setReview).catch(() => setReview(null))
  }, [matchId])
  return review
}

const whereWord = (w: string) => (w === 'dead' ? 'dead' : w === 'elsewhere' ? 'cross-map' : 'right there, uninvolved')

// One question's ledger, phrased as process, never as result. Every clock
// in it opens the stage on that moment.
export function QuestionPanel({ review, which, onJump }: { review: MatchReview; which: QuestionKey; onJump: Jump }) {
  const title = QUESTIONS.find(q => q.key === which)?.title ?? ''
  const lane = review.laneDuel
  const fights = review.fights
  const disc = review.discipline
  const stew = review.stewardship
  const laneNet = lane ? lane.detail.components.reduce((s, c) => s + c.delta, 0) : 0

  return (
    <div className="review-q">
      <div className="rv-head">
        <span className="rv-question">{title}</span>
        <VerdictBadge v={verdictOf(review, which)} />
      </div>

      {which === 'lane' && (lane ? (
        <ul className="rv-evidence">
          <li>
            Kill exchange vs {lane.detail.opponent}:{' '}
            <strong>{lane.detail.killsOnOpponent}–{lane.detail.deathsToOpponent}</strong>
          </li>
          {lane.detail.laneGoldDiff15 !== null && (
            <li>Lane gold @15: <strong className={lane.detail.laneGoldDiff15 >= 0 ? 'win' : 'loss'}>
              {lane.detail.laneGoldDiff15 > 0 ? '+' : ''}{lane.detail.laneGoldDiff15}g</strong></li>
          )}
          {lane.detail.lateGold && (
            <li>Gold vs lane @{lane.detail.lateGold.min}: <strong className={lane.detail.lateGold.gold >= 0 ? 'win' : 'loss'}>
              {lane.detail.lateGold.gold > 0 ? '+' : ''}{lane.detail.lateGold.gold}g</strong></li>
          )}
          {(lane.detail.myCashKills > 0 || lane.detail.theirCashKills > 0) && (
            <li>Cash-ins while the other was away:{' '}
              <strong className={lane.detail.myCashKills >= lane.detail.theirCashKills ? 'win' : 'loss'}>
                you {lane.detail.myCashKills} · them {lane.detail.theirCashKills}</strong></li>
          )}
          {lane.detail.theirCashIns.map((a, i) => (
            <li key={`t${i}`} className={a.where === 'elsewhere' && !a.paid ? 'loss' : ''}>
              <TimeLink t={a.timeSec} onJump={onJump} /> — they got {a.kills} while you were {whereWord(a.where)}
              {a.where === 'elsewhere' && (a.paid ? '; your split took a structure (paid)' : '; your absence bought nothing')}
            </li>
          ))}
          {lane.detail.myCashIns.map((a, i) => (
            <li key={`m${i}`} className="win">
              <TimeLink t={a.timeSec} onJump={onJump} /> — you got {a.kills} while they were {whereWord(a.where)}
              {a.where === 'elsewhere' && a.paid ? ' (their split paid)' : ''}
            </li>
          ))}
          {lane.detail.components.length > 0 && (
            <li className="mut">
              Verdict math: {lane.detail.components.map(c => `${c.label} ${c.delta > 0 ? '+1' : '−1'}`).join(' · ')}
              {' '}→ net {laneNet > 0 ? '+' : ''}{laneNet} (yes at +2, no at −2)
            </li>
          )}
        </ul>
      ) : <p className="mut sm-text">No same-role opponent in this game.</p>)}

      {which === 'stewardship' && (stew ? (
        <ul className="rv-evidence">
          <li>
            Started {stew.detail.state} vs lane:{' '}
            <strong className={stew.detail.startGold >= 0 ? 'win' : 'loss'}>
              {stew.detail.startGold > 0 ? '+' : ''}{stew.detail.startGold}g</strong> @{stew.detail.startMin}
            {' → '}
            <strong className={stew.detail.endGold >= 0 ? 'win' : 'loss'}>
              {stew.detail.endGold > 0 ? '+' : ''}{stew.detail.endGold}g</strong> @{stew.detail.endMin}
            {' — '}<strong>{stew.detail.summary}</strong>
          </li>
          {stew.detail.teamGold15 !== null && stew.detail.teamGold20 !== null && (
            <li className="mut">
              Team gold: {stew.detail.teamGold15 > 0 ? '+' : ''}{stew.detail.teamGold15}g @15
              {' → '}{stew.detail.teamGold20 > 0 ? '+' : ''}{stew.detail.teamGold20}g @20
            </li>
          )}
        </ul>
      ) : <p className="mut sm-text">Game too short (or no lane opponent) to judge the trajectory.</p>)}

      {which === 'fights' && (
        <ul className="rv-evidence">
          {fights.detail.overstays.length === 0 && fights.verdict !== null && (
            <li className="win">No overstays — every death had a reason the numbers can see</li>
          )}
          {fights.detail.overstays.map((o, i) => (
            <li key={`o${i}`} className="loss">
              <TimeLink t={o.timeSec} onJump={onJump} /> — died to {o.killedBy} with the numbers present
              ({o.alliesNear} all{o.alliesNear === 1 ? 'y' : 'ies'} vs {o.enemiesNear} enem{o.enemiesNear === 1 ? 'y' : 'ies'} near, no committed fight)
            </li>
          ))}
          {fights.detail.paidAbsences.map((a, i) => (
            <li key={`p${i}`} className="win">
              <TimeLink t={a.startSec} onJump={onJump} /> — team {a.result === 'lost' ? 'gave' : a.result === 'won' ? 'won' : 'held'} a {a.size} without you;
              your split took {a.paid.map(k => k.toLowerCase().replace('_', ' ')).join(' + ')} (paid)
            </li>
          ))}
          <li className="mut">
            Context: entered {fights.detail.participated} fights — won {fights.detail.won}, lost {fights.detail.lost}
            {fights.detail.draw > 0 ? `, drew ${fights.detail.draw}` : ''} · converted {fights.detail.converted} · conceded {fights.detail.conceded}.
            The team's fight war shows here; only overstays grade you.
          </li>
          {fights.verdict === null && <li className="mut">No fights and no late deaths — nothing to judge.</li>}
        </ul>
      )}

      {which === 'discipline' && (
        <ul className="rv-evidence">
          <li>
            Deaths: <strong>{disc.detail.deaths}</strong>
            {disc.detail.deaths > 0 && (
              <> — {disc.detail.ganked > 0 && <span className="loss">{disc.detail.ganked} ganked · </span>}
                {disc.detail.followIns > 0 && <span className="loss">{disc.detail.followIns} follow-in · </span>}
                {disc.detail.followInsTraded > 0 && <span>{disc.detail.followInsTraded} follow-in, traded · </span>}
                {disc.detail.fogPicks > 0 && <span className="loss">{disc.detail.fogPicks} picked from fog · </span>}
                {disc.detail.outnumbered > 0 && <span className="loss">{disc.detail.outnumbered} stepped outnumbered · </span>}
                {disc.detail.outnumberedTraded > 0 && <span>{disc.detail.outnumberedTraded} stepped outnumbered, traded · </span>}
                {disc.detail.withTeam} with the team</>
            )}
          </li>
          {disc.detail.fightsStepped > 0 && (
            <li className="mut">
              Stepped into <strong>{disc.detail.fightsStepped}</strong> fights —{' '}
              {disc.detail.flagged === 0
                ? 'every step accounted'
                : `${disc.detail.flagged} flagged${disc.detail.flagged === 1 && disc.verdict === 'yes'
                    ? '; one lapse across that many steps is still the habit'
                    : ''}`}
            </li>
          )}
          {disc.detail.concededEpicsAbsent.map((c, i) => (
            <li key={i} className={c.paid ? '' : 'loss'}>
              <TimeLink t={c.timeSec} onJump={onJump} /> — enemy {c.kind.toLowerCase()} while you were {(c.myDistance / 1000).toFixed(1)}k units away
              ({c.alliesNear} all{c.alliesNear === 1 ? 'y' : 'ies'} there);{' '}
              {c.paid ? 'you took a structure for it (traded)' : 'nothing taken in return'}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

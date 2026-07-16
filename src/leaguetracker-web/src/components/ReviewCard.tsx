import { useEffect, useState } from 'react'
import { api } from '../api'
import { CONTEST_LABEL, contestSentence } from '../contest'
import type { MatchReview, ReviewVerdict } from '../types'

const mmss = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`

const VERDICT_WORD: Record<string, string> = { yes: 'Yes', mixed: 'Mixed', no: 'No' }

function Badge({ v }: { v: ReviewVerdict }) {
  return <span className={`rv-badge ${v ?? 'na'}`}>{v ? VERDICT_WORD[v] : 'no data'}</span>
}

/// The three questions for one game - phrased as process, never as result.
export default function ReviewCard({ matchId }: { matchId: string }) {
  const [review, setReview] = useState<MatchReview | null | undefined>(undefined)

  useEffect(() => {
    setReview(undefined)
    api.review(matchId).then(setReview).catch(() => setReview(null))
  }, [matchId])

  if (review === undefined || review === null) return null
  const lane = review.laneDuel
  const fights = review.fights
  const disc = review.discipline
  const stew = review.stewardship

  const whereWord = (w: string) => w === 'dead' ? 'dead' : w === 'elsewhere' ? 'cross-map' : 'right there, uninvolved'
  const laneNet = lane ? lane.detail.components.reduce((s, c) => s + c.delta, 0) : 0

  return (
    <div className="card review-card">
      <h2>The four questions <span className="mut">process, not result</span></h2>
      <div className="contest-head">
        <span className={`contest-chip ${review.contest ?? 'na'}`}>
          {review.contest ? CONTEST_LABEL[review.contest] : 'No verdict'}
        </span>
        <span className="contest-sentence">{contestSentence(review)}</span>
      </div>
      <div className="review-grid">
        <div className="review-q">
          <div className="rv-head">
            <span className="rv-question">Did I out-duel my lane?</span>
            <Badge v={lane?.verdict ?? null} />
          </div>
          {lane && (
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
                  {mmss(a.timeSec)} — they got {a.kills} while you were {whereWord(a.where)}
                  {a.where === 'elsewhere' && (a.paid ? '; your split took a structure (paid)' : '; your absence bought nothing')}
                </li>
              ))}
              {lane.detail.myCashIns.map((a, i) => (
                <li key={`m${i}`} className="win">
                  {mmss(a.timeSec)} — you got {a.kills} while they were {whereWord(a.where)}
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
          )}
          {!lane && <p className="mut sm-text">No same-role opponent in this game.</p>}
        </div>

        <div className="review-q">
          <div className="rv-head">
            <span className="rv-question">Did I keep my lead / recover my deficit?</span>
            <Badge v={stew?.verdict ?? null} />
          </div>
          {stew ? (
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
          ) : <p className="mut sm-text">Game too short (or no lane opponent) to judge the trajectory.</p>}
        </div>

        <div className="review-q">
          <div className="rv-head">
            <span className="rv-question">Did my fights buy the map?</span>
            <Badge v={fights.verdict} />
          </div>
          <ul className="rv-evidence">
            <li>Fights taken: <strong>{fights.detail.participated}</strong> — won {fights.detail.won}, lost {fights.detail.lost}</li>
            <li>Won fights converted into objectives: <strong>{fights.detail.converted}/{fights.detail.won}</strong></li>
            {fights.detail.conceded > 0 && (
              <li className="loss">Lost fights the enemy converted: {fights.detail.conceded}</li>
            )}
            {fights.verdict === null && <li className="mut">No decisive fights involved you.</li>}
          </ul>
        </div>

        <div className="review-q">
          <div className="rv-head">
            <span className="rv-question">Did I account for the enemy before stepping?</span>
            <Badge v={disc.verdict} />
          </div>
          <ul className="rv-evidence">
            <li>
              Deaths: <strong>{disc.detail.deaths}</strong>
              {disc.detail.deaths > 0 && (
                <> — {disc.detail.ganked > 0 && <span className="loss">{disc.detail.ganked} ganked · </span>}
                  {disc.detail.followIns > 0 && <span className="loss">{disc.detail.followIns} follow-in · </span>}
                  {disc.detail.isolated > 0 && <span className="loss">{disc.detail.isolated} caught alone · </span>}
                  {disc.detail.withTeam} with the team</>
              )}
            </li>
            {disc.detail.concededEpicsAbsent.map((c, i) => (
              <li key={i} className={c.paid ? '' : 'loss'}>
                {mmss(c.timeSec)} — enemy {c.kind.toLowerCase()} while you were {(c.myDistance / 1000).toFixed(1)}k units away
                ({c.alliesNear} all{c.alliesNear === 1 ? 'y' : 'ies'} there);{' '}
                {c.paid ? 'you took a structure for it (traded)' : 'nothing taken in return'}
              </li>
            ))}
          </ul>
        </div>
      </div>
      <p className="mut sm-text" style={{ margin: '10px 2px 0' }}>
        Positions are interpolated between 60-second frames, and Riot exposes no ward/fog data — so these say where
        people <em>were</em>, not what you could see. Judge the call, not just the verdict.
      </p>
    </div>
  )
}

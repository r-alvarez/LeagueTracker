import { useEffect, useState } from 'react'
import { api } from '../api'
import type { StopLoss } from '../types'

const POLL_MS = 60_000
/// Losses in the live session before the banner speaks up.
const WARN_AT = 2

/// The tilt guard: appears only while a losing session is actually live
/// (2+ straight ranked losses, last game under 3h ago) and argues from the
/// player's own history - the measured winrate of the next game after this
/// many straight losses - not from generic "take a break" advice.
export default function StopLossBanner() {
  const [data, setData] = useState<StopLoss | null>(null)

  useEffect(() => {
    const poll = () => api.stopLoss().then(setData).catch(() => setData(null))
    poll()
    const id = setInterval(poll, POLL_MS)
    return () => clearInterval(id)
  }, [])

  if (!data || !data.sessionActive || data.streak < WARN_AT) return null

  const bucket = data.nextGame[Math.min(data.streak, 3)]
  const fresh = data.nextGame[0]
  const stop = data.streak >= 3
  const color = stop ? 'var(--lp-loss)' : 'var(--warn)'

  return (
    <div className="card" style={{
      marginBottom: 16, display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap',
      borderLeft: `3px solid ${color}`,
    }}>
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8, fontWeight: 700, color }}>
        <span style={{ width: 9, height: 9, borderRadius: '50%', background: color, boxShadow: `0 0 6px ${color}` }} />
        Stop-loss
      </span>
      <span>
        <strong>{data.streak} straight losses</strong> this session.
      </span>
      {bucket.winRate !== null && bucket.games >= 5 && (
        <span className="mut">
          Your next game after {data.streak >= 3 ? '3+' : data.streak} straight losses:{' '}
          <strong className={bucket.winRate < 50 ? 'loss' : ''}>{bucket.winRate}%</strong> win rate
          ({bucket.games} games{fresh.winRate !== null ? ` · ${fresh.winRate}% fresh` : ''}).
        </span>
      )}
      <span style={{ marginLeft: 'auto', fontWeight: 650 }}>
        {stop ? 'The math says stop for today.' : 'One more loss and the math says stop.'}
      </span>
    </div>
  )
}

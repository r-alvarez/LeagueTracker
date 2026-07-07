import { useEffect, useState } from 'react'
import { api } from '../api'
import type { ChallengeBenchmark, ChallengeRow } from '../types'

// Iron → Challenger, mapped to the LP rank colours so a level reads at a glance.
const LEVEL_COLOR: Record<string, string> = {
  CHALLENGER: 'var(--warn)', GRANDMASTER: 'var(--lp-loss)', MASTER: 'var(--series-1)',
  DIAMOND: 'var(--series-1)', PLATINUM: 'var(--chart-green)', EMERALD: 'var(--chart-green)',
  GOLD: 'var(--warn)', SILVER: 'var(--muted)', BRONZE: 'var(--muted)', IRON: 'var(--muted)',
}

function Row({ c }: { c: ChallengeRow }) {
  // Riot's percentile is 0–1 with higher = better standing; render as "top X%".
  const topPct = c.percentile !== null ? Math.max(1, Math.round((1 - c.percentile) * 100)) : null
  return (
    <div className="profile-row" title={c.description}>
      <span className="profile-label">
        {c.name}
        <span className="cat-chip" style={{ color: LEVEL_COLOR[c.level] ?? 'var(--muted)' }}>{c.level}</span>
      </span>
      <span className="profile-bar">
        <span className="fill" style={{
          left: 0, width: `${c.percentile !== null ? Math.round(c.percentile * 100) : 0}%`,
          background: LEVEL_COLOR[c.level] ?? 'var(--muted)',
        }} />
      </span>
      <span className="profile-vals mut">{topPct !== null ? `top ${topPct}%` : '—'}</span>
    </div>
  )
}

export default function ChallengesCard() {
  const [data, setData] = useState<ChallengeBenchmark | null | undefined>(undefined)

  useEffect(() => {
    api.challengePercentiles().then(setData).catch(() => setData(null))
  }, [])

  if (data === undefined) return null   // still loading — stay quiet
  if (data === null || data.challenges.length === 0) {
    return (
      <div className="card" style={{ marginBottom: 16 }}>
        <h2>Vs the ladder <span className="mut" style={{ fontWeight: 400 }}>— percentile benchmarking</span></h2>
        <p className="mut" style={{ margin: 0 }}>
          This ranks you against every player using Riot's Challenges API — the one benchmark the wins-vs-losses view can't
          give. It needs an API key with <strong>Challenges-V1</strong> access, which your current development key doesn't
          have (the endpoint returns 401). It lights up automatically once your personal/production key is approved.
        </p>
      </div>
    )
  }

  const sorted = [...data.challenges].sort((a, b) =>
    b.levelRank - a.levelRank || (b.percentile ?? 0) - (a.percentile ?? 0))
  const strengths = sorted.slice(0, 8)
  const weaknesses = [...sorted].reverse().slice(0, 8)

  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <h2>Vs the ladder <span className="mut" style={{ fontWeight: 400 }}>— where you rank against every player</span></h2>
      <div className="grid two-col">
        <div>
          <div className="sub-h" style={{ marginTop: 0 }}>Your strengths (top challenges)</div>
          <div className="profile-list">{strengths.map(c => <Row key={c.id} c={c} />)}</div>
        </div>
        <div>
          <div className="sub-h" style={{ marginTop: 0 }}>Where you rank lowest (train these)</div>
          <div className="profile-list">{weaknesses.map(c => <Row key={c.id} c={c} />)}</div>
        </div>
      </div>
      <p className="mut sm-text" style={{ margin: '10px 0 0' }}>
        Account-level, not windowed{data.asOfUtc ? ` · as of ${new Date(data.asOfUtc).toLocaleDateString()}` : ''}.
        Level is your Iron→Challenger tier on each challenge; bar is your percentile.
      </p>
    </div>
  )
}

import { useState } from 'react'
import type { LpPerGame, RankInfo, Stats, Status } from '../types'
import { useChampionIcons } from '../champions'
import { FormDots, tierClass } from './Stats'

/// CDragon serves the official ranked emblems; if the path ever moves the img
/// hides itself and the tier-colored text still carries the rank.
function RankEmblem({ tier }: { tier: string }) {
  const [broken, setBroken] = useState(false)
  if (broken) return null
  const src = `https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-static-assets/global/default/images/ranked-emblem/emblem-${tier.toLowerCase()}.png`
  return <img className="ph-emblem" src={src} alt={tier} loading="lazy" onError={() => setBroken(true)} />
}

function WinrateRing({ wins, losses, size = 76 }: { wins: number; losses: number; size?: number }) {
  const total = Math.max(1, wins + losses)
  const pct = wins / total
  const r = (size - 10) / 2
  const c = 2 * Math.PI * r
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} className="ph-ring">
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="rgba(240, 85, 106, 0.45)" strokeWidth="6" />
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="var(--series-1)" strokeWidth="6"
        strokeDasharray={`${c * pct} ${c}`} strokeLinecap="round" transform={`rotate(-90 ${size / 2} ${size / 2})`} />
      <text x="50%" y="46%" textAnchor="middle" fontSize="15" fontWeight="800" fill="var(--ink)">{Math.round(pct * 100)}%</text>
      <text x="50%" y="64%" textAnchor="middle" fontSize="9" fontWeight="600" fill="var(--muted)">{wins}W {losses}L</text>
    </svg>
  )
}

export default function ProfileHeader({ status, stats, lpGames }: {
  status: Status | null
  stats: Stats | null
  lpGames: LpPerGame[]
}) {
  const icons = useChampionIcons()
  if (!status) return null

  const solo: RankInfo | undefined = status.ranks.find(r => r.queue === 'Solo/Duo')
  const mostPlayed = stats?.byChampion?.[0]?.key ?? null
  const avatar = mostPlayed ? icons(mostPlayed) : null
  const soloDelta = stats?.lpDeltas.find(d => d.queue === 'Solo/Duo')
  const recentForm = lpGames
    .filter(g => g.queueName.includes('Solo'))
    .slice(0, 10)
    .map(g => g.win)
    .reverse()

  return (
    <div className="card ph">
      <span className="ph-avatar">
        {avatar
          ? <img src={avatar} alt={mostPlayed ?? ''} title={mostPlayed ? `Most played: ${mostPlayed}` : undefined} />
          : <span className="champ-mono">{status.riotId.slice(0, 2)}</span>}
      </span>

      <div className="ph-id">
        <span className="ph-name">{status.riotId.split('#')[0]} <span className="mut">#{status.riotId.split('#')[1]}</span></span>
        <span className="mut sm-text">{status.matches} games tracked · {status.replays} replays · patches {status.patches.slice(-2).join(', ')}</span>
        {recentForm.length > 0 && (
          <span className="ph-form"><span className="mut sm-text">form</span> <FormDots results={recentForm} /></span>
        )}
      </div>

      {solo && (
        <div className="ph-rank">
          <RankEmblem tier={solo.tier} />
          <span className="ph-rank-text">
            <span className={`ph-tier ${tierClass(solo.tier)}`}>{solo.tier} {solo.division}</span>
            <span className="ph-lp">{solo.lp} LP</span>
            {soloDelta && (soloDelta.last30 !== null || soloDelta.last7 !== null) && (
              <span className="ph-deltas">
                {soloDelta.last30 !== null && (
                  <span className={`lp-chip ${soloDelta.last30 >= 0 ? 'gain' : 'loss'}`}>30d {soloDelta.last30 >= 0 ? '+' : ''}{soloDelta.last30}</span>
                )}
                {soloDelta.last7 !== null && (
                  <span className={`lp-chip ${soloDelta.last7 >= 0 ? 'gain' : 'loss'}`}>7d {soloDelta.last7 >= 0 ? '+' : ''}{soloDelta.last7}</span>
                )}
              </span>
            )}
          </span>
        </div>
      )}

      {solo && <WinrateRing wins={solo.wins} losses={solo.losses} />}
    </div>
  )
}

import { useEffect, useState } from 'react'
import { api } from '../api'
import type { LiveGame } from '../types'
import { useChampionIcons, useChampionNames } from '../champions'
import { RankChip } from './Stats'

const POLL_MS = 30_000

function ChampStrip({ ids, highlight }: { ids: number[]; highlight?: number }) {
  const icons = useChampionIcons()
  const names = useChampionNames()
  return (
    <span style={{ display: 'inline-flex', gap: 3, alignItems: 'center' }}>
      {ids.map((id, i) => {
        const name = names(id) ?? `#${id}`
        const icon = name.startsWith('#') ? null : icons(name)
        return (
          <span key={`${id}-${i}`} className="champ-frame" title={name}
            style={{ width: 22, height: 22, outline: id === highlight ? '2px solid var(--warn)' : undefined }}>
            {icon
              ? <img src={icon} alt={name} loading="lazy" />
              : <span className="champ-mono" style={{ width: 22, height: 22 }}>{name.replace('#', '').slice(0, 2)}</span>}
          </span>
        )
      })}
    </span>
  )
}

export default function LiveGameBanner() {
  const [game, setGame] = useState<LiveGame | null>(null)
  const [, setTick] = useState(0)
  const names = useChampionNames()

  useEffect(() => {
    const poll = () => api.live().then(setGame).catch(() => setGame(null))
    poll()
    const id = setInterval(poll, POLL_MS)
    // Extra ticker so the elapsed minutes advance between polls.
    const tick = setInterval(() => setTick(t => t + 1), 60_000)
    return () => { clearInterval(id); clearInterval(tick) }
  }, [])

  if (!game) return null

  const since = game.startedUtc ?? game.detectedUtc
  const elapsedMin = Math.max(0, Math.floor((Date.now() - new Date(since).getTime()) / 60_000))
  const myChampion = names(game.myChampionId)
  const allies = game.participants.filter(p => p.teamId === game.myTeamId).map(p => p.championId)
  const enemies = game.participants.filter(p => p.teamId !== game.myTeamId).map(p => p.championId)

  // Tint the banner by the lobby rank gap: green when favored, red when outranked.
  const gap = game.rankGapLp
  const favored = gap !== null && gap !== 0 ? gap < 0 : null

  return (
    <div className="card" style={{
      marginBottom: 16, display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap',
      borderLeft: favored === null ? undefined : `3px solid var(${favored ? '--delta-good' : '--lp-loss'})`,
    }}>
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8, fontWeight: 600 }}>
        <span style={{
          width: 9, height: 9, borderRadius: '50%', background: 'var(--lp-loss)',
          boxShadow: '0 0 6px var(--lp-loss)',
        }} />
        In game
      </span>
      <span className="mut">
        {game.queue}{myChampion ? ` · ${myChampion}` : ''}
        {game.startedUtc ? ` · ${elapsedMin} min` : ' · loading / early game'}
      </span>
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
        <ChampStrip ids={allies} highlight={game.myChampionId} />
        <span className="mut sm-text">vs</span>
        <ChampStrip ids={enemies} />
      </span>
      {game.avgAllyRank && game.avgEnemyRank && (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <RankChip label={game.avgAllyRank} />
          <span className="mut sm-text">vs</span>
          <RankChip label={game.avgEnemyRank} />
          {favored !== null && (
            <span className={`sm-text ${favored ? 'win' : 'loss'}`} style={{ fontWeight: 700 }}>
              {favored ? `favored by ${-gap!} LP` : `outranked by ${gap} LP`}
            </span>
          )}
        </span>
      )}
      <span className="mut sm-text" style={{ marginLeft: 'auto' }}>
        capture starts moments after the game ends
      </span>
    </div>
  )
}

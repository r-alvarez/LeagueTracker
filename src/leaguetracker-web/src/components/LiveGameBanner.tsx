import { useEffect, useState } from 'react'
import { api } from '../api'
import type { LiveGame } from '../types'
import { useChampionIcons, useChampionNames } from '../champions'
import { RankChip } from './Stats'

// The server re-reads spectator every 30s while a game is live; polling at
// half that keeps the banner within ~15s of what the server knows.
const POLL_MS = 15_000

// The in-game clock: seconds since the server-calibrated zero, or null while
// spectator has not published a start time yet (loading screen, first moments).
const gameClockSec = (game: LiveGame, nowMs: number): number | null =>
  game.clockStartUtc ? Math.max(0, Math.floor((nowMs - new Date(game.clockStartUtc).getTime()) / 1000)) : null

const formatClock = (sec: number) => `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`

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
    return () => clearInterval(id)
  }, [])

  // A live clock, not a stale minute count: re-render every second while in game.
  const inGame = game !== null
  useEffect(() => {
    if (!inGame) return
    const tick = setInterval(() => setTick(t => t + 1), 1000)
    return () => clearInterval(tick)
  }, [inGame])

  if (!game) return null

  const clockSec = gameClockSec(game, Date.now())
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
        {' · '}
        {clockSec === null
          ? 'loading / early game'
          : <span style={{ fontVariantNumeric: 'tabular-nums' }}>{formatClock(clockSec)}</span>}
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

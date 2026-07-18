import { Bar, BarChart, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { LpPerGame } from '../types'

interface Props {
  games: LpPerGame[]
}

/// Height of the neutral stub drawn for games with no attributed LP - visible
/// but clearly not a measurement.
const UNKNOWN_STUB = 4

type Row = LpPerGame & { bar: number; known: boolean }

interface TooltipProps {
  active?: boolean
  payload?: Array<{ payload: Row }>
}

function GameTooltip({ active, payload }: TooltipProps) {
  if (!active || !payload?.length) return null
  const g = payload[0].payload
  const change = g.lpChange ?? 0
  return (
    <div className="viz-tooltip">
      <div className="v">{g.known ? `${change > 0 ? '+' : ''}${change} LP` : '? LP'}</div>
      <div className="l">
        {g.champion} · {g.win ? 'Victory' : 'Defeat'} {g.kda}
        <br />
        {g.known ? <>{g.lpBefore} → {g.lpAfter}</> : 'not captured live — LP unknown'}
        <br />
        {new Date(g.gameEndUtc).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}
      </div>
    </div>
  )
}

/// Gain/loss per game is polarity around zero - a diverging pair (blue gain,
/// red loss), rounded 4px at the data end, square at the baseline. Games
/// without an attributed LP keep their slot as a small neutral stub (signed by
/// win/loss) instead of silently disappearing - a gap in capture shouldn't
/// read as a gap in play.
export default function LpPerGameBars({ games }: Props) {
  const data: Row[] = games
    .slice(0, 30)
    .reverse()
    .map(g => ({ ...g, known: g.lpChange !== null, bar: g.lpChange ?? (g.win ? UNKNOWN_STUB : -UNKNOWN_STUB) }))
  if (data.length === 0) {
    return <div className="empty">No ranked games yet - bars appear as games are captured.</div>
  }
  const unknowns = data.filter(d => !d.known).length

  return (
    <>
      <ResponsiveContainer width="100%" height={unknowns > 0 ? 242 : 260}>
        <BarChart data={data} margin={{ top: 8, right: 12, bottom: 0, left: 8 }} barCategoryGap={2}>
          <XAxis dataKey="id" tick={false} tickLine={false} stroke="var(--baseline)" height={8} />
          <YAxis
            tick={{ fill: 'var(--muted)', fontSize: 12 }}
            stroke="transparent"
            tickLine={false}
            width={36}
            tickFormatter={(v: number) => `${v > 0 ? '+' : ''}${v}`}
          />
          <Tooltip content={<GameTooltip />} cursor={{ fill: 'color-mix(in srgb, var(--ink) 5%, transparent)' }} />
          <ReferenceLine y={0} stroke="var(--baseline)" strokeWidth={1} />
          <Bar dataKey="bar" maxBarSize={18} isAnimationActive={false}>
            {data.map(g => (
              <Cell
                key={g.id}
                fill={g.known ? (g.bar >= 0 ? 'var(--lp-gain)' : 'var(--lp-loss)') : 'var(--ink-4)'}
                radius={(g.bar >= 0 ? [4, 4, 0, 0] : [0, 0, 4, 4]) as unknown as number}
              />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
      {unknowns > 0 && (
        <p className="mut sm-text" style={{ margin: '4px 0 0' }}>
          {unknowns} of {data.length} games have no attributed LP (gray stubs) — LP is only captured live.
        </p>
      )}
    </>
  )
}

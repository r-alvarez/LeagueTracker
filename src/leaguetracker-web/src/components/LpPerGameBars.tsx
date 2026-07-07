import { Bar, BarChart, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { LpPerGame } from '../types'

interface Props {
  games: LpPerGame[]
}

interface TooltipProps {
  active?: boolean
  payload?: Array<{ payload: LpPerGame }>
}

function GameTooltip({ active, payload }: TooltipProps) {
  if (!active || !payload?.length) return null
  const g = payload[0].payload
  const change = g.lpChange ?? 0
  return (
    <div className="viz-tooltip">
      <div className="v">{change > 0 ? '+' : ''}{change} LP</div>
      <div className="l">
        {g.champion} · {g.win ? 'Victory' : 'Defeat'} {g.kda}
        <br />
        {g.lpBefore} → {g.lpAfter}
        <br />
        {new Date(g.gameEndUtc).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}
      </div>
    </div>
  )
}

/// Gain/loss per game is polarity around zero - a diverging pair (blue gain,
/// red loss), rounded 4px at the data end, square at the baseline.
export default function LpPerGameBars({ games }: Props) {
  const data = games.filter(g => g.lpChange !== null).slice(0, 30).reverse()
  if (data.length === 0) {
    return <div className="empty">No attributed LP changes yet - they appear as ranked games are captured live.</div>
  }

  return (
    <ResponsiveContainer width="100%" height={260}>
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
        <Bar dataKey="lpChange" maxBarSize={18} isAnimationActive={false}>
          {data.map(g => (
            <Cell
              key={g.id}
              fill={(g.lpChange ?? 0) >= 0 ? 'var(--lp-gain)' : 'var(--lp-loss)'}
              radius={((g.lpChange ?? 0) >= 0 ? [4, 4, 0, 0] : [0, 0, 4, 4]) as unknown as number}
            />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  )
}

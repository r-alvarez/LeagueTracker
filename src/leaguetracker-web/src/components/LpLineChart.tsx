import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { LpPoint } from '../types'
import { rankLabel, rankTicks } from '../rank'

interface Props {
  points: LpPoint[]
}

interface TooltipProps {
  active?: boolean
  payload?: Array<{ payload: LpPoint & { t: number } }>
}

function LpTooltip({ active, payload }: TooltipProps) {
  if (!active || !payload?.length) return null
  const p = payload[0].payload
  // Back-filled history (imported from dpm.lol) has no cumulative win/loss
  // counters - it's a day's closing rank, not a live snapshot.
  const backfilled = p.wins + p.losses === 0
  return (
    <div className="viz-tooltip">
      <div className="v">{p.label}</div>
      <div className="l">
        {backfilled
          ? <>{new Date(p.timestampUtc).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })} · daily close (back-filled)</>
          : <>
              {new Date(p.timestampUtc).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}
              {' · '}{p.wins}W/{p.losses}L
            </>}
      </div>
    </div>
  )
}

export default function LpLineChart({ points }: Props) {
  if (points.length < 2) {
    return <div className="empty">Not enough LP snapshots yet - they accrue as games are captured.</div>
  }

  const data = points.map(p => ({ ...p, t: new Date(p.timestampUtc).getTime() }))
  const values = data.map(p => p.rankValue)
  const min = Math.min(...values)
  const max = Math.max(...values)
  const pad = Math.max(25, (max - min) * 0.15)

  return (
    <ResponsiveContainer width="100%" height={260}>
      <AreaChart data={data} margin={{ top: 8, right: 12, bottom: 0, left: 8 }}>
        <defs>
          <linearGradient id="lpFill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--series-1)" stopOpacity={0.22} />
            <stop offset="100%" stopColor="var(--series-1)" stopOpacity={0.02} />
          </linearGradient>
        </defs>
        <CartesianGrid stroke="var(--grid)" strokeWidth={1} vertical={false} />
        <XAxis
          dataKey="t"
          type="number"
          scale="time"
          domain={['dataMin', 'dataMax']}
          tickFormatter={(t: number) => new Date(t).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}
          tick={{ fill: 'var(--muted)', fontSize: 12 }}
          stroke="var(--baseline)"
          tickLine={false}
        />
        <YAxis
          domain={[min - pad, max + pad]}
          ticks={rankTicks(min - pad, max + pad)}
          tickFormatter={(v: number) => rankLabel(v, false)}
          tick={{ fill: 'var(--muted)', fontSize: 12 }}
          stroke="transparent"
          tickLine={false}
          width={92}
        />
        <Tooltip content={<LpTooltip />} cursor={{ stroke: 'var(--baseline)', strokeWidth: 1 }} />
        <Area
          type="monotone"
          dataKey="rankValue"
          stroke="var(--series-1)"
          strokeWidth={2}
          fill="url(#lpFill)"
          strokeLinejoin="round"
          strokeLinecap="round"
          activeDot={{ r: 5, fill: 'var(--series-1)', stroke: 'var(--surface)', strokeWidth: 2 }}
          isAnimationActive={false}
        />
      </AreaChart>
    </ResponsiveContainer>
  )
}

import { Bar, BarChart, CartesianGrid, Cell, Line, LineChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { SeriesPoint } from '../types'

interface TooltipProps {
  active?: boolean
  payload?: Array<{ payload: SeriesPoint }>
}

const fmtDate = (d: string) =>
  new Date(d).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })

function WinRateTooltip({ active, payload }: TooltipProps) {
  if (!active || !payload?.length) return null
  const p = payload[0].payload
  return (
    <div className="viz-tooltip">
      <div className="v">{p.rollingWinRate10}%</div>
      <div className="l">game {p.n} · {fmtDate(p.date)} · {p.win ? 'Victory' : 'Defeat'}</div>
    </div>
  )
}

export function RollingWinRateChart({ series }: { series: SeriesPoint[] }) {
  if (series.length < 3) return <div className="empty">Not enough games in this window.</div>
  return (
    <ResponsiveContainer width="100%" height={230}>
      <LineChart data={series} margin={{ top: 8, right: 12, bottom: 0, left: 0 }}>
        <CartesianGrid stroke="var(--grid)" strokeWidth={1} vertical={false} />
        <XAxis dataKey="n" tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="var(--baseline)" tickLine={false} />
        <YAxis domain={[0, 100]} ticks={[0, 25, 50, 75, 100]} tickFormatter={(v: number) => `${v}%`}
          tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="transparent" tickLine={false} width={44} />
        <Tooltip content={<WinRateTooltip />} cursor={{ stroke: 'var(--baseline)', strokeWidth: 1 }} />
        <ReferenceLine y={50} stroke="var(--baseline)" strokeDasharray="4 4" />
        <Line type="stepAfter" dataKey="rollingWinRate10" stroke="var(--chart-green)" strokeWidth={2}
          strokeLinejoin="round" strokeLinecap="round" dot={false}
          activeDot={{ r: 5, fill: 'var(--chart-green)', stroke: 'var(--surface)', strokeWidth: 2 }}
          isAnimationActive={false} />
      </LineChart>
    </ResponsiveContainer>
  )
}

function LaneGoldTooltip({ active, payload }: TooltipProps) {
  if (!active || !payload?.length) return null
  const p = payload[0].payload
  const v = p.laneGoldAt10 ?? 0
  return (
    <div className="viz-tooltip">
      <div className="v">{v > 0 ? '+' : ''}{v} gold @10</div>
      <div className="l">game {p.n} · {fmtDate(p.date)} · {p.win ? 'Victory' : 'Defeat'}{p.csAt10 !== null ? ` · ${p.csAt10} CS@10` : ''}</div>
    </div>
  )
}

/// Lead/deficit vs the lane opponent at 10:00 - polarity around zero, so a
/// diverging pair (blue ahead, red behind), rounded at the data end.
export function LaneGoldChart({ series }: { series: SeriesPoint[] }) {
  const data = series.filter(p => p.laneGoldAt10 !== null)
  if (data.length < 3) return <div className="empty">No laning data in this window (timelines required).</div>
  return (
    <ResponsiveContainer width="100%" height={230}>
      <BarChart data={data} margin={{ top: 8, right: 12, bottom: 0, left: 0 }} barCategoryGap={1}>
        <XAxis dataKey="n" tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="var(--baseline)" tickLine={false} />
        <YAxis tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="transparent" tickLine={false} width={48}
          tickFormatter={(v: number) => `${v > 0 ? '+' : ''}${v}`} />
        <Tooltip content={<LaneGoldTooltip />} cursor={{ fill: 'color-mix(in srgb, var(--ink) 5%, transparent)' }} />
        <ReferenceLine y={0} stroke="var(--baseline)" strokeWidth={1} />
        <Bar dataKey="laneGoldAt10" maxBarSize={12} isAnimationActive={false}>
          {data.map(p => (
            <Cell key={p.id}
              fill={(p.laneGoldAt10 ?? 0) >= 0 ? 'var(--lp-gain)' : 'var(--lp-loss)'}
              radius={(((p.laneGoldAt10 ?? 0) >= 0 ? [4, 4, 0, 0] : [0, 0, 4, 4]) as unknown) as number} />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  )
}

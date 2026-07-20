import { Area, AreaChart, Bar, CartesianGrid, Cell, ComposedChart, Line, ReferenceDot, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { SeriesPoint } from '../types'

interface TooltipProps {
  active?: boolean
  payload?: Array<{ payload: SeriesPoint & { roll: number | null } }>
}

const fmtDate = (d: string) =>
  new Date(d).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })

const signed = (v: number) => `${v > 0 ? '+' : ''}${v}`

// Adaptive smoothing: a 10-game window over 300 games is sawtooth noise, a
// 30-game window over 20 games is a flat line. Scale the window to the data
// so the line always shows drift, never sample jitter.
const rollWindow = (n: number) =>
  Math.min(n <= 80 ? 10 : n <= 200 ? 20 : 30, Math.max(3, Math.floor(n / 2)))

// The x-axis speaks dates, not game indices - "that dip was mid-June" is
// actionable, "that dip was game 143" is not. Marks stay equally spaced per
// game; ~6 evenly spread ticks carry the calendar.
function dateTicks(points: { n: number; date: string }[]) {
  const count = Math.min(6, points.length)
  const idx = Array.from({ length: count }, (_, i) => Math.round((i * (points.length - 1)) / (count - 1)))
  const ticks = [...new Set(idx)].map(i => points[i].n)
  const labels = new Map(idx.map(i => [points[i].n, fmtDate(points[i].date)]))
  return { ticks, label: (n: number) => labels.get(n) ?? '' }
}

// The coaching read: recent half vs earlier half of the window, in words.
// The chart shows the shape; this sentence says what to make of it.
function halves<T>(xs: T[]): [T[], T[]] {
  const mid = Math.floor(xs.length / 2)
  return [xs.slice(0, mid), xs.slice(mid)]
}

function Verdict({ word, cls, rest }: { word: string; cls: string; rest: string }) {
  return <div className="chart-verdict"><span className={cls}>{word}</span> — {rest}</div>
}

export function RollingWinRateChart({ series }: { series: SeriesPoint[] }) {
  if (series.length < 3) return <div className="empty">Not enough games in this window.</div>
  const k = rollWindow(series.length)

  // Full windows only: the old partial ramp-in opened every chart with a
  // meaningless 100%/0% spike from a 1-2 game sample.
  let wins = 0
  const data = series.map((p, i) => {
    wins += p.win ? 1 : 0
    if (i >= k) wins -= series[i - k].win ? 1 : 0
    return { ...p, roll: i >= k - 1 ? Math.round((100 * wins) / k) : null }
  }).filter((p): p is SeriesPoint & { roll: number } => p.roll !== null)
  const last = data[data.length - 1]
  const { ticks, label } = dateTicks(data)

  const [older, recent] = halves(series)
  const wrOf = (xs: SeriesPoint[]) => Math.round((100 * xs.filter(p => p.win).length) / xs.length)
  const delta = wrOf(recent) - wrOf(older)
  const word = delta >= 5 ? 'Trending up' : delta <= -5 ? 'Trending down' : 'Holding steady'
  const cls = delta >= 5 ? 'up' : delta <= -5 ? 'down' : 'steady'

  const WinRateTooltip = ({ active, payload }: TooltipProps) => {
    if (!active || !payload?.length) return null
    const p = payload[0].payload
    return (
      <div className="viz-tooltip">
        <div className="v">{p.roll}% <span className="mut">last {k}</span></div>
        <div className="l">game {p.n} · {fmtDate(p.date)} · {p.win ? 'Victory' : 'Defeat'}</div>
      </div>
    )
  }

  return (
    <>
      {older.length >= 5 && (
        <Verdict word={word} cls={cls}
          rest={`${wrOf(recent)}% over your last ${recent.length} vs ${wrOf(older)}% the ${older.length} before · ${k}-game rolling line`} />
      )}
      <ResponsiveContainer width="100%" height={230}>
        <AreaChart data={data} margin={{ top: 14, right: 40, bottom: 0, left: 0 }}>
          <defs>
            <linearGradient id="wrFill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="var(--chart-green)" stopOpacity={0.22} />
              <stop offset="100%" stopColor="var(--chart-green)" stopOpacity={0.02} />
            </linearGradient>
          </defs>
          <CartesianGrid stroke="var(--grid)" strokeWidth={1} vertical={false} />
          <XAxis dataKey="n" ticks={ticks} interval={0} tickFormatter={label}
            tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="var(--baseline)" tickLine={false} />
          <YAxis domain={[0, 100]} ticks={[0, 25, 50, 75, 100]} tickFormatter={(v: number) => `${v}%`}
            tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="transparent" tickLine={false} width={44} />
          <Tooltip content={<WinRateTooltip />} cursor={{ stroke: 'var(--baseline)', strokeWidth: 1 }} />
          <ReferenceLine y={50} stroke="var(--baseline)" strokeDasharray="4 4" />
          <Area type="monotone" dataKey="roll" stroke="var(--chart-green)" strokeWidth={2}
            fill="url(#wrFill)" strokeLinejoin="round" strokeLinecap="round"
            activeDot={{ r: 5, fill: 'var(--chart-green)', stroke: 'var(--surface)', strokeWidth: 2 }}
            isAnimationActive={false} />
          {/* The story's endpoint: where the form line sits right now. */}
          <ReferenceDot x={last.n} y={last.roll} r={4}
            fill="var(--chart-green)" stroke="var(--surface)" strokeWidth={2}
            label={{ value: `${last.roll}%`, position: 'right', fill: 'var(--ink)', fontSize: 12, fontWeight: 700 }} />
        </AreaChart>
      </ResponsiveContainer>
    </>
  )
}

/// Lead/deficit vs the lane opponent at 10:00. Per-game diverging bars carry
/// the variance (the -2500 disasters stay visible); a neutral rolling-average
/// line on top carries the story - is laning drifting better or worse. At
/// large windows the bars fade to a backdrop so the line reads first.
export function LaneGoldChart({ series }: { series: SeriesPoint[] }) {
  const laned = series.filter(p => p.laneGoldAt10 !== null)
  if (laned.length < 3) return <div className="empty">No laning data in this window (timelines required).</div>
  const k = rollWindow(laned.length)

  let sum = 0
  const data = laned.map((p, i) => {
    sum += p.laneGoldAt10 ?? 0
    if (i >= k) sum -= laned[i - k].laneGoldAt10 ?? 0
    return { ...p, roll: i >= k - 1 ? Math.round(sum / k) : null }
  })
  const faded = laned.length > 80
  const { ticks, label } = dateTicks(data)

  const [older, recent] = halves(laned)
  const avgOf = (xs: SeriesPoint[]) => Math.round(xs.reduce((s, p) => s + (p.laneGoldAt10 ?? 0), 0) / xs.length)
  const delta = avgOf(recent) - avgOf(older)
  const word = delta >= 75 ? 'Laning improving' : delta <= -75 ? 'Laning slipping' : 'Laning steady'
  const cls = delta >= 75 ? 'up' : delta <= -75 ? 'down' : 'steady'

  const LaneGoldTooltip = ({ active, payload }: TooltipProps) => {
    if (!active || !payload?.length) return null
    const p = payload[0].payload
    return (
      <div className="viz-tooltip">
        <div className="v">{signed(p.laneGoldAt10 ?? 0)} gold @10{p.roll !== null && <span className="mut"> · {signed(p.roll)} avg last {k}</span>}</div>
        <div className="l">game {p.n} · {fmtDate(p.date)} · {p.win ? 'Victory' : 'Defeat'}{p.csAt10 !== null ? ` · ${p.csAt10} CS@10` : ''}</div>
      </div>
    )
  }

  return (
    <>
      {older.length >= 5 && (
        <Verdict word={word} cls={cls}
          rest={`avg ${signed(avgOf(recent))} over your last ${recent.length} laned games vs ${signed(avgOf(older))} before · ${k}-game rolling line`} />
      )}
      <ResponsiveContainer width="100%" height={230}>
        <ComposedChart data={data} margin={{ top: 14, right: 12, bottom: 0, left: 0 }} barCategoryGap={1}>
          <XAxis dataKey="n" ticks={ticks} interval={0} tickFormatter={label}
            tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="var(--baseline)" tickLine={false} />
          <YAxis tick={{ fill: 'var(--muted)', fontSize: 12 }} stroke="transparent" tickLine={false} width={48}
            tickFormatter={(v: number) => signed(v)} />
          <Tooltip content={<LaneGoldTooltip />} cursor={{ fill: 'color-mix(in srgb, var(--ink) 5%, transparent)' }} />
          <ReferenceLine y={0} stroke="var(--baseline)" strokeWidth={1} />
          <Bar dataKey="laneGoldAt10" maxBarSize={12} isAnimationActive={false}>
            {data.map(p => (
              <Cell key={p.id} fillOpacity={faded ? 0.35 : 0.9}
                fill={(p.laneGoldAt10 ?? 0) >= 0 ? 'var(--lp-gain)' : 'var(--lp-loss)'}
                radius={(((p.laneGoldAt10 ?? 0) >= 0 ? [4, 4, 0, 0] : [0, 0, 4, 4]) as unknown) as number} />
            ))}
          </Bar>
          {/* Neutral ink, not a series hue: the line is a summary of the bars,
              not a second data series - it must not read as ahead/behind. */}
          <Line type="monotone" dataKey="roll" stroke="var(--ink-2)" strokeWidth={2}
            dot={false} activeDot={false} connectNulls={false} isAnimationActive={false} />
        </ComposedChart>
      </ResponsiveContainer>
    </>
  )
}

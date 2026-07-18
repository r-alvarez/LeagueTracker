import { Bar, BarChart, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { LpPerGame, LpPoint } from '../types'

interface Props {
  games: LpPerGame[]
  /// LP snapshots for the same queue - lets days without per-game LP derive
  /// their net from consecutive daily closes (the dpm.lol back-fill included).
  points?: LpPoint[]
}

/// Height of the neutral stub drawn for a day whose net LP is unknowable -
/// visible but clearly not a measurement.
const UNKNOWN_STUB = 4
/// How many most-recent playing days to show.
const DAYS = 21

type Source = 'games' | 'snapshots' | 'partial' | 'unknown'

interface DayRow {
  date: string
  label: string
  net: number
  source: Source
  known: number
  unknown: number
  wins: number
  losses: number
  games: LpPerGame[]
  bar: number
}

interface TooltipProps {
  active?: boolean
  payload?: Array<{ payload: DayRow }>
}

function DayTooltip({ active, payload }: TooltipProps) {
  if (!active || !payload?.length) return null
  const d = payload[0].payload
  return (
    <div className="viz-tooltip">
      <div className="v">
        {d.source === 'unknown' ? '? LP' : `${d.net > 0 ? '+' : ''}${d.net} LP`}
        <span className="mut"> · {d.wins}W-{d.losses}L</span>
      </div>
      <div className="l">
        {d.label}
        {d.games.slice(0, 8).map(g => (
          <span key={g.id}>
            <br />
            {g.champion} · {g.win ? 'W' : 'L'} {g.lpChange !== null ? `${g.lpChange > 0 ? '+' : ''}${g.lpChange}` : '?'}
          </span>
        ))}
        {d.games.length > 8 && <><br />… {d.games.length - 8} more</>}
        {d.source === 'snapshots' && <><br />day total from rank snapshots — per-game split unknown</>}
        {d.source === 'partial' && <><br />{d.unknown} game{d.unknown > 1 ? 's' : ''} without captured LP not counted</>}
      </div>
    </div>
  )
}

/// Net LP per playing day - the session view. Per-game LP is almost binary
/// (±20), so game-by-game bars just restate win/loss; summed by day the bars
/// gain real magnitude (a +60 evening vs a -50 tilt) and a date axis.
///
/// Day totals resolve in order of fidelity: exact per-game sums where live
/// capture got every game; otherwise the difference between that day's closing
/// rank snapshot and the previous played day's close (correct because snapshot
/// days without ranked games contribute nothing in between - this is what
/// back-fills the pre-tracker era); otherwise a partial sum; otherwise a
/// neutral stub.
export default function LpPerGameBars({ games, points = [] }: Props) {
  const byDay = new Map<string, LpPerGame[]>()
  for (const g of games) {
    const key = new Date(g.gameEndUtc).toLocaleDateString('sv')   // local YYYY-MM-DD, sorts naturally
    byDay.set(key, [...(byDay.get(key) ?? []), g])
  }

  // Last rankValue per calendar day, then each day's predecessor close.
  const closes = new Map<string, number>()
  for (const p of [...points].sort((a, b) => a.timestampUtc.localeCompare(b.timestampUtc))) {
    closes.set(new Date(p.timestampUtc).toLocaleDateString('sv'), p.rankValue)
  }
  const closeDates = [...closes.keys()].sort()
  const playedDates = [...byDay.keys()].sort()
  const derivedNet = (date: string): number | null => {
    if (!closes.has(date)) return null
    const i = closeDates.indexOf(date)
    if (i <= 0) return null
    // A close-to-close diff is only THIS day's LP if no ranked games were
    // played between the two closes - a played day without its own snapshot
    // in the gap would get its LP lumped into this bar.
    const prev = closeDates[i - 1]
    if (playedDates.some(d => d > prev && d < date)) return null
    return closes.get(date)! - closes.get(prev)!
  }

  const data: DayRow[] = [...byDay.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .slice(-DAYS)
    .map(([date, dayGames]) => {
      const knownGames = dayGames.filter(g => g.lpChange !== null)
      const gameSum = knownGames.reduce((s, g) => s + (g.lpChange ?? 0), 0)
      const wins = dayGames.filter(g => g.win).length
      const derived = knownGames.length < dayGames.length ? derivedNet(date) : null
      const [net, source]: [number, Source] =
        knownGames.length === dayGames.length ? [gameSum, 'games']
        : derived !== null ? [derived, 'snapshots']
        : knownGames.length > 0 ? [gameSum, 'partial']
        : [0, 'unknown']
      return {
        date,
        label: new Date(`${date}T12:00:00`).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }),
        net,
        source,
        known: knownGames.length,
        unknown: dayGames.length - knownGames.length,
        wins,
        losses: dayGames.length - wins,
        games: [...dayGames].sort((a, b) => a.gameEndUtc.localeCompare(b.gameEndUtc)),
        bar: source === 'unknown' ? (wins >= dayGames.length - wins ? UNKNOWN_STUB : -UNKNOWN_STUB) : net,
      }
    })
  if (data.length === 0) {
    return <div className="empty">No ranked games yet - bars appear as games are captured.</div>
  }
  const unknownDays = data.filter(d => d.source === 'unknown').length
  const snapshotDays = data.filter(d => d.source === 'snapshots').length
  const totalNet = data.reduce((s, d) => s + (d.source !== 'unknown' ? d.net : 0), 0)

  return (
    <>
      <ResponsiveContainer width="100%" height={242}>
        <BarChart data={data} margin={{ top: 8, right: 12, bottom: 0, left: 8 }} barCategoryGap={3}>
          <XAxis dataKey="label" tick={{ fill: 'var(--muted)', fontSize: 11 }} tickLine={false} stroke="var(--baseline)" />
          <YAxis
            tick={{ fill: 'var(--muted)', fontSize: 12 }}
            stroke="transparent"
            tickLine={false}
            width={36}
            tickFormatter={(v: number) => `${v > 0 ? '+' : ''}${v}`}
          />
          <Tooltip content={<DayTooltip />} cursor={{ fill: 'color-mix(in srgb, var(--ink) 5%, transparent)' }} />
          <ReferenceLine y={0} stroke="var(--baseline)" strokeWidth={1} />
          <Bar dataKey="bar" maxBarSize={22} isAnimationActive={false}>
            {data.map(d => (
              <Cell
                key={d.date}
                fill={d.source === 'unknown' ? 'var(--ink-4)' : d.bar >= 0 ? 'var(--lp-gain)' : 'var(--lp-loss)'}
                radius={(d.bar >= 0 ? [4, 4, 0, 0] : [0, 0, 4, 4]) as unknown as number}
              />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
      <p className="mut sm-text" style={{ margin: '4px 0 0' }}>
        Net {totalNet > 0 ? '+' : ''}{totalNet} LP over these days.
        {snapshotDays > 0 && <> {snapshotDays} day{snapshotDays > 1 ? 's' : ''} derived from daily rank snapshots.</>}
        {unknownDays > 0 && <> {unknownDays} day{unknownDays > 1 ? 's' : ''} (gray stubs) have no LP data at all.</>}
      </p>
    </>
  )
}

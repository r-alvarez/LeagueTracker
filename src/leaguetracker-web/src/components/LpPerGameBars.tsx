import { Bar, BarChart, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { LpPerGame } from '../types'

interface Props {
  games: LpPerGame[]
}

/// Height of the neutral stub drawn for a day where NO game has attributed LP -
/// visible but clearly not a measurement.
const UNKNOWN_STUB = 4
/// How many most-recent playing days to show.
const DAYS = 21

interface DayRow {
  date: string
  label: string
  net: number
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
        {d.known > 0 ? `${d.net > 0 ? '+' : ''}${d.net} LP` : '? LP'}
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
        {d.unknown > 0 && d.known > 0 && <><br />{d.unknown} game{d.unknown > 1 ? 's' : ''} without captured LP not counted</>}
      </div>
    </div>
  )
}

/// Net LP per playing day - the session view. Per-game LP is almost binary
/// (±20), so game-by-game bars just restate win/loss; summed by day the bars
/// gain real magnitude (a +60 evening vs a -50 tilt) and a date axis. Days
/// where nothing was captured live keep their slot as a neutral stub.
export default function LpPerGameBars({ games }: Props) {
  const byDay = new Map<string, LpPerGame[]>()
  for (const g of games) {
    const key = new Date(g.gameEndUtc).toLocaleDateString('sv')   // local YYYY-MM-DD, sorts naturally
    byDay.set(key, [...(byDay.get(key) ?? []), g])
  }
  const data: DayRow[] = [...byDay.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .slice(-DAYS)
    .map(([date, dayGames]) => {
      const knownGames = dayGames.filter(g => g.lpChange !== null)
      const net = knownGames.reduce((s, g) => s + (g.lpChange ?? 0), 0)
      const wins = dayGames.filter(g => g.win).length
      return {
        date,
        label: new Date(`${date}T12:00:00`).toLocaleDateString(undefined, { month: 'short', day: 'numeric' }),
        net,
        known: knownGames.length,
        unknown: dayGames.length - knownGames.length,
        wins,
        losses: dayGames.length - wins,
        games: [...dayGames].sort((a, b) => a.gameEndUtc.localeCompare(b.gameEndUtc)),
        bar: knownGames.length > 0 ? net : (wins >= dayGames.length - wins ? UNKNOWN_STUB : -UNKNOWN_STUB),
      }
    })
  if (data.length === 0) {
    return <div className="empty">No ranked games yet - bars appear as games are captured.</div>
  }
  const unknownDays = data.filter(d => d.known === 0).length
  const totalNet = data.reduce((s, d) => s + (d.known > 0 ? d.net : 0), 0)

  return (
    <>
      <ResponsiveContainer width="100%" height={unknownDays > 0 ? 242 : 260}>
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
                fill={d.known > 0 ? (d.bar >= 0 ? 'var(--lp-gain)' : 'var(--lp-loss)') : 'var(--ink-4)'}
                radius={(d.bar >= 0 ? [4, 4, 0, 0] : [0, 0, 4, 4]) as unknown as number}
              />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
      <p className="mut sm-text" style={{ margin: '4px 0 0' }}>
        Net {totalNet > 0 ? '+' : ''}{totalNet} LP over these days.
        {unknownDays > 0 && <> {unknownDays} day{unknownDays > 1 ? 's' : ''} (gray stubs) have no captured LP.</>}
      </p>
    </>
  )
}

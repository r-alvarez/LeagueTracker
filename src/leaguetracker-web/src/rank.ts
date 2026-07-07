// Mirror of the API's RankMath scale: Iron IV 0 LP = 0, each division 100,
// each tier 400, Master+ = 2800 + LP.
const TIERS = ['Iron', 'Bronze', 'Silver', 'Gold', 'Platinum', 'Emerald', 'Diamond']
const DIVISIONS = ['IV', 'III', 'II', 'I']

export function rankLabel(value: number, withLp = true): string {
  if (value >= 2800) return `Master+ ${Math.round(value - 2800)} LP`
  const v = Math.max(0, value)
  const tierIdx = Math.min(Math.floor(v / 400), TIERS.length - 1)
  const rem = v - tierIdx * 400
  const divIdx = Math.floor(rem / 100)
  const lp = Math.round(rem - divIdx * 100)
  const base = `${TIERS[tierIdx]} ${DIVISIONS[divIdx]}`
  return withLp ? `${base} ${lp} LP` : base
}

// Clean division boundaries for y-axis ticks (every 100 = one division).
export function rankTicks(min: number, max: number): number[] {
  const start = Math.floor(min / 100) * 100
  const end = Math.ceil(max / 100) * 100
  const step = end - start > 600 ? 200 : 100
  const ticks: number[] = []
  for (let v = start; v <= end; v += step) ticks.push(v)
  return ticks
}

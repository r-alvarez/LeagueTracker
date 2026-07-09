// Shared stat primitives - the visual vocabulary every page speaks: colored
// KDA, LP chips, rank-tier chips, winrate bars, proportional damage bars.

const TIER_CLASS: Record<string, string> = {
  IRON: 'tier-iron', BRONZE: 'tier-bronze', SILVER: 'tier-silver', GOLD: 'tier-gold',
  PLATINUM: 'tier-plat', EMERALD: 'tier-emerald', DIAMOND: 'tier-diamond',
  MASTER: 'tier-master', GRANDMASTER: 'tier-gm', CHALLENGER: 'tier-chall',
}

export const tierClass = (tierOrLabel: string | null | undefined): string =>
  tierOrLabel ? TIER_CLASS[tierOrLabel.trim().split(/\s+/)[0].toUpperCase()] ?? '' : ''

/// "GOLD IV 89 LP" -> "G4 · 89 LP"; keeps unknown labels as-is.
const shortRank = (label: string) => {
  const m = label.match(/^(\w+)\s+([IV]+)\s+(\d+)\s*LP?$/i)
  if (!m) return label
  const tier = m[1].toUpperCase()
  const letter = tier === 'GRANDMASTER' ? 'GM' : tier[0]
  const div = { I: '1', II: '2', III: '3', IV: '4' }[m[2].toUpperCase()] ?? m[2]
  return `${letter}${div} · ${m[3]} LP`
}

export function RankChip({ label, full }: { label: string | null | undefined; full?: boolean }) {
  if (!label) return <span className="mut">—</span>
  return <span className={`rank-chip ${tierClass(label)}`}>{full ? label : shortRank(label)}</span>
}

const ratioClass = (r: number | null) =>
  r === null ? 'kda-perfect' : r >= 5 ? 'kda-5' : r >= 4 ? 'kda-4' : r >= 3 ? 'kda-3' : r < 1 ? 'kda-low' : ''

export function KdaStat({ kills, deaths, assists, kp }: { kills: number; deaths: number; assists: number; kp?: number | null }) {
  const ratio = deaths === 0 ? null : (kills + assists) / deaths
  return (
    <span className="kda-block">
      <span className="kda-line">
        {kills} <span className="kda-sep">/</span> <span className="kda-deaths">{deaths}</span> <span className="kda-sep">/</span> {assists}
      </span>
      <span className={`kda-ratio ${ratioClass(ratio)}`}>
        {ratio === null ? 'Perfect' : `${ratio.toFixed(2)} KDA`}
        {kp !== null && kp !== undefined && <span className="mut"> · {Math.round(kp * 100)}% KP</span>}
      </span>
    </span>
  )
}

export function LpChip({ change }: { change: number | null }) {
  if (change === null) return <span className="mut sm-text">LP —</span>
  return (
    <span className={`lp-chip ${change >= 0 ? 'gain' : 'loss'}`}>
      {change >= 0 ? '▲' : '▼'} {Math.abs(change)} LP
    </span>
  )
}

export function WinrateBar({ wins, losses }: { wins: number; losses: number }) {
  const total = wins + losses
  const pct = total > 0 ? (100 * wins) / total : 0
  return (
    <span className="wr-wrap" title={`${wins}W ${losses}L`}>
      <span className="wr-bar"><span className="fill" style={{ width: `${pct}%` }} /></span>
      <span className={`wr-pct ${pct >= 55 ? 'win' : pct < 48 && total >= 5 ? 'loss' : ''}`}>{Math.round(pct)}%</span>
    </span>
  )
}

export function DamageBar({ value, max, tone = 'var(--lp-loss)' }: { value: number; max: number; tone?: string }) {
  return (
    <span className="dmg-wrap">
      <span className="dmg-num">{value.toLocaleString()}</span>
      <span className="dmg-bar"><span className="fill" style={{ width: `${max > 0 ? Math.round((100 * value) / max) : 0}%`, background: tone }} /></span>
    </span>
  )
}

export function FormDots({ results }: { results: boolean[] }) {
  return (
    <span className="form-dots" title={results.map(w => (w ? 'W' : 'L')).join('')}>
      {results.map((w, i) => <span key={i} className={w ? 'dot win-dot' : 'dot loss-dot'} />)}
    </span>
  )
}

/// "16 h ago" / "3 d ago" style timestamps; exact date in the title attribute.
export function RelTime({ date }: { date: string }) {
  const then = new Date(date)
  const mins = Math.max(0, Math.floor((Date.now() - then.getTime()) / 60_000))
  const text = mins < 60 ? `${mins}m ago`
    : mins < 48 * 60 ? `${Math.round(mins / 60)}h ago`
    : mins < 14 * 24 * 60 ? `${Math.round(mins / 60 / 24)}d ago`
    : then.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  return <span title={then.toLocaleString()}>{text}</span>
}

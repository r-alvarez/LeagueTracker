import type { ReactNode } from 'react'
import { useLoadoutIcons } from '../champions'

/// In-game-style hover card: trigger wraps the icon, the card floats above.
/// Pure CSS positioning - no portal - so keep triggers out of clipped scrollers.
export function RichTip({ children, tip }: { children: ReactNode; tip: ReactNode | null }) {
  if (tip === null) return <>{children}</>
  return (
    <span className="rich-tip">
      {children}
      <span className="rich-tip-card" role="tooltip">{tip}</span>
    </span>
  )
}

/// Item slot with the dpm.lol-style tooltip: name, cost, stat lines, passive.
export function ItemIcon({ id, size, dim }: { id: number; size?: number; dim?: boolean }) {
  const icons = useLoadoutIcons()
  const url = icons.item(id)
  const info = icons.itemInfo(id)
  const style = size ? { width: size, height: size } : undefined

  const tip = id > 0 ? (
    <>
      <span className="tip-title">{info?.name ?? `Item ${id}`}{info && <span className="tip-gold"> {info.gold}g</span>}</span>
      {info?.stats.map((s, i) => <span key={i} className="tip-stat">{s}</span>)}
      {info?.passive && <span className="tip-passive">{info.passive.length > 260 ? `${info.passive.slice(0, 260)}…` : info.passive}</span>}
    </>
  ) : null

  return (
    <RichTip tip={tip}>
      <span className={`slot ${dim ? 'dim' : ''}`} style={style}>
        {id > 0 && url && <img src={url} alt={info?.name ?? ''} loading="lazy" />}
      </span>
    </RichTip>
  )
}

/// Rune / stat-shard icon with name + short description on hover.
export function PerkIcon({ id, className = '' }: { id: number; className?: string }) {
  const icons = useLoadoutIcons()
  const perk = icons.perk(id) ?? icons.rune(id)
  const desc = perk && 'desc' in perk ? perk.desc : ''

  const tip = perk ? (
    <>
      <span className="tip-title">{perk.name}</span>
      {desc && <span className="tip-passive">{desc}</span>}
    </>
  ) : null

  return (
    <RichTip tip={tip}>
      <span className={`slot round ${className}`}>
        {perk && <img src={perk.icon} alt={perk.name} loading="lazy" />}
      </span>
    </RichTip>
  )
}

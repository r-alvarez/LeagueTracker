import type { ReactNode } from 'react'
import { useLoadoutIcons, type UnitKind } from '../champions'

/// Glyphs for non-champion damage sources (turrets, minions, jungle monsters)
/// - drawn inline so no asset-naming dependency on Riot's CDNs.
export function UnitGlyph({ kind, size = 28 }: { kind: UnitKind; size?: number }) {
  return (
    <span className="unit-glyph" style={{ width: size, height: size }} title={kind}>
      <svg viewBox="0 0 24 24" width={size - 8} height={size - 8} fill="currentColor" aria-hidden>
        {kind === 'turret' && (
          <path d="M8 2h8l-1 4h2v4h-2.5l1.5 9h2v3H6v-3h2l1.5-9H7V6h2L8 2Zm3 4h2l.5-2h-3l.5 2Z" />
        )}
        {kind === 'minions' && (
          <>
            <circle cx="7" cy="9" r="3.4" />
            <circle cx="17" cy="9" r="3.4" />
            <circle cx="12" cy="17" r="3.8" />
          </>
        )}
        {kind === 'monster' && (
          <path d="M3 3c3 4 5 5 9 5s6-1 9-5c-1 6-3 9-5 11l1 7-5-4-5 4 1-7C6 12 4 9 3 3Z" />
        )}
      </svg>
    </span>
  )
}

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
      <span className="tip-head">
        {url ? <img className="tip-icon" src={url} alt="" loading="lazy" /> : null}
        <span className="tip-title">{info?.name ?? `Item ${id}`}{info ? <span className="tip-gold"> {info.gold}g</span> : null}</span>
      </span>
      {info?.stats.map((s, i) => <span key={i} className="tip-stat">{s}</span>)}
      {info?.passive ? <span className="tip-passive">{info.passive.length > 260 ? `${info.passive.slice(0, 260)}…` : info.passive}</span> : null}
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
  const perk = icons.perk(id)
  const fallback = icons.rune(id)
  const name = perk?.name ?? fallback?.name
  const icon = perk?.icon ?? fallback?.icon
  const desc = perk?.desc ?? ''

  const tip = name ? (
    <>
      <span className="tip-head">
        {icon ? <img className="tip-icon round" src={icon} alt="" loading="lazy" /> : null}
        <span className="tip-title">{name}</span>
      </span>
      {desc ? <span className="tip-passive">{desc}</span> : null}
    </>
  ) : null

  return (
    <RichTip tip={tip}>
      <span className={`slot round ${className}`}>
        {icon ? <img src={icon} alt={name ?? ''} loading="lazy" /> : null}
      </span>
    </RichTip>
  )
}

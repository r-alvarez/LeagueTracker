import { useState } from 'react'

// League's standard position glyphs. CommunityDragon serves the client's own
// SVGs; on any load failure we fall back to a two-letter tag so rows never break.
const ROLE_SVG: Record<string, string> = {
  TOP: 'position-top',
  JUNGLE: 'position-jungle',
  MIDDLE: 'position-middle',
  BOTTOM: 'position-bottom',
  UTILITY: 'position-utility',
}

const SHORT: Record<string, string> = {
  TOP: 'TOP', JUNGLE: 'JGL', MIDDLE: 'MID', BOTTOM: 'ADC', UTILITY: 'SUP',
}

export default function RoleIcon({ role, size = 16 }: { role: string; size?: number }) {
  const [failed, setFailed] = useState(false)
  const slug = ROLE_SVG[role]
  if (!slug || failed) {
    return role ? <span className="role-tag">{SHORT[role] ?? role.slice(0, 3)}</span> : null
  }
  return (
    <img
      className="role-icon"
      style={{ width: size, height: size }}
      src={`https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-static-assets/global/default/svg/${slug}.svg`}
      alt={SHORT[role] ?? role}
      title={SHORT[role] ?? role}
      loading="lazy"
      onError={() => setFailed(true)}
    />
  )
}

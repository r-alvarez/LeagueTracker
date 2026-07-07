import { useEffect, useMemo, useState } from 'react'

type IconMap = Record<string, string>

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, '')

// Session-wide cache so the DataDragon fetch happens at most once, no matter
// how many rows ask for an icon. `inflight` dedupes concurrent first-callers.
let cache: IconMap | null = null
let inflight: Promise<IconMap> | null = null

async function load(): Promise<IconMap> {
  const versions = (await fetch('https://ddragon.leagueoflegends.com/api/versions.json').then(r => r.json())) as string[]
  const v = versions[0]
  const champ = (await fetch(`https://ddragon.leagueoflegends.com/cdn/${v}/data/en_US/champion.json`).then(r => r.json())) as {
    data: Record<string, { id: string; name: string }>
  }
  const map: IconMap = {}
  for (const c of Object.values(champ.data)) {
    const url = `https://ddragon.leagueoflegends.com/cdn/${v}/img/champion/${c.id}.png`
    map[norm(c.name)] = url // display name, e.g. "Nunu & Willump"
    map[norm(c.id)] = url // image id, e.g. "MonkeyKing" (Wukong)
  }
  return map
}

// Resolves a champion name to its DataDragon square-icon URL. Fetched once and
// cached; on any failure (offline, CDN down) every lookup returns null so
// callers fall back to a monogram — the UI never breaks.
export function useChampionIcons(): (name: string) => string | null {
  const [map, setMap] = useState<IconMap | null>(cache)

  useEffect(() => {
    if (cache) return
    if (!inflight) inflight = load().then(m => (cache = m)).catch(() => (cache = {}))
    let alive = true
    void inflight.then(m => alive && setMap(m))
    return () => { alive = false }
  }, [])

  return useMemo(() => (name: string) => map?.[norm(name)] ?? null, [map])
}

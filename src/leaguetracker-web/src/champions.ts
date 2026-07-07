import { useEffect, useMemo, useState } from 'react'

interface Assets {
  version: string
  champs: Record<string, string>
  spells: Record<number, string>
}

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, '')

// Session-wide cache so the DataDragon fetches happen at most once, no matter
// how many rows ask for an icon. `inflight` dedupes concurrent first-callers.
let cache: Assets | null = null
let inflight: Promise<Assets> | null = null

async function load(): Promise<Assets> {
  const versions = (await fetch('https://ddragon.leagueoflegends.com/api/versions.json').then(r => r.json())) as string[]
  const v = versions[0]
  const cdn = `https://ddragon.leagueoflegends.com/cdn/${v}`

  const champ = (await fetch(`${cdn}/data/en_US/champion.json`).then(r => r.json())) as {
    data: Record<string, { id: string; name: string }>
  }
  const champs: Record<string, string> = {}
  for (const c of Object.values(champ.data)) {
    const url = `${cdn}/img/champion/${c.id}.png`
    champs[norm(c.name)] = url // display name, e.g. "Nunu & Willump"
    champs[norm(c.id)] = url // image id, e.g. "MonkeyKing" (Wukong)
  }

  const summ = (await fetch(`${cdn}/data/en_US/summoner.json`).then(r => r.json())) as {
    data: Record<string, { key: string; image: { full: string } }>
  }
  const spells: Record<number, string> = {}
  for (const s of Object.values(summ.data)) {
    spells[parseInt(s.key, 10)] = `${cdn}/img/spell/${s.image.full}`
  }

  return { version: v, champs, spells }
}

function useAssets(): Assets | null {
  const [assets, setAssets] = useState<Assets | null>(cache)
  useEffect(() => {
    if (cache) return
    if (!inflight) inflight = load().then(a => (cache = a)).catch(() => (cache = { version: '', champs: {}, spells: {} }))
    let alive = true
    void inflight.then(a => alive && setAssets(a))
    return () => { alive = false }
  }, [])
  return assets
}

// Resolvers return null on any failure (offline, CDN down) so callers fall
// back to monograms/empty slots - the UI never breaks.
export function useChampionIcons(): (name: string) => string | null {
  const assets = useAssets()
  return useMemo(() => (name: string) => assets?.champs[norm(name)] ?? null, [assets])
}

export function useLoadoutIcons(): {
  item: (id: number) => string | null
  spell: (id: number) => string | null
} {
  const assets = useAssets()
  return useMemo(() => ({
    item: (id: number) => (assets && assets.version && id > 0
      ? `https://ddragon.leagueoflegends.com/cdn/${assets.version}/img/item/${id}.png`
      : null),
    spell: (id: number) => assets?.spells[id] ?? null,
  }), [assets])
}

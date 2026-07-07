import { useEffect, useMemo, useState } from 'react'

interface Assets {
  version: string
  champs: Record<string, string>
  spells: Record<number, string>
  runes: Record<number, { icon: string; name: string }>
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

  // Rune trees: styles and every perk, keyed by id. Icon paths are served from
  // the version-less img root.
  const trees = (await fetch(`${cdn}/data/en_US/runesReforged.json`).then(r => r.json())) as Array<{
    id: number; icon: string; name: string
    slots: Array<{ runes: Array<{ id: number; icon: string; name: string }> }>
  }>
  const runes: Record<number, { icon: string; name: string }> = {}
  for (const tree of trees) {
    runes[tree.id] = { icon: `https://ddragon.leagueoflegends.com/cdn/img/${tree.icon}`, name: tree.name }
    for (const slot of tree.slots) {
      for (const r of slot.runes) {
        runes[r.id] = { icon: `https://ddragon.leagueoflegends.com/cdn/img/${r.icon}`, name: r.name }
      }
    }
  }

  return { version: v, champs, spells, runes }
}

// Stat shards aren't in runesReforged - small stable set, text is enough.
export const STAT_SHARDS: Record<number, string> = {
  5001: 'HP scaling', 5005: 'Attack speed', 5007: 'Ability haste', 5008: 'Adaptive force',
  5010: 'Move speed', 5011: 'Health', 5013: 'Tenacity',
}

function useAssets(): Assets | null {
  const [assets, setAssets] = useState<Assets | null>(cache)
  useEffect(() => {
    if (cache) return
    if (!inflight) inflight = load().then(a => (cache = a)).catch(() => (cache = { version: '', champs: {}, spells: {}, runes: {} }))
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
  rune: (id: number) => { icon: string; name: string } | null
} {
  const assets = useAssets()
  return useMemo(() => ({
    item: (id: number) => (assets && assets.version && id > 0
      ? `https://ddragon.leagueoflegends.com/cdn/${assets.version}/img/item/${id}.png`
      : null),
    spell: (id: number) => assets?.spells[id] ?? null,
    rune: (id: number) => assets?.runes[id] ?? null,
  }), [assets])
}

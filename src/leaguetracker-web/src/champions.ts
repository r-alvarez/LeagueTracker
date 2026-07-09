import { useEffect, useMemo, useState } from 'react'

export interface ItemInfo {
  name: string
  gold: number
  stats: string[]
  passive: string
}

export interface PerkInfo {
  icon: string
  name: string
  desc: string
}

interface Assets {
  version: string
  champs: Record<string, string>
  champNames: Record<number, string>
  champIds: Record<string, string>
  spells: Record<number, string>
  runes: Record<number, { icon: string; name: string }>
  perks: Record<number, PerkInfo>
  items: Record<number, ItemInfo>
}

const stripTags = (s: string) => s.replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]+>/g, '').replace(/&nbsp;/g, ' ').trim()

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
    data: Record<string, { id: string; name: string; key: string }>
  }
  const champs: Record<string, string> = {}
  const champNames: Record<number, string> = {}
  const champIds: Record<string, string> = {}
  for (const c of Object.values(champ.data)) {
    const url = `${cdn}/img/champion/${c.id}.png`
    champs[norm(c.name)] = url // display name, e.g. "Nunu & Willump"
    champs[norm(c.id)] = url // image id, e.g. "MonkeyKing" (Wukong)
    champNames[parseInt(c.key, 10)] = c.name // numeric id (spectator only sends these)
    champIds[norm(c.name)] = c.id // display name -> DDragon id, for per-champion data fetches
    champIds[norm(c.id)] = c.id
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

  // CommunityDragon perks: EVERY perk id - keystones, minor runes AND stat
  // shards (which runesReforged lacks) - with names and short descriptions.
  const perks: Record<number, PerkInfo> = {}
  try {
    const cdragon = 'https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/'
    const raw = (await fetch(`${cdragon}v1/perks.json`).then(r => r.json())) as Array<{
      id: number; name: string; iconPath: string; shortDesc: string
    }>
    for (const p of raw) {
      perks[p.id] = {
        icon: cdragon + p.iconPath.replace(/^\/lol-game-data\/assets\//i, '').toLowerCase(),
        name: p.name,
        desc: stripTags(p.shortDesc ?? ''),
      }
    }
  } catch { /* tooltips degrade to runesReforged names */ }

  // Item names, cost and stat lines for in-game-style tooltips.
  const items: Record<number, ItemInfo> = {}
  try {
    const itemData = (await fetch(`${cdn}/data/en_US/item.json`).then(r => r.json())) as {
      data: Record<string, { name: string; gold: { total: number }; description: string }>
    }
    for (const [id, item] of Object.entries(itemData.data)) {
      const statsMatch = item.description.match(/<stats>([\s\S]*?)<\/stats>/i)
      const stats = statsMatch ? stripTags(statsMatch[1]).split('\n').map(s => s.trim()).filter(Boolean) : []
      const passive = stripTags(item.description.replace(/<stats>[\s\S]*?<\/stats>/i, ''))
      items[parseInt(id, 10)] = { name: item.name, gold: item.gold.total, stats, passive }
    }
  } catch { /* item tooltips degrade to ids */ }

  return { version: v, champs, champNames, champIds, spells, runes, perks, items }
}

// Per-champion ability maps for the death recap: internal lowercase spell id
// ("vaynesilveredbolts") -> "W · Silver Bolts". Fetched lazily per champion.
const abilityCache: Record<string, Record<string, string>> = {}
const abilityInflight: Record<string, Promise<void>> = {}

async function loadAbilities(version: string, ddragonId: string): Promise<void> {
  try {
    const raw = (await fetch(`https://ddragon.leagueoflegends.com/cdn/${version}/data/en_US/champion/${ddragonId}.json`)
      .then(r => r.json())) as {
      data: Record<string, { spells: Array<{ id: string; name: string }>; passive: { name: string } }>
    }
    const champ = raw.data[ddragonId]
    const map: Record<string, string> = {}
    champ.spells.forEach((s, i) => { map[s.id.toLowerCase()] = `${'QWER'[i]} · ${s.name}` })
    map.passive = champ.passive.name
    abilityCache[ddragonId] = map
  } catch {
    abilityCache[ddragonId] = {}
  }
}

const SPECIAL_SPELLS: Record<string, string> = {
  summonerdot: 'Ignite', summonerexhaust: 'Exhaust', burning: 'Burn',
  attack: 'Basic attack', turretbasicattack: 'Turret shot',
}

/// Labels damage-recap spell names for the given source champions. Triggers the
/// per-champion fetches and re-renders as they land; unresolvable names fall
/// back to a cleaned-up version of the raw id.
export function useAbilityLabels(sources: string[]): (source: string, spellName: string) => string {
  const assets = useAssets()
  const [, bump] = useState(0)

  useEffect(() => {
    if (!assets?.version) return
    for (const source of sources) {
      const id = assets.champIds[norm(source)]
      if (!id || abilityCache[id]) continue
      abilityInflight[id] ??= loadAbilities(assets.version, id).then(() => bump(t => t + 1))
    }
  }, [assets, sources])

  return useMemo(() => (source: string, spellName: string) => {
    const n = spellName.toLowerCase().trim()
    if (!n) return 'Basic attacks'
    const s = norm(source)
    if (n === `${s}basicattack` || n === 'basicattack') return 'Basic attack'
    if (SPECIAL_SPELLS[n]) return SPECIAL_SPELLS[n]

    const id = assets?.champIds[s]
    const fromData = id ? abilityCache[id]?.[n] : undefined
    if (fromData) return fromData

    // "gragasq" -> "Q" even when the spell id spells it differently.
    if (n.startsWith(s)) {
      const rest = n.slice(s.length)
      if (/^[qwer]$/.test(rest)) return rest.toUpperCase()
      if (rest.length > 1) return rest[0].toUpperCase() + rest.slice(1)
    }
    return spellName
  }, [assets])
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
    if (!inflight) inflight = load().then(a => (cache = a)).catch(() => (cache = { version: '', champs: {}, champNames: {}, champIds: {}, spells: {}, runes: {}, perks: {}, items: {} }))
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

export function useChampionNames(): (id: number) => string | null {
  const assets = useAssets()
  return useMemo(() => (id: number) => assets?.champNames[id] ?? null, [assets])
}

export function useLoadoutIcons(): {
  item: (id: number) => string | null
  itemInfo: (id: number) => ItemInfo | null
  spell: (id: number) => string | null
  rune: (id: number) => { icon: string; name: string } | null
  perk: (id: number) => PerkInfo | null
} {
  const assets = useAssets()
  return useMemo(() => ({
    item: (id: number) => (assets && assets.version && id > 0
      ? `https://ddragon.leagueoflegends.com/cdn/${assets.version}/img/item/${id}.png`
      : null),
    itemInfo: (id: number) => assets?.items[id] ?? null,
    spell: (id: number) => assets?.spells[id] ?? null,
    // Prefer the CDragon perk (covers stat shards and never has gaps);
    // runesReforged stays as fallback while perks.json is loading.
    rune: (id: number) => assets?.perks[id] ?? assets?.runes[id] ?? null,
    perk: (id: number) => assets?.perks[id] ?? null,
  }), [assets])
}

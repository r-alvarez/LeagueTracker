// Static-data names for the ids the API stores. Ids are stable across patches;
// anything unknown falls back to the raw id.
const SUMMONER_SPELLS: Record<number, string> = {
  1: 'Cleanse', 3: 'Exhaust', 4: 'Flash', 6: 'Ghost', 7: 'Heal', 11: 'Smite',
  12: 'Teleport', 13: 'Clarity', 14: 'Ignite', 21: 'Barrier', 32: 'Snowball',
}

const KEYSTONES: Record<number, string> = {
  8005: 'Press the Attack', 8008: 'Lethal Tempo', 8010: 'Conqueror', 8021: 'Fleet Footwork',
  8112: 'Electrocute', 8128: 'Dark Harvest', 9923: 'Hail of Blades',
  8214: 'Summon Aery', 8229: 'Arcane Comet', 8230: 'Phase Rush',
  8437: 'Grasp of the Undying', 8439: 'Aftershock', 8465: 'Guardian',
  8351: 'Glacial Augment', 8360: 'Unsealed Spellbook', 8369: 'First Strike',
}

export const summonerSpell = (id: number) => SUMMONER_SPELLS[id] ?? `#${id}`
export const keystone = (id: number) => KEYSTONES[id] ?? (id > 0 ? `#${id}` : '—')

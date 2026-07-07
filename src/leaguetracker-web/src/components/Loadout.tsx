import { useLoadoutIcons } from '../champions'
import { summonerSpell } from '../lookups'

interface Props {
  items: string | null
  summoner1Id: number | null
  summoner2Id: number | null
}

/// The per-game icon strip: two summoner spells, six item slots, trinket.
/// Empty slots render as quiet squares so rows keep a steady rhythm.
export default function Loadout({ items, summoner1Id, summoner2Id }: Props) {
  const icons = useLoadoutIcons()
  const itemIds = (items ?? '').split(',').map(s => parseInt(s, 10) || 0)
  while (itemIds.length < 7) itemIds.push(0)

  const slot = (key: string, url: string | null, title: string) => (
    <span key={key} className="slot" title={title}>
      {url && <img src={url} alt="" loading="lazy" />}
    </span>
  )

  const showSumms = summoner1Id !== null || summoner2Id !== null
  return (
    <span className="loadout">
      {showSumms && (
        <span className="slots">
          {[summoner1Id, summoner2Id].map((id, i) =>
            slot(`s${i}`, id ? icons.spell(id) : null, id ? summonerSpell(id) : ''))}
        </span>
      )}
      <span className="slots">
        {itemIds.slice(0, 6).map((id, i) => slot(`i${i}`, icons.item(id), id ? `Item ${id}` : ''))}
        {slot('trinket', icons.item(itemIds[6]), itemIds[6] ? 'Trinket' : '')}
      </span>
    </span>
  )
}

import { useLoadoutIcons } from '../champions'
import { summonerSpell } from '../lookups'
import { ItemIcon } from './GameIcons'

interface Props {
  items: string | null
  summoner1Id: number | null
  summoner2Id: number | null
}

/// The per-game icon strip: two summoner spells, six item slots, trinket.
/// Empty slots render as quiet squares so rows keep a steady rhythm; items
/// carry the in-game-style hover card.
export default function Loadout({ items, summoner1Id, summoner2Id }: Props) {
  const icons = useLoadoutIcons()
  const itemIds = (items ?? '').split(',').map(s => parseInt(s, 10) || 0)
  while (itemIds.length < 7) itemIds.push(0)

  const showSumms = summoner1Id !== null || summoner2Id !== null
  return (
    <span className="loadout">
      {showSumms && (
        <span className="slots">
          {[summoner1Id, summoner2Id].map((id, i) => (
            <span key={`s${i}`} className="slot" title={id ? summonerSpell(id) : ''}>
              {id !== null && id > 0 && icons.spell(id) && <img src={icons.spell(id)!} alt="" loading="lazy" />}
            </span>
          ))}
        </span>
      )}
      <span className="slots">
        {itemIds.slice(0, 7).map((id, i) => <ItemIcon key={`i${i}`} id={id} />)}
      </span>
    </span>
  )
}

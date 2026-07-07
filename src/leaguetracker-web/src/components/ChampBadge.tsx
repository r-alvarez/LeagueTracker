import { useChampionIcons } from '../champions'

interface Props {
  name: string
  small?: boolean
  sub?: string
}

export default function ChampBadge({ name, small, sub }: Props) {
  const icon = useChampionIcons()(name)
  const mono = name.slice(0, 2).toUpperCase()
  return (
    <span className={small ? 'champ sm' : 'champ'}>
      {icon
        ? <img className="champ-img" src={icon} alt="" loading="lazy" />
        : <span className="champ-mono">{mono}</span>}
      <span className="champ-name">{name}</span>
      {sub && <span className="mut">{sub}</span>}
    </span>
  )
}

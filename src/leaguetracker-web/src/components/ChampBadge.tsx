import { useChampionIcons } from '../champions'

interface Props {
  name: string
  small?: boolean
  sub?: string
  iconOnly?: boolean
}

export default function ChampBadge({ name, small, sub, iconOnly }: Props) {
  const icon = useChampionIcons()(name)
  const mono = name.slice(0, 2).toUpperCase()
  return (
    <span className={small ? 'champ sm' : 'champ'} title={iconOnly ? name : undefined}>
      {icon
        ? <img className="champ-img" src={icon} alt={iconOnly ? name : ''} loading="lazy" />
        : <span className="champ-mono">{mono}</span>}
      {!iconOnly && <span className="champ-name">{name}</span>}
      {!iconOnly && sub && <span className="mut">{sub}</span>}
    </span>
  )
}

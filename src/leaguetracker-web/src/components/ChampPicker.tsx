import { useEffect, useRef, useState } from 'react'
import { useChampionIcons } from '../champions'
import type { ChampionFacet } from '../types'

/// dpm-style champion filter: a portrait button that opens a searchable list.
/// Options come from the facets endpoint, so only champions with games appear.
export default function ChampPicker({ placeholder, value, options, onChange }: {
  placeholder: string
  value: string
  options: ChampionFacet[]
  onChange: (name: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const wrap = useRef<HTMLDivElement>(null)
  const icons = useChampionIcons()

  useEffect(() => {
    const close = (e: MouseEvent) => {
      if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [])

  const shown = options.filter(o => o.name.toLowerCase().includes(search.toLowerCase()))
  const icon = value ? icons(value) : null

  return (
    <div className="champ-picker" ref={wrap}>
      <button className={`cp-face ${value ? 'set' : ''}`} title={value || placeholder}
        onClick={() => { setOpen(o => !o); setSearch('') }}>
        {icon ? <img src={icon} alt={value} /> : <span className="cp-q">{value ? value.slice(0, 2) : '?'}</span>}
      </button>
      {value && <button className="cp-clear" title="Clear" onClick={() => onChange('')}>×</button>}
      {open && (
        <div className="cp-drop">
          <input autoFocus placeholder="Search champion…" value={search} onChange={e => setSearch(e.target.value)} />
          <div className="cp-list">
            {shown.map(o => {
              const i = icons(o.name)
              return (
                <button key={o.name} onClick={() => { onChange(o.name); setOpen(false) }}>
                  {i ? <img src={i} alt="" loading="lazy" /> : <span className="cp-q sm">{o.name.slice(0, 1)}</span>}
                  <span className="cp-name">{o.name}</span>
                  <span className="mut sm-text">{o.count}</span>
                </button>
              )
            })}
            {shown.length === 0 && <span className="cp-none mut sm-text">No champion matches</span>}
          </div>
        </div>
      )}
    </div>
  )
}

import type { AdminUser } from '../types'

/// The admin's hand for "whose is this": a select over the people who have
/// signed in (or were named in configuration) - exactly the ones the server
/// will accept - plus "nobody". Value is the owner's email, '' for unowned.
export default function OwnerSelect({ users, value, onChange, disabled }: {
  users: AdminUser[]; value: string; onChange: (email: string) => void; disabled?: boolean
}) {
  // A current owner missing from the list (the list is still loading, or a
  // configured email that never signed in) keeps its option so the select
  // does not silently show "nobody".
  const known = users.some(u => u.email === value)
  return (
    <select className="select" value={value} disabled={disabled} onChange={e => onChange(e.target.value)} aria-label="Owner">
      <option value="">— nobody —</option>
      {!known && value && <option value={value}>{value}</option>}
      {users.map(u => (
        <option key={u.id} value={u.email}>{u.displayName && u.displayName !== u.email ? `${u.displayName} · ${u.email}` : u.email}</option>
      ))}
    </select>
  )
}

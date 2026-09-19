import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import { auth } from '../auth'
import type { RenderAlert } from '../types'

const POLL_MS = 120_000

/// The render box has stopped: jobs are waiting and nothing has finished for
/// hours. Nothing errors in that state - the queue just grows - so the
/// tracker's watchdog says so here, to admins only (it names machines).
export default function RenderStallBanner() {
  const [alert, setAlert] = useState<RenderAlert | null>(null)

  useEffect(() => {
    if (!auth.isAdmin) return
    const poll = () => api.renderAlert().then(setAlert).catch(() => setAlert(null))
    poll()
    const id = setInterval(poll, POLL_MS)
    return () => clearInterval(id)
  }, [])

  if (!alert) return null

  return (
    <div className="card" role="alert" style={{
      marginBottom: 16, display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap',
      borderLeft: '3px solid var(--warn)',
    }}>
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8, fontWeight: 700, color: 'var(--warn)' }}>
        <span style={{ width: 9, height: 9, borderRadius: '50%', background: 'var(--warn)', boxShadow: '0 0 6px var(--warn)' }} />
        Render box stalled
      </span>
      <span>{alert.message}</span>
      <Link to="/machines" style={{ marginLeft: 'auto', fontWeight: 650 }}>Machines →</Link>
    </div>
  )
}

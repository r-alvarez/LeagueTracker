import { useEffect, useState } from 'react'
import { account } from '../account'
import { api } from '../api'
import type { ClaimInfo } from '../types'

const ICON_CDN = 'https://ddragon.leagueoflegends.com/cdn/15.1.1/img/profileicon'

/// "Is this your Riot account?" - the server names a starter icon, the
/// player sets it in the League client, Riot confirms. Signed-in visitors
/// of an unowned profile see this; owners never do.
export default function ClaimAccount() {
  const [claim, setClaim] = useState<ClaimInfo | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)

  useEffect(() => {
    api.myClaims().then(all => setClaim(all.find(c => c.accountId === account.current.id) ?? null)).catch(() => setClaim(null))
  }, [])

  const start = async () => {
    setBusy(true); setMessage(null)
    try { setClaim(await api.startClaim(account.current.id)) } catch (e) { setMessage(String(e)) } finally { setBusy(false) }
  }
  const verify = async () => {
    if (!claim) return
    setBusy(true); setMessage(null)
    try {
      const r = await api.verifyClaim(claim.id)
      setClaim(r.claim)
      if (r.verified) { setDone(true); setMessage('Verified - this account is yours. Reloading…'); window.setTimeout(() => window.location.reload(), 1200) }
      else setMessage(r.error ?? 'Not yet')
    } catch (e) { setMessage(String(e)) } finally { setBusy(false) }
  }

  const expires = claim ? new Date(claim.expiresUtc) : null
  const live = claim && claim.state === 'pending' && expires && expires.getTime() > Date.now()

  return (
    <div className="card">
      <h2>Is this your Riot account?</h2>
      {!live ? (
        <>
          <p className="mut" style={{ marginTop: 0 }}>
            Claim it and the page becomes yours to manage: machines, syncs, exports, what visitors see. The proof is a
            profile icon: we name one, you set it in the League client, Riot confirms it. Nothing to buy or unlock.
          </p>
          <button className="action primary" disabled={busy} onClick={start}>Claim this account</button>
        </>
      ) : (
        <div className="claim-box">
          <img className="claim-icon" src={`${ICON_CDN}/${claim.iconId}.png`} alt={`Profile icon ${claim.iconId}`} width={72} height={72} />
          <div>
            <p style={{ margin: '0 0 6px' }}>
              In the League client, open your profile → change icon → pick <strong>this one</strong> (starter icon #{claim.iconId}), then press Verify.
            </p>
            <p className="mut sm-text" style={{ margin: '0 0 8px' }}>
              Valid until {expires!.toLocaleTimeString()} · {claim.attemptsLeft} check{claim.attemptsLeft === 1 ? '' : 's'} left · Riot can take a minute to show the change.
            </p>
            <button className="action primary" disabled={busy || done} onClick={verify}>Verify</button>
          </div>
        </div>
      )}
      {message && <p className={done ? 'sm-text' : 'warn-text sm-text'} style={{ margin: '8px 0 0' }}>{message}</p>}
    </div>
  )
}

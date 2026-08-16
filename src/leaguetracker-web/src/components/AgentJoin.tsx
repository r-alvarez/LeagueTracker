import { useEffect, useState } from 'react'
import { account } from '../account'

interface Release { version: string; file: string; sizeBytes: number; installer: string | null; installerSizeBytes: number }

/// Everything a new machine needs, from the site: the current agent build
/// (the same zip the trackers serve to self-updates) and a join code that
/// carries tracker URL + Access token + role, so the friend's setup window
/// is one paste. The code is built HERE, in the browser - the token never
/// goes to the server; it only travels owner → friend.
export default function AgentJoin() {
  const [release, setRelease] = useState<Release | null>(null)
  const [open, setOpen] = useState(false)
  const [cfId, setCfId] = useState('')
  const [cfSecret, setCfSecret] = useState('')
  const [role, setRole] = useState<'recorder' | 'renderer' | 'both'>('recorder')
  const [prefix, setPrefix] = useState(account.current.label)
  const [code, setCode] = useState('')
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    fetch('/api/agent/release').then(r => (r.status === 200 ? r.json() : null)).then(setRelease).catch(() => setRelease(null))
  }, [])

  const build = () => {
    const payload = {
      server: window.location.origin,
      cfId: cfId.trim(),
      cfSecret: cfSecret.trim(),
      role,
      prefix: prefix.trim(),
    }
    const json = JSON.stringify(payload)
    const b64 = btoa(unescape(encodeURIComponent(json))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
    setCode(`lt1:${b64}`)
    setCopied(false)
  }

  const copy = async () => {
    try { await navigator.clipboard.writeText(code); setCopied(true) } catch { /* the textarea is selectable */ }
  }

  return (
    <div className="card">
      <h2>Get the agent</h2>
      <p className="mut" style={{ marginTop: 0 }}>
        The agent runs on the gaming PC: it records the player's games and publishes them to YouTube (recorder), or cuts
        replay clips for every account (renderer). Install: run the installer (no admin needed), then in the setup window
        type this site's address (or paste a join code) and Save - the machine shows up below as <em>pending</em> until you
        approve it. It updates itself from here afterwards.
      </p>
      <p className="mut sm-text" style={{ margin: '0 0 10px' }}>
        The download is safe but not code-signed yet, so Windows SmartScreen says "not commonly downloaded". To get past it
        once: in the browser's download bar choose <strong>Keep</strong> (Edge: the ··· menu → Keep → Keep anyway), then if a
        blue "Windows protected your PC" box appears click <strong>More info → Run anyway</strong>. (Or right-click the
        downloaded file → Properties → tick <strong>Unblock</strong> → OK before running.)
      </p>
      <p style={{ margin: '0 0 10px' }}>
        {release ? (
          <>
            {release.installer && (
              <a className="action primary" href={`/api/agent/release/${encodeURIComponent(release.installer)}`} download>
                Download installer {release.version} ({Math.round(release.installerSizeBytes / 1_000_000)} MB)
              </a>
            )}{' '}
            <a className={`action${release.installer ? '' : ' primary'}`} href={`/api/agent/release/${encodeURIComponent(release.file)}`} download title="Portable: unzip anywhere and double-click the exe">
              {release.installer ? 'zip' : `Download agent ${release.version}`} ({Math.round(release.sizeBytes / 1_000_000)} MB)
            </a>
          </>
        ) : (
          <span className="mut">No agent build published on this tracker yet.</span>
        )}
        {' '}
        <button className="action" onClick={() => setOpen(o => !o)}>{open ? 'Hide join code' : 'Make a join code…'}</button>
      </p>
      {open && (
        <div className="agent-join">
          <p className="mut sm-text" style={{ marginTop: 0 }}>
            Optional. A join code pre-fills the setup window (site address, role, title prefix). Leave the token fields
            empty for the normal approve-on-this-page flow; fill them only for a machine that must bypass approval with a
            Cloudflare Access service token. The code is made in your browser and never sent anywhere.
          </p>
          <div className="agent-join-grid">
            <label>Token ID<input value={cfId} onChange={e => setCfId(e.target.value)} placeholder="….access" /></label>
            <label>Token secret<input value={cfSecret} onChange={e => setCfSecret(e.target.value)} type="password" /></label>
            <label>Role
              <select value={role} onChange={e => setRole(e.target.value as typeof role)}>
                <option value="recorder">Recorder – a player's PC</option>
                <option value="renderer">Renderer – the replay box</option>
                <option value="both">Both</option>
              </select>
            </label>
            <label>Video title prefix<input value={prefix} onChange={e => setPrefix(e.target.value)} placeholder="e.g. Ben" /></label>
          </div>
          <p style={{ margin: '8px 0' }}>
            <button className="action primary" onClick={build}>Make join code</button>
            {code && <button className="action" onClick={copy} style={{ marginLeft: 8 }}>{copied ? 'Copied' : 'Copy'}</button>}
          </p>
          {code && <textarea className="agent-join-code" readOnly value={code} rows={3} onFocus={e => e.currentTarget.select()} />}
        </div>
      )}
    </div>
  )
}

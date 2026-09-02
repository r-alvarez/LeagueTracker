import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { account } from '../account'
import { api } from '../api'
import { auth } from '../auth'
import { useChampionIcons, useItemCatalog } from '../champions'
import ChampPicker from '../components/ChampPicker'
import { PHASES, PHASE_HINT, PHASE_LABEL, RULE_KINDS, STATUS_LABEL, clock, parseClock, ruleMeta } from '../gameplans'
import type { RuleParamMeta } from '../gameplans'
import type { ChampionFacet, GameplanAdherence, GameplanImportResult, GameplanPhase, GameplanSummary, PointStatus, ReferencePoint } from '../types'

interface Draft { key: number; id?: string; phase: GameplanPhase; text: string; rule: { kind: string; params: Record<string, number> } }

const ADHERENCE_GAMES = 100
const DOTS = 30
const MIN_SPLIT = 5

let nextKey = 1
const draftOf = (p: ReferencePoint): Draft => ({ key: nextKey++, id: p.id, phase: p.phase, text: p.text, rule: { kind: p.rule.kind, params: { ...p.rule.params } } })

/// Commits on blur / enter, so a half-typed clock never reaches the plan.
function ClockInput({ value, onChange }: { value: number; onChange: (sec: number) => void }) {
  const [text, setText] = useState(clock(value))
  useEffect(() => { setText(clock(value)) }, [value])
  const bad = parseClock(text) === null
  const commit = () => { const sec = parseClock(text); if (sec !== null) onChange(sec); else setText(clock(value)) }
  return (
    <input className={`text gp-clock ${bad ? 'bad' : ''}`} value={text} onChange={e => setText(e.target.value)}
      onBlur={commit} onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); commit() } }} />
  )
}

function ItemPick({ value, onChange }: { value: number; onChange: (id: number) => void }) {
  const items = useItemCatalog()
  const [search, setSearch] = useState('')
  const [open, setOpen] = useState(false)
  const current = items.find(i => i.id === value)
  const shown = search.length > 0 ? items.filter(i => i.name.toLowerCase().includes(search.toLowerCase())).slice(0, 12) : []
  return (
    <span className="gp-item-pick">
      <input className="text" placeholder="Search item…" value={open ? search : (current?.name ?? '')}
        onFocus={() => setOpen(true)} onBlur={() => setTimeout(() => { setOpen(false); setSearch('') }, 120)}
        onChange={e => setSearch(e.target.value)} />
      {open && shown.length > 0 && (
        <div className="cp-drop gp-item-drop">
          <div className="cp-list">
            {shown.map(i => (
              <button key={i.id} type="button" onMouseDown={() => { onChange(i.id); setSearch(''); setOpen(false) }}>
                <span className="cp-name">{i.name}</span><span className="mut sm-text">{i.gold}g</span>
              </button>
            ))}
          </div>
        </div>
      )}
    </span>
  )
}

function ParamField({ meta, value, onChange }: { meta: RuleParamMeta; value: number; onChange: (v: number) => void }) {
  const num = (min: number, max: number, step = 1) => (
    <input className="text gp-num-in" type="number" min={min} max={max} step={step} value={value}
      onChange={e => onChange(Math.max(min, Math.min(max, parseInt(e.target.value || '0', 10))))} />
  )
  return (
    <label className="gp-param">
      <span>{meta.label}</span>
      {meta.unit === 'clock' && <ClockInput value={value} onChange={onChange} />}
      {meta.unit === 'units' && <span className="gp-unit">{num(500, 8000, 100)} units</span>}
      {meta.unit === 'pct' && <span className="gp-unit">{num(0, 100, 5)} %</span>}
      {meta.unit === 'level' && num(2, 18)}
      {meta.unit === 'count' && num(1, 10)}
      {meta.unit === 'toggle' && <input type="checkbox" checked={value !== 0} onChange={e => onChange(e.target.checked ? 1 : 0)} />}
      {meta.unit === 'item' && <ItemPick value={value} onChange={onChange} />}
    </label>
  )
}

function Editor({ champion, canManage, onSaved }: { champion: string; canManage: boolean; onSaved: () => void }) {
  const [drafts, setDrafts] = useState<Draft[] | null>(null)
  const [saved, setSaved] = useState<string>('')
  const [defaults, setDefaults] = useState<Record<string, Record<string, number>>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [adherence, setAdherence] = useState<GameplanAdherence | null>(null)

  useEffect(() => {
    setDrafts(null)
    setError(null)
    api.gameplan(champion).then(plan => {
      const list = (plan?.points ?? []).map(draftOf)
      setDrafts(list)
      setSaved(JSON.stringify(list.map(strip)))
    }).catch(e => setError(String(e)))
    api.gameplanAdherence(champion, ADHERENCE_GAMES).then(setAdherence).catch(() => setAdherence(null))
  }, [champion])
  useEffect(() => { api.gameplanRuleDefaults().then(setDefaults).catch(() => setDefaults({})) }, [])

  const strip = (d: Draft) => ({ id: d.id, phase: d.phase, text: d.text, rule: d.rule })
  const dirty = drafts !== null && JSON.stringify(drafts.map(strip)) !== saved
  const invalid = drafts?.some(d => d.text.trim().length === 0) ?? false

  const update = (key: number, patch: Partial<Draft>) => setDrafts(ds => ds!.map(d => d.key === key ? { ...d, ...patch } : d))
  const setRuleKind = (d: Draft, kind: string) =>
    update(d.key, { rule: { kind, params: { ...(defaults[kind] ?? {}) } } })
  const setParam = (d: Draft, key: string, value: number) =>
    update(d.key, { rule: { ...d.rule, params: { ...d.rule.params, [key]: value } } })
  const newPoint = (phase: GameplanPhase): Draft => {
    const kind = RULE_KINDS[0].kind
    return { key: nextKey++, phase, text: '', rule: { kind, params: { ...(defaults[kind] ?? {}) } } }
  }
  const move = (index: number, dir: -1 | 1) => setDrafts(ds => {
    const next = [...ds!]
    const j = index + dir
    if (j < 0 || j >= next.length) return ds
    ;[next[index], next[j]] = [next[j], next[index]]
    return next
  })

  const save = () => {
    if (!drafts) return
    setBusy(true)
    setError(null)
    api.saveGameplan(champion, drafts.map(strip))
      .then(plan => {
        const list = plan.points.map(draftOf)
        setDrafts(list)
        setSaved(JSON.stringify(list.map(strip)))
        onSaved()
        return api.gameplanAdherence(champion, ADHERENCE_GAMES).then(setAdherence)
      })
      .catch(e => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false))
  }

  const remove = () => {
    if (!window.confirm(`Delete the ${champion} gameplan?`)) return
    setBusy(true)
    api.deleteGameplan(champion).then(() => { setDrafts([]); setSaved(JSON.stringify([])); setAdherence(null); onSaved() })
      .catch(e => setError(String(e))).finally(() => setBusy(false))
  }

  if (drafts === null) return <div className="empty">Loading…</div>

  return (
    <>
      <div className="card" style={{ marginBottom: 16 }}>
        <div className="card-head">
          <h2>{champion} <span className="mut">reference points</span></h2>
          {canManage && (
            <span className="card-head-actions">
              {dirty && <span className="mut sm-text">unsaved changes</span>}
              <button className="action primary" disabled={busy || !dirty || invalid} onClick={save}>Save plan</button>
              {saved !== '[]' && <button className="action" disabled={busy} onClick={remove}>delete plan</button>}
            </span>
          )}
        </div>
        {drafts.length === 0 && (
          <p className="card-intro mut">
            The sheet a coach would hand you for {champion}: one sentence per reference point, grouped by phase, each
            with the rule the tracker scores it by. What the timeline cannot see does not go on the sheet.
          </p>
        )}
        {PHASES.map(ph => {
          const rows = drafts.map((d, i) => ({ d, i })).filter(x => x.d.phase === ph)
          if (rows.length === 0 && !canManage) return null
          return (
            <div key={ph} className="gp-phase">
              <div className="gp-phase-head">
                <span className="gp-phase-name">{PHASE_LABEL[ph]}</span>
                <span className="mut sm-text">{PHASE_HINT[ph]}</span>
                {canManage && (
                  <button className="action sm-action" onClick={() => setDrafts([...drafts, newPoint(ph)])}>
                    + add point
                  </button>
                )}
              </div>
              {rows.map(({ d, i }) => {
                const meta = ruleMeta(d.rule.kind)
                return (
                  <div key={d.key} className="gp-edit-row">
                    <div className="gp-edit-main">
                      <span className="gp-num">{String(i + 1).padStart(2, '0')}</span>
                      {canManage ? (
                        <input className="text gp-text-in" value={d.text} maxLength={200} placeholder="e.g. At 6, look for 2v2s with our jungler"
                          onChange={e => update(d.key, { text: e.target.value })} />
                      ) : <span className="gp-text">{d.text}</span>}
                      {canManage && (
                        <>
                          <select className="select" value={d.rule.kind} onChange={e => setRuleKind(d, e.target.value)} title="Rule">
                            {RULE_KINDS.map(r => <option key={r.kind} value={r.kind}>{r.label}</option>)}
                          </select>
                          <span className="gp-row-tools">
                            <button className="action sm-action" title="Move up" onClick={() => move(i, -1)}>↑</button>
                            <button className="action sm-action" title="Move down" onClick={() => move(i, 1)}>↓</button>
                            <button className="action sm-action" title="Remove" onClick={() => setDrafts(drafts.filter(x => x.key !== d.key))}>remove</button>
                          </span>
                        </>
                      )}
                    </div>
                    {meta && (
                      <div className="gp-edit-rule">
                        {canManage && meta.params.map(pm => (
                          <ParamField key={pm.key} meta={pm} value={d.rule.params[pm.key] ?? 0} onChange={v => setParam(d, pm.key, v)} />
                        ))}
                        <p className="mut sm-text" style={{ margin: '4px 0 0', flexBasis: '100%' }}>{meta.desc}</p>
                      </div>
                    )}
                  </div>
                )
              })}
            </div>
          )
        })}
        {error && <p className="loss sm-text" style={{ margin: '10px 2px 0' }}>{error}</p>}
      </div>

      {adherence && adherence.points.length > 0 && <Adherence data={adherence} />}
    </>
  )
}

/// The last N games on the champion, point by point - the habit, not the game.
function Adherence({ data }: { data: GameplanAdherence }) {
  const games = data.games
  const wins = games.filter(g => g.win).length
  return (
    <div className="card">
      <h2>
        Last {games.length} games <span className="mut">how often each point held, and whether holding it travelled with winning</span>
      </h2>
      <div className="table-scroll">
        <table className="data gp-adherence">
          <thead>
            <tr>
              <th>Reference point</th>
              <th>Held</th>
              <th title={`Win rate in the games where the point was met vs missed; overall ${games.length > 0 ? Math.round(100 * wins / games.length) : 0}% over these games`}>Wins when met · missed</th>
              <th title="Newest on the left">{games.length > 0 ? `Last ${Math.min(DOTS, games.length)}` : ''}</th>
            </tr>
          </thead>
          <tbody>
            {data.points.map(p => {
              const judged = p.met + p.missed
              const pct = judged >= 3 ? Math.round(100 * p.met / judged) : null
              const whenMet = p.met >= MIN_SPLIT ? Math.round(100 * p.winsWhenMet / p.met) : null
              const whenMissed = p.missed >= MIN_SPLIT ? Math.round(100 * p.winsWhenMissed / p.missed) : null
              return (
                <tr key={p.id}>
                  <td>
                    <span className="gp-adh-text">{p.text}</span>
                    <span className="mut sm-text"> · {PHASE_LABEL[p.phase].toLowerCase()}</span>
                  </td>
                  <td className="wr-cell">
                    {pct !== null ? <strong className={pct >= 67 ? 'win' : pct <= 33 ? 'loss' : ''}>{pct}%</strong> : <span className="mut">—</span>}
                    <span className="wr-rec">
                      {p.met} met · {p.missed} missed{p.na > 0 ? ` · ${p.na} n/a` : ''}{p.pending > 0 ? ` · ${p.pending} pending` : ''}
                    </span>
                  </td>
                  <td className="wr-cell">
                    {whenMet === null && whenMissed === null
                      ? <span className="mut">—</span>
                      : (
                        <>
                          <strong className={whenMet !== null && whenMissed !== null && whenMet > whenMissed ? 'win' : ''}>{whenMet !== null ? `${whenMet}%` : '—'}</strong>
                          <span className="mut"> · </span>
                          <strong className={whenMet !== null && whenMissed !== null && whenMissed < whenMet ? 'loss' : ''}>{whenMissed !== null ? `${whenMissed}%` : '—'}</strong>
                          <span className="wr-rec">{p.winsWhenMet}/{p.met} · {p.winsWhenMissed}/{p.missed}</span>
                        </>
                      )}
                  </td>
                  <td>
                    <span className="gp-dots">
                      {p.recent.slice(0, DOTS).map(r => {
                        const g = games.find(x => x.id === r.matchId)
                        return (
                          <Link key={r.matchId} to={`/matches/${r.matchId}`} className={`gp-dot lg ${r.status}`}
                            title={`${g ? new Date(g.gameEndUtc).toLocaleDateString() : ''} · ${g?.win ? 'win' : 'loss'} · ${STATUS_LABEL[r.status as PointStatus]}`} />
                        )
                      })}
                    </span>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      <p className="mut sm-text" style={{ margin: '10px 2px 0' }}>
        The win split is conditioned on the result, so it says whether a point travels with winning on your games, not
        that holding it causes the win. Rates hide until five games sit on each side.
      </p>
    </div>
  )
}

/// Paste the JSON an Export (or the export bundle's gameplans.json) produced;
/// each champion's plan lands or fails on its own.
function ImportBox({ onDone }: { onDone: () => void }) {
  const [text, setText] = useState('')
  const [busy, setBusy] = useState(false)
  const [results, setResults] = useState<GameplanImportResult[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const run = () => {
    let bundle: unknown
    try { bundle = JSON.parse(text) } catch { setError('That is not valid JSON.'); return }
    setBusy(true)
    setError(null)
    api.importGameplans(bundle)
      .then(r => { setResults(r); if (r.length > 0 && r.every(x => !x.error)) onDone() })
      .catch(e => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false))
  }

  return (
    <div className="gp-import">
      <textarea className="text" rows={6} spellCheck={false} placeholder='{"plans":[{"champion":"Ahri","points":[…]}]}'
        value={text} onChange={e => setText(e.target.value)} />
      <div className="gp-import-actions">
        <button className="action primary sm-action" disabled={busy || text.trim().length === 0} onClick={run}>Import plans</button>
        <span className="mut sm-text">Existing plans for the same champions are replaced.</span>
      </div>
      {error && <p className="loss sm-text" style={{ margin: '6px 0 0' }}>{error}</p>}
      {results && (
        <ul className="rv-evidence" style={{ marginTop: 6 }}>
          {results.length === 0 && <li className="mut">Nothing to import - no plans in that JSON.</li>}
          {results.map((r, i) => (
            <li key={i} className={r.error ? 'loss' : 'win'}>
              {r.champion || '(no champion)'}: {r.error ?? `${r.points} point${r.points === 1 ? '' : 's'} imported`}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

export default function Gameplans() {
  const [params, setParams] = useSearchParams()
  const champion = params.get('champion') ?? ''
  const [plans, setPlans] = useState<GameplanSummary[]>([])
  const [facets, setFacets] = useState<ChampionFacet[]>([])
  const [importing, setImporting] = useState(false)
  const icons = useChampionIcons()
  const canManage = auth.owns(account.current.id)

  const refresh = () => { api.gameplans().then(setPlans).catch(() => setPlans([])) }
  useEffect(() => {
    refresh()
    api.matchFacets().then(f => setFacets(f.champions)).catch(() => setFacets([]))
  }, [])

  const select = (name: string) => setParams(name ? { champion: name } : {})
  const options = useMemo(() => facets.length > 0 ? facets : plans.map(p => ({ name: p.champion, count: p.points })), [facets, plans])

  return (
    <>
      <div className="card" style={{ marginBottom: 16 }}>
        <div className="card-head">
          <h2>Gameplans <span className="mut">the reference points you hold yourself to, per champion</span></h2>
          {canManage && (
            <span className="card-head-actions">
              <span className="mut sm-text">{plans.length === 0 ? 'Start one:' : 'New plan:'}</span>
              <ChampPicker placeholder="Champion" value="" options={options.filter(o => !plans.some(p => p.champion === o.name))} onChange={select} />
              {plans.length > 0 && <a className="action sm-action" href={account.apiUrl('/api/gameplans/export')} download>Export</a>}
              <button className={`action sm-action ${importing ? 'on' : ''}`} onClick={() => setImporting(v => !v)}>Import…</button>
            </span>
          )}
        </div>
        {importing && canManage && <ImportBox onDone={() => { refresh(); setImporting(false) }} />}
        {plans.length === 0 && !canManage && <p className="mut" style={{ margin: 0 }}>No gameplans written yet.</p>}
        {plans.length > 0 && (
          <div className="gp-plan-list">
            {plans.map(p => {
              const icon = icons(p.champion)
              return (
                <button key={p.champion} className={`gp-plan-chip ${p.champion === champion ? 'on' : ''}`} onClick={() => select(p.champion)}>
                  {icon ? <img src={icon} alt="" /> : <span className="cp-q">{p.champion.slice(0, 2)}</span>}
                  <span>{p.champion}</span>
                  <span className="mut sm-text">{p.points} point{p.points === 1 ? '' : 's'}</span>
                </button>
              )
            })}
          </div>
        )}
      </div>

      {champion
        ? <Editor champion={champion} canManage={canManage} onSaved={refresh} />
        : plans.length === 0 && canManage && (
          <div className="empty">
            Pick a champion above to write its first reference points — the sheet you would want a coach to hand you,
            and the checklist every game of theirs gets scored against.
          </div>
        )}
    </>
  )
}

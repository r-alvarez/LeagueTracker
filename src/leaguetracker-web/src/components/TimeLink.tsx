import type { ReactNode } from 'react'

export const clock = (sec: number) => `${Math.floor(sec / 60)}:${String(Math.floor(sec % 60)).padStart(2, '0')}`

export type Jump = (timeSec: number) => void

// Every game clock on the match page is a way into the stage.
export function TimeLink({ t, onJump }: { t: number; onJump: Jump }) {
  return (
    <button type="button" className="tlink" title="Open this moment on the stage" onClick={() => onJump(t)}>{clock(t)}</button>
  )
}

// Server prose ("16:51 skirmish 5v1 won; 19:13 ...") with each clock made a
// link. "1:24 after the target" and "within 5:00" are durations, not clocks.
export function linkClocks(text: string, onJump: Jump): ReactNode[] {
  const out: ReactNode[] = []
  const re = /\b(\d{1,2}):(\d{2})\b/g
  let last = 0
  let key = 0
  for (let m = re.exec(text); m !== null; m = re.exec(text)) {
    const before = text.slice(last, m.index)
    const after = text.slice(m.index + m[0].length)
    out.push(before)
    if (/within\s$/.test(before) || /^\s+after\b/.test(after)) out.push(m[0])
    else out.push(<TimeLink key={key++} t={Number(m[1]) * 60 + Number(m[2])} onJump={onJump} />)
    last = m.index + m[0].length
  }
  out.push(text.slice(last))
  return out
}

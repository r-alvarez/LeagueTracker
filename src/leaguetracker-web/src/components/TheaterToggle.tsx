export default function TheaterToggle({ on, onToggle, what }: { on: boolean; onToggle: () => void; what: string }) {
  return (
    <button type="button" className="action sm-action theater-toggle" aria-pressed={on} onClick={onToggle}
      title={on ? `Put ${what} back beside the video` : 'Widen the video across the card'}>
      {on ? '⤡ Normal view' : '⤢ Theater'}
    </button>
  )
}

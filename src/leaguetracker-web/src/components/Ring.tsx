// Score bands share the validated chart palette: struggling -> strong.
export const ringColor = (score: number) =>
  score < 40 ? 'var(--lp-loss)' : score < 60 ? 'var(--series-3)' : score < 80 ? 'var(--series-1)' : 'var(--chart-green)'

export default function Ring({ score, size = 56 }: { score: number | null; size?: number }) {
  const r = (size - 10) / 2
  const c = 2 * Math.PI * r
  const color = score !== null ? ringColor(score) : 'var(--panel-2)'
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} className="lens-ring" style={{ filter: score !== null ? `drop-shadow(0 0 6px color-mix(in srgb, ${color} 45%, transparent))` : undefined }}>
      <circle cx={size / 2} cy={size / 2} r={r} fill="var(--page)" stroke="var(--panel-2)" strokeWidth="5" />
      {score !== null && (
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={color} strokeWidth="5"
          strokeDasharray={`${(c * score) / 100} ${c}`} strokeLinecap="round"
          transform={`rotate(-90 ${size / 2} ${size / 2})`} />
      )}
      <text x="50%" y="50%" dy="0.35em" textAnchor="middle" fontSize={size / 3.1} fontWeight="800" fill="var(--ink)">
        {score ?? '—'}
      </text>
    </svg>
  )
}

import { useRenderHealth } from '../renders'

/** Shows what to fix before rendering can work. Hidden when everything is set up. */
export default function HealthBanner() {
  const { data } = useRenderHealth()
  if (!data || data.ok) return null
  const failing = data.checks.filter((c) => !c.ok)
  return (
    <div className="card banner">
      <strong>Clip rendering needs some setup</strong>
      <ul>
        {failing.map((c) => (
          <li key={c.id}>
            <span>{c.label}</span>
            {c.fix && <div className="small muted mono">{c.fix}</div>}
          </li>
        ))}
      </ul>
    </div>
  )
}

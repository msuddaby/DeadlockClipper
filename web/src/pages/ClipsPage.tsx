import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { api, previewUrl, videoUrl, type RenderInfo } from '../api'
import HealthBanner from '../components/HealthBanner'
import { formatClock, formatDate, highlightLabel, useHeroNames } from '../format'
import { renderStatusText, useRenders } from '../renders'

export default function ClipsPage() {
  const renders = useRenders()
  const heroName = useHeroNames()
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: api.deleteRender,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['renders'] }),
  })

  const all = renders.data ?? []
  const active = all.filter((r) => r.status === 'Queued' || r.status === 'Rendering')
  const failed = all.filter((r) => r.status === 'Failed')
  const done = all.filter((r) => r.status === 'Done' && r.hasVideo)

  return (
    <div className="stack">
      <HealthBanner />

      {active.length > 0 && (
        <section className="card">
          <h2>Rendering</h2>
          <ul className="list">
            {active.map((r) => (
              <li key={r.id} className="list-row">
                <ClipTitle r={r} heroName={heroName} />
                <span className={`badge ${r.status === 'Rendering' ? 'parsing' : 'queued'}`}>{renderStatusText(r)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="card">
        <h2>Clips</h2>
        {renders.isLoading && <p className="muted">Loading…</p>}
        {!renders.isLoading && done.length === 0 && (
          <p className="muted">
            No clips yet. Open a <Link to="/">demo</Link> and render a highlight.
          </p>
        )}
        <div className="clip-grid">
          {done.map((r) => (
            <article key={r.id} className="clip-card">
              <video src={previewUrl(r.id)} controls preload="metadata" />
              <div className="clip-meta">
                <ClipTitle r={r} heroName={heroName} />
                <div className="clip-actions">
                  <a className="button ghost" href={videoUrl(r.id)} download>
                    Download
                  </a>
                  <button className="button ghost danger" disabled={remove.isPending} onClick={() => remove.mutate(r.id)}>
                    Delete
                  </button>
                </div>
              </div>
            </article>
          ))}
        </div>
      </section>

      {failed.length > 0 && (
        <section className="card">
          <h2>Failed</h2>
          <ul className="list">
            {failed.map((r) => (
              <li key={r.id} className="list-row">
                <div>
                  <ClipTitle r={r} heroName={heroName} />
                  <div className="error small">{r.error}</div>
                </div>
                <div className="clip-actions">
                  <Link className="button ghost" to={`/demos/${r.demoId}`}>
                    Open demo
                  </Link>
                  <button className="button ghost danger" onClick={() => remove.mutate(r.id)}>
                    Remove
                  </button>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  )
}

function ClipTitle({ r, heroName }: { r: RenderInfo; heroName: (id: number) => string }) {
  return (
    <div>
      <div>
        <span className={`badge hl-${r.highlightType.toLowerCase()}`}>{highlightLabel[r.highlightType]}</span>{' '}
        <strong>{r.playerName}</strong> <span className="muted">({heroName(r.heroId)})</span>
      </div>
      <div className="muted small">
        <Link to={`/demos/${r.demoId}`}>{r.matchId ? `Match ${r.matchId}` : 'Demo'}</Link> · {formatClock(r.gameTimeSeconds)}
        {r.finishedAt && <> · rendered {formatDate(r.finishedAt)}</>}
      </div>
    </div>
  )
}

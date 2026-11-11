import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { api, type DemoSummary } from '../api'
import UploadDropzone from '../components/UploadDropzone'
import { formatBytes, formatClock, formatDate } from '../format'

const isBusy = (d: DemoSummary) => d.status === 'Downloading' || d.status === 'Queued' || d.status === 'Parsing'

export default function DemosPage() {
  const queryClient = useQueryClient()
  const demos = useQuery({
    queryKey: ['demos'],
    queryFn: api.demos,
    refetchInterval: (q) => (q.state.data?.some(isBusy) ? 1000 : false),
  })
  const local = useQuery({ queryKey: ['local-replays'], queryFn: api.localReplays })

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['demos'] })
    queryClient.invalidateQueries({ queryKey: ['local-replays'] })
  }
  const importLocal = useMutation({ mutationFn: api.importLocal, onSuccess: refresh })
  const remove = useMutation({ mutationFn: api.deleteDemo, onSuccess: refresh })
  const reparse = useMutation({ mutationFn: api.reparse, onSuccess: refresh })
  const cancelDownload = useMutation({ mutationFn: api.cancelDownload, onSuccess: refresh })

  return (
    <div className="stack">
      <section className="grid-2">
        <div className="card">
          <h2>Upload</h2>
          <UploadDropzone />
        </div>

        <div className="card">
          <h2>Local replays</h2>
          {local.isLoading && <p className="muted">Loading…</p>}
          {local.error && <p className="error">{(local.error as Error).message}</p>}
          {local.data && !local.data.exists && (
            <p className="muted">
              Replays folder not found{local.data.replaysDir ? <> at <code>{local.data.replaysDir}</code></> : null}. Set{' '}
              <code>Clipper:ReplaysDir</code> in appsettings.json.
            </p>
          )}
          {local.data?.exists && local.data.replays.length === 0 && (
            <p className="muted">No replays downloaded yet. Open a match in-game to download its replay.</p>
          )}
          {local.data?.exists && local.data.replays.length > 0 && (
            <ul className="list">
              {local.data.replays.map((r) => (
                <li key={r.fileName} className="list-row">
                  <div>
                    <div className="mono">{r.fileName}</div>
                    <div className="muted small">
                      {formatBytes(r.sizeBytes)} · {formatDate(r.modifiedAt)}
                    </div>
                  </div>
                  {r.importedDemoId ? (
                    <Link to={`/demos/${r.importedDemoId}`} className="button ghost">
                      View
                    </Link>
                  ) : (
                    <button
                      className="button"
                      disabled={importLocal.isPending}
                      onClick={() => importLocal.mutate(r.fileName)}
                    >
                      Import
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {importLocal.error && <p className="error">{(importLocal.error as Error).message}</p>}
        </div>
      </section>

      <section className="card">
        <h2>Demos</h2>
        {demos.isLoading && <p className="muted">Loading…</p>}
        {demos.error && <p className="error">{(demos.error as Error).message}</p>}
        {demos.data?.length === 0 && <p className="muted">Nothing here yet. Upload or import a demo.</p>}
        {demos.data && demos.data.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Demo</th>
                <th>Status</th>
                <th>Duration</th>
                <th>Highlights</th>
                <th>Added</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {demos.data.map((d) => (
                <tr key={d.id}>
                  <td>
                    <Link to={`/demos/${d.id}`} className="mono">
                      {d.matchId ? `Match ${d.matchId}` : d.fileName}
                    </Link>
                    <div className="muted small">
                      {d.source === 'LocalReplay' ? 'Local replay' : d.source === 'MatchId' ? 'Downloaded' : d.source} · {formatBytes(d.sizeBytes)}
                    </div>
                  </td>
                  <td>
                    <StatusCell demo={d} />
                  </td>
                  <td>{d.durationSeconds != null ? formatClock(d.durationSeconds) : '–'}</td>
                  <td>{d.highlightCount ?? '–'}</td>
                  <td className="small">{formatDate(d.createdAt)}</td>
                  <td className="actions">
                    {d.status === 'Downloading' && (
                      <button className="button ghost danger" onClick={() => cancelDownload.mutate(d.id)}>
                        Cancel
                      </button>
                    )}
                    {(d.status === 'Failed' || d.status === 'Done') && d.sizeBytes > 0 && (
                      <button className="button ghost" onClick={() => reparse.mutate(d.id)}>
                        Re-parse
                      </button>
                    )}
                    <button className="button ghost danger" onClick={() => remove.mutate(d.id)}>
                      Remove
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}

function StatusCell({ demo }: { demo: DemoSummary }) {
  if (isBusy(demo)) {
    return (
      <div className="status-progress">
        <span className={`badge ${demo.status.toLowerCase()}`}>{demo.status}</span>
        <progress value={demo.progress ?? 0} max={1} />
        <span className="small muted">{Math.round((demo.progress ?? 0) * 100)}%</span>
        {demo.error && <span className="small muted">{demo.error}</span>}
      </div>
    )
  }
  return (
    <div>
      <span className={`badge ${demo.status.toLowerCase()}`}>{demo.status}</span>
      {demo.error && (
        <div className="error small" title={demo.error}>
          {demo.error}
        </div>
      )}
    </div>
  )
}

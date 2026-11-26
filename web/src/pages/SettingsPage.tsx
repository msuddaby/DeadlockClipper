import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { Link, useLocation } from 'react-router'
import { api } from '../api'
import AutoModeCard from '../components/AutoModeCard'
import PlayersCard from '../components/PlayersCard'
import SteamAccounts from '../components/SteamAccounts'
import UploadDropzone from '../components/UploadDropzone'
import { formatBytes, formatDate } from '../format'
import { useRenderHealth } from '../renders'

const sections = [
  ['players', 'Players'],
  ['auto', 'Auto mode'],
  ['accounts', 'Clipper accounts'],
  ['rendering', 'Rendering'],
  ['import', 'Import demos'],
] as const

export default function SettingsPage() {
  const status = useQuery({ queryKey: ['steam-status'], queryFn: api.steamStatus, refetchInterval: 5_000 })
  const { hash } = useLocation()
  useEffect(() => {
    if (hash) document.getElementById(hash.slice(1))?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [hash])

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>Settings</h1>
          <p className="muted">Whose matches to clip, how they're found, and how clips are made.</p>
        </div>
      </div>
      <div className="settings-layout">
        <nav className="settings-nav">
          {sections.map(([id, label]) => (
            <Link key={id} to={`#${id}`}>
              {label}
            </Link>
          ))}
        </nav>
        <div className="stack">
          <div id="players">
            <PlayersCard clipperAccounts={(status.data?.accounts ?? []).map((a) => a.account)} />
          </div>
          <div id="auto">
            <AutoModeCard />
          </div>
          <div id="accounts">
            <SteamAccounts status={status.data} />
          </div>
          <section id="rendering" className="card">
            <div className="section-head">
              <h2>Rendering</h2>
            </div>
            <RenderChecks />
          </section>
          <section id="import" className="card">
            <div className="section-head">
              <h2>Import demos</h2>
            </div>
            <div className="grid-2">
              <div>
                <h3 style={{ marginBottom: 10 }}>Upload</h3>
                <UploadDropzone />
              </div>
              <LocalReplays />
            </div>
          </section>
        </div>
      </div>
    </div>
  )
}

function RenderChecks() {
  const { data } = useRenderHealth()
  if (!data) return <p className="muted small">Checking…</p>
  return (
    <ul className="check-list">
      {data.checks.map((c) => (
        <li key={c.id}>
          <span className={`dot ${c.ok ? 'ok' : 'bad'}`}>{c.ok ? '✓' : '!'}</span>
          <div>
            <div>{c.label}</div>
            {!c.ok && c.fix && <div className="muted small mono">{c.fix}</div>}
          </div>
        </li>
      ))}
    </ul>
  )
}

function LocalReplays() {
  const queryClient = useQueryClient()
  const local = useQuery({ queryKey: ['local-replays'], queryFn: api.localReplays })
  const importLocal = useMutation({
    mutationFn: api.importLocal,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['local-replays'] })
      queryClient.invalidateQueries({ queryKey: ['demos'] })
    },
  })
  return (
    <div>
      <h3 style={{ marginBottom: 10 }}>Replays the game downloaded</h3>
      {local.data && !local.data.exists && (
        <p className="muted small">
          Replays folder not found{local.data.replaysDir ? <> at <code>{local.data.replaysDir}</code></> : null}. Set{' '}
          <code>Clipper:ReplaysDir</code> in appsettings.json.
        </p>
      )}
      {local.data?.exists && local.data.replays.length === 0 && (
        <p className="muted small">None yet. Open a match's replay in-game to download it.</p>
      )}
      <ul className="list">
        {local.data?.replays.map((r) => (
          <li key={r.fileName} className="list-row">
            <div>
              <div className="mono">{r.fileName}</div>
              <div className="faint small">
                {formatBytes(r.sizeBytes)} · {formatDate(r.modifiedAt)}
              </div>
            </div>
            {r.importedDemoId ? (
              <Link to={`/demos/${r.importedDemoId}`} className="button ghost sm">
                Open
              </Link>
            ) : (
              <button className="button sm" disabled={importLocal.isPending} onClick={() => importLocal.mutate(r.fileName)}>
                Import
              </button>
            )}
          </li>
        ))}
      </ul>
      {importLocal.error && <p className="error small">{(importLocal.error as Error).message}</p>}
    </div>
  )
}

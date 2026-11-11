import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api, type LoginStatus, type SteamAccount, type SteamStatus } from '../api'

const time = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })

/** The clipper's Steam accounts: list, remove, and add (password + Steam Guard, done in the browser). */
export default function SteamAccounts({ status }: { status: SteamStatus | undefined }) {
  const queryClient = useQueryClient()
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['steam-status'] })
  const remove = useMutation({ mutationFn: api.removeSteamAccount, onSuccess: refresh })
  const [adding, setAdding] = useState(false)
  const accounts = status?.accounts ?? []
  const perHour = status ? Math.floor(3600 / status.metadataIntervalSeconds) : 0

  return (
    <section className="card">
      <div className="section-head">
        <h2>Clipper accounts</h2>
        {!adding && (
          <button className="button ghost" onClick={() => setAdding(true)}>
            Add account
          </button>
        )}
      </div>
      <p className="muted small">
        Secondary Steam accounts that read match data from Valve for you. Each needs Deadlock in its library, and at
        least one must be your Steam friend (Valve only shares match history with friends). Each account reads about{' '}
        {perHour} matches an hour; more accounts scan faster.
      </p>
      {accounts.length === 0 && !adding && <p>No accounts yet.</p>}
      {accounts.length > 0 && (
        <ul className="list">
          {accounts.map((a) => (
            <li key={a.account} className="list-row">
              <div>
                <strong>{a.account}</strong>
                <div className="small">
                  <AccountState a={a} />
                </div>
              </div>
              <button
                className="button ghost danger"
                disabled={remove.isPending}
                onClick={() => remove.mutate(a.account)}
                title="Forgets the saved login"
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
      {adding && (
        <AddAccount
          onDone={() => {
            setAdding(false)
            refresh()
          }}
          onCancel={() => setAdding(false)}
        />
      )}
    </section>
  )
}

function AccountState({ a }: { a: SteamAccount }) {
  if (a.lastError) return <span className="error">{a.lastError}</span>
  const parts = [a.connected ? 'connected' : 'connects when needed']
  if (a.rateLimitedUntil) parts.push(`rate-limited by Valve until ${time(a.rateLimitedUntil)}`)
  else if (a.nextRequestAt) parts.push(`next request ${time(a.nextRequestAt)}`)
  if (a.requestsMade > 0) parts.push(`${a.requestsMade} matches read this session`)
  return <span className="muted">{parts.join(' · ')}</span>
}

function AddAccount({ onDone, onCancel }: { onDone: () => void; onCancel: () => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [loginId, setLoginId] = useState<string | null>(null)

  const start = useMutation({
    mutationFn: () => api.addSteamAccount(username, password),
    onSuccess: (s) => {
      setPassword('') // not kept around in the page once Steam has it
      setLoginId(s.id)
    },
  })
  const login = useQuery({
    queryKey: ['steam-login', loginId],
    queryFn: () => api.steamLogin(loginId!),
    enabled: !!loginId,
    refetchInterval: (q) => (q.state.data && ['Done', 'Failed'].includes(q.state.data.state) ? false : 1000),
  })
  const submit = useMutation({
    mutationFn: () => api.submitSteamCode(loginId!, code),
    onSuccess: () => setCode(''),
  })

  const s: LoginStatus | undefined = login.data
  if (s?.state === 'Done') {
    return (
      <div className="stack">
        <p>
          Added <strong>{s.username}</strong>. If it's the account that reads your match history, send it a Steam friend
          request from your main account; it accepts automatically.
        </p>
        <div>
          <button className="button" onClick={onDone}>
            OK
          </button>
        </div>
      </div>
    )
  }

  if (loginId) {
    return (
      <div className="stack">
        {(!s || s.state === 'Starting') && <p className="muted">Signing in to Steam…</p>}
        {s?.state === 'ConfirmOnPhone' && <p>Approve the sign-in in the Steam mobile app for {s.username}.</p>}
        {s?.state === 'Verifying' && <p className="muted">Signed in. Checking the account can reach Deadlock…</p>}
        {s?.state === 'NeedsCode' && (
          <form
            className="inline-form"
            onSubmit={(e) => {
              e.preventDefault()
              if (code.trim()) submit.mutate()
            }}
          >
            <label htmlFor="steam-code" className="small">
              {s.codeKind === 'email' ? `Steam Guard code emailed to ${s.emailHint ?? 'the account'}` : 'Steam Guard code from the mobile app'}
            </label>
            <input
              id="steam-code"
              className="text-input mono"
              autoComplete="one-time-code"
              value={code}
              onChange={(e) => setCode(e.target.value.toUpperCase())}
            />
            <button className="button" disabled={!code.trim() || submit.isPending}>
              Submit
            </button>
            {s.previousCodeWrong && <span className="error small">That code didn't work; try the current one.</span>}
          </form>
        )}
        {s?.state === 'Failed' && <p className="error">{s.error}</p>}
        <div>
          <button
            className="button ghost"
            onClick={() => {
              setLoginId(null)
              if (s?.state !== 'Failed') onCancel()
            }}
          >
            {s?.state === 'Failed' ? 'Try again' : 'Cancel'}
          </button>
        </div>
      </div>
    )
  }

  return (
    <form
      className="inline-form"
      onSubmit={(e) => {
        e.preventDefault()
        if (username.trim() && password) start.mutate()
      }}
    >
      <input
        className="text-input"
        placeholder="Steam username"
        autoComplete="username"
        value={username}
        onChange={(e) => setUsername(e.target.value)}
      />
      <input
        className="text-input"
        type="password"
        placeholder="Password"
        autoComplete="current-password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
      />
      <button className="button" disabled={!username.trim() || !password || start.isPending}>
        Sign in
      </button>
      <button type="button" className="button ghost" onClick={onCancel}>
        Cancel
      </button>
      <span className="muted small">The password goes straight to Steam and isn't saved; only a login token is kept.</span>
      {start.error && <p className="error small">{(start.error as Error).message}</p>}
    </form>
  )
}

import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api'
import { usePlayerLabel } from '../matches'
import { parseSteamId, removePlayer, setActivePlayer, usePlayers } from '../players'

/** Whose matches the clipper works on: you, your friends, anyone a clipper account is Steam friends with. */
export default function PlayersCard({ clipperAccounts }: { clipperAccounts: string[] }) {
  const { players, active } = usePlayers()
  const label = usePlayerLabel()
  const [value, setValue] = useState('')
  const [showFriends, setShowFriends] = useState(false)
  const parsed = parseSteamId(value)
  const friends = useQuery({ queryKey: ['steam-friends'], queryFn: api.steamFriends, enabled: showFriends, staleTime: 60_000 })
  const tracked = new Set(players.map((p) => p.steamId))
  const untracked = (friends.data ?? []).filter((f) => !tracked.has(f.steamId))
  const accounts = clipperAccounts.length ? clipperAccounts.join(' or ') : 'a clipper account'

  return (
    <section className="card">
      <div className="section-head">
        <h2>Players</h2>
      </div>
      <div className="stack" style={{ gap: 12 }}>
        <p className="muted small">
          Clip anyone's matches: you, a friend, a teammate. Valve only shares match history with Steam friends, so each
          player needs to be on the friends list of {accounts} (friend requests to it are accepted automatically). Switch
          between players from the sidebar.
        </p>

        {players.length > 0 && (
          <ul className="list">
            {players.map((p) => (
              <li key={p.steamId} className="list-row">
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontWeight: 650 }}>{label(p.steamId)}</div>
                  <div className="faint small mono">{p.steamId}</div>
                </div>
                <div className="inline-form">
                  {p.steamId === active ? (
                    <span className="pill">Viewing</span>
                  ) : (
                    <button className="button ghost sm" onClick={() => setActivePlayer(p.steamId)}>
                      View
                    </button>
                  )}
                  <button className="button ghost sm" title="Remove from this list (scanned matches and clips stay)" onClick={() => removePlayer(p.steamId)}>
                    Remove
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}

        <form
          className="inline-form"
          onSubmit={(e) => {
            e.preventDefault()
            if (!parsed) return
            setActivePlayer(parsed)
            setValue('')
          }}
        >
          <input
            className="text-input mono"
            style={{ width: 300 }}
            placeholder="SteamID64, friend code or profile link"
            value={value}
            onChange={(e) => setValue(e.target.value)}
          />
          <button className="button" disabled={!parsed || tracked.has(parsed)}>
            Add player
          </button>
          {value.trim() && !parsed && (
            <span className="error small">
              {/\/id\//.test(value) ? 'Custom profile URLs need the numeric ID: pick them from the friends list instead.' : "That isn't a Steam ID."}
            </span>
          )}
          {parsed && tracked.has(parsed) && <span className="muted small">Already added.</span>}
          {parsed && !tracked.has(parsed) && parsed !== value.trim() && <span className="faint small mono">{parsed}</span>}
        </form>

        <div>
          {!showFriends ? (
            <button className="button ghost sm" onClick={() => setShowFriends(true)}>
              Pick from the clipper accounts' friends
            </button>
          ) : friends.isLoading ? (
            <p className="muted small">Asking Steam for the friends list…</p>
          ) : friends.error ? (
            <p className="error small">{(friends.error as Error).message}</p>
          ) : (
            <>
              <div className="inline-form" style={{ marginBottom: 8 }}>
                <h3>Friends of the clipper accounts</h3>
                <button className="button ghost sm" disabled={friends.isFetching} onClick={() => friends.refetch()}>
                  {friends.isFetching ? 'Refreshing…' : 'Refresh'}
                </button>
              </div>
              {untracked.length === 0 ? (
                <p className="muted small">
                  {friends.data?.length
                    ? 'Everyone on the friends list is already added.'
                    : `No friends yet. Send ${accounts} a friend request from the player's Steam account.`}
                </p>
              ) : (
                <ul className="list">
                  {untracked.map((f) => (
                    <li key={f.steamId} className="list-row">
                      <div style={{ minWidth: 0 }}>
                        <div style={{ fontWeight: 650 }}>{f.name ?? 'Unknown name'}</div>
                        <div className="faint small mono">
                          {f.steamId}
                          {clipperAccounts.length > 1 && ` · via ${f.accounts.join(', ')}`}
                        </div>
                      </div>
                      <button className="button sm" onClick={() => setActivePlayer(f.steamId, f.name)}>
                        Add
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
        </div>
      </div>
    </section>
  )
}

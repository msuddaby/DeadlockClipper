# Deadlock Clipper

Allstar-style highlight finder for Deadlock. Upload or import a `.dem`, and it parses the match and lists players, the kill feed and multikill highlights, each with tick ranges ready for clipping.

- `src/DeadlockClipper.Core`: demo parsing (DemoFile.Net) and highlight detectors. No web dependencies.
- `src/DeadlockClipper.Api`: ASP.NET Core minimal API, SQLite, background parse queue.
- `web/`: React + Vite + TanStack Query.
- `tests/DeadlockClipper.Core.Tests`: detector unit tests and an opt-in real-demo integration test.
- `lib/demofile-net`: vendored and patched DemoFile.Net. Upstream's schema predates recent Deadlock patches; see [PATCHES.md](lib/demofile-net/PATCHES.md).

## Run (dev)

```sh
dotnet run --project src/DeadlockClipper.Api   # http://localhost:5040
pnpm --dir web install
pnpm --dir web dev                             # http://localhost:5173, proxies /api
```

## Run (single port)

```sh
pnpm --dir web build                           # outputs into the API's wwwroot
dotnet run --project src/DeadlockClipper.Api   # UI and API on http://localhost:5040
```

## Config (`src/DeadlockClipper.Api/appsettings.json`)

| Key | Default | |
|---|---|---|
| `Clipper:DataDir` | `data` | SQLite db, uploaded demos, hero cache |
| `Clipper:ReplaysDir` | Deadlock `game/citadel/replays` | Listed under "Local replays"; imported in place, never copied or deleted |
| `Clipper:HeroesUrl` | assets.deadlock-api.com | Hero id → name, cached to `data/heroes.json` |

## Tests

```sh
dotnet test
DEADLOCK_TEST_DEMO=/path/to/match.dem dotnet test   # also parses a real demo; checks kill feed == scoreboard per player
```

## Roadmap

1. Paste a match ID to download the demo from Valve (`POST /api/demos/from-match/{id}` is stubbed and returns 501).
2. Auto mode: watch the replays folder or match history and clip new games automatically.
3. Rendering: drive the game's demo playback (`playdemo`, `demo_gototick`) and record with OBS over obs-websocket, using each highlight's `clipStartTick`/`clipEndTick`. Then assemble with ffmpeg.

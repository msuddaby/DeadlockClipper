# Deadlock Clipper

Allstar-style highlight clipper for Deadlock. Import a demo after a match, and it finds the multikills and renders them as captioned MP4 clips. Rendering is invisible and silent: the game runs headless in the background while you keep using your PC.

- `src/DeadlockClipper.Core`: demo parsing (DemoFile.Net) and highlight detectors. No web dependencies.
- `src/DeadlockClipper.Render`: drives a headless Deadlock to record highlights and finishes them with ffmpeg. No web dependencies, so a remote render worker can host it later.
- `src/DeadlockClipper.Render.Cli`: renders highlights from the command line (smoke testing).
- `src/DeadlockClipper.Api`: ASP.NET Core minimal API, SQLite, background parse and render queues.
- `web/`: React + Vite + TanStack Query.
- `tools/`: Deadlock launch wrapper and WirePlumber audio rule (see setup).
- `lib/demofile-net`: vendored and patched DemoFile.Net. Upstream's schema predates recent Deadlock patches; see [PATCHES.md](lib/demofile-net/PATCHES.md).

## Rendering setup (Linux, once)

1. **Packages:** `gamescope`, `gstreamer` with `gst-plugin-va` (AMD/Intel hardware H.264; or set `Clipper:Render:VideoEncoder` to `x264enc`), `gst-plugin-pipewire`, `xdotool`, `pipewire`, `libpulse` (`pactl`), `ffmpeg`.
2. **Launch options:** in Steam, Deadlock → Properties → Launch Options:
   ```
   /path/to/DeadlockClipper/tools/deadlock-launch-wrapper.sh %command%
   ```
   Pressing Play runs the game exactly as before. When the clipper renders, the wrapper does four things:
   - snapshots your Deadlock settings,
   - runs the game in headless gamescope at 1920x1080 with its audio sent to a silent sink,
   - restores your settings when it exits,
   - on the next launch, restores them first if a render was killed.
3. **Audio rule:** stops PipeWire remembering that Deadlock's audio was silenced:
   ```sh
   cp tools/wireplumber/51-deadlock-clipper.conf ~/.config/wireplumber/wireplumber.conf.d/
   systemctl --user restart wireplumber
   ```

The app checks all of this (`GET /api/render/health`) and shows what's missing.

Notes:
- Steam runs one game per account, so close Deadlock before rendering. Queued renders wait until you do.
- Rendering is real time: a clip takes about as long to record as it lasts, plus roughly 30–60 s per demo to load and seek. Seeks go forward in 20k-tick hops; a single long `demo_gototick` jump crashes the client.

## Run

```sh
pnpm --dir web install && pnpm --dir web build   # outputs into the API's wwwroot
dotnet run --project src/DeadlockClipper.Api     # UI and API on http://localhost:5040
```

For frontend development, run `pnpm --dir web dev` (http://localhost:5173, proxies `/api`) alongside the API.

Typical flow:
1. Finish a match and open the replay in-game, which downloads it.
2. In the app, **Import** it from Local replays.
3. Click ☆ next to your name.
4. Click **Render all mine**. Clips appear on the demo page and under **Clips**, and are saved to `~/Videos/Deadlock Clips/<match>/`.

## Config (`src/DeadlockClipper.Api/appsettings.json`)

| Key | Default | |
|---|---|---|
| `Clipper:DataDir` | `data` | SQLite db, uploaded demos, hero cache, render scratch space |
| `Clipper:ReplaysDir` | Deadlock `game/citadel/replays` | Listed under "Local replays"; imported in place, never copied or deleted |
| `Clipper:HeroesUrl` | assets.deadlock-api.com | Hero id → name, cached to `data/heroes.json` |
| `Clipper:Render:GameDir` | parent of `ReplaysDir` | Deadlock's `game/citadel` folder |
| `Clipper:Render:OutputDir` | `~/Videos/Deadlock Clips` | Finished clips |
| `Clipper:Render:Width` / `Height` / `Fps` | 1920 / 1080 / 60 | Render resolution |
| `Clipper:Render:VideoEncoder` | `vah264enc` | GStreamer encoder for the raw capture (`x264enc` for CPU) |

## Tests

```sh
dotnet test
DEADLOCK_TEST_DEMO=/path/to/match.dem dotnet test   # also parses a real demo; checks kill feed == scoreboard per player

# Render one highlight end to end without the web app:
dotnet run --project src/DeadlockClipper.Render.Cli -- <game/citadel/replays/match.dem> --highlight <steamId>-<tick>
```

## Roadmap

1. Remote render workers: host `DeadlockClipper.Render` on GPU boxes with their own Steam accounts.
2. Vertical 9:16 clips composited from HUD regions (hero portrait, kill feed, gameplay).
3. Paste a match ID to download the demo from Valve (`POST /api/demos/from-match/{id}` is stubbed and returns 501).
4. Auto mode: render your highlights automatically when a new replay appears.

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
1. Get the demo: either open the replay in-game and **Import** it from Local replays, or find it under
   **Matches** (below) and click **Download**.
2. Click ☆ next to your name.
3. Click **Render all mine**. Clips appear on the demo page and under **Clips**, and are saved to `~/Videos/Deadlock Clips/<match>/`.

### Finding matches without downloading demos

**Matches** lists your recent games scored from Valve's small match metadata files (every death with killer and
time), so you can see which ones have multikills before downloading a 200-400 MB replay. It talks to Deadlock's
Game Coordinator directly through **secondary Steam accounts** (Steam allows one in-game session per account, so
never your main one). Each account needs Deadlock in its library.

1. Under **Matches → Clipper accounts**, click **Add account** and sign in (Steam Guard code or approval in the Steam
   app). The password goes straight to Steam; only a refresh token is saved, owner-only, in
   `~/.config/deadlock-clipper/steam-accounts/`. (`dotnet run --project src/DeadlockClipper.Steam.Cli -- login` does
   the same from a terminal.)
2. From your main account, send one of them a Steam friend request (Valve only shares match history with friends).
   It's accepted automatically the next time that account connects.
3. Enter your SteamID64 and click **Scan match history**, then **Load older matches** for more.

Valve rate-limits match metadata per account and doesn't document the limit. The one published measurement is about
17 requests per 30 minutes ([DeadlockAnalytics #15](https://github.com/Maxalon/DeadlockAnalytics/pull/15)), so each
account makes one request every 110 s (~30 matches an hour); add accounts to scan faster. Matches with under 2 kills
are scored without a request, and still-downloadable, high-kill matches go first. Replays expire about three weeks
after a match.

The same CLI has `history`, `salts`, `meta` and `download` commands for debugging.

## Config (`src/DeadlockClipper.Api/appsettings.json`)

| Key | Default | |
|---|---|---|
| `Clipper:DataDir` | `data` | SQLite db, uploaded demos, hero cache, render scratch space |
| `Clipper:ReplaysDir` | Deadlock `game/citadel/replays` | Listed under "Local replays"; imported in place, never copied or deleted |
| `Clipper:HeroesUrl` | assets.deadlock-api.com | Hero id → name, cached to `data/heroes.json` |
| `Clipper:Steam:AccountsDir` | `~/.config/deadlock-clipper/steam-accounts` | Saved logins for the clipper Steam accounts |
| `Clipper:Steam:MetadataIntervalSeconds` | 110 | Minimum gap between metadata requests per account |
| `Clipper:Steam:IdleDisconnectSeconds` | 120 | Log an account off Steam after this long without requests |
| `Clipper:Render:GameDir` | parent of `ReplaysDir` | Deadlock's `game/citadel` folder |
| `Clipper:Render:OutputDir` | `~/Videos/Deadlock Clips` | Finished clips |
| `Clipper:Render:Width` / `Height` / `Fps` | 1920 / 1080 / 60 | Render resolution |
| `Clipper:Render:PreRollSeconds` | 5 | Playback before each clip/segment that's recorded then cut, so the post-jump stutter isn't in the clip |
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
3. Auto mode: render your highlights automatically when a new replay appears.

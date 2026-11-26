# Deadlock Clipper: scaling research brief

A self-contained description of the project for researching how to run it as a hosted service at scale. Numbers
marked **measured** come from running the system on real matches; **estimate** means derived, not observed.

## 1. What it is

An automatic highlight clipper for Valve's game **Deadlock** (a 6v6 MOBA/shooter, still invite-only as of
Oct 2026), in the spirit of Allstar (CS2/LoL/Dota clips) and Leetify (stats).

For a player it:

1. **Finds their matches** via Valve's Game Coordinator (match history) and **scores each for clip potential** from
   a ~160 KB metadata file, before downloading anything.
2. **Downloads the match replay** (a `.dem` demo, 130–630 MB) from Valve's replay CDN.
3. **Parses the demo** (kills, assists, chat, players, winner) and **detects highlights**: multikills, takedown
   streaks (kills + assists), teamfights, kill compilations.
4. **Renders each highlight to an MP4** by running the real game client headlessly, playing the demo back in
   real time with the camera on the player, screen-capturing it, and compositing styled captions (hero art, kill
   counters, victims' chat reactions) with ffmpeg.
5. Has a web UI (match list ranked by clip potential, match pages, clip gallery) and an **auto mode** that does all
   of the above unattended.

Today it runs on one desktop PC for one player. The question is what it takes to run it for many players.

## 2. Architecture and stack

| Part | Tech |
|---|---|
| API + background workers | .NET 10, ASP.NET Core minimal APIs, EF Core + SQLite, `BackgroundService` workers |
| Web UI | React 19, Vite, TanStack Query (served by the API) |
| Demo parsing | `demofile-net` (vendored, patched) for Source 2 demos; Google.Protobuf |
| Game Coordinator access | SteamKit2 (C# Steam client library) |
| Replay/metadata files | HTTP from `replay{cluster}.valve.net`; Zstandard-compressed despite `.bz2` names (ZstdSharp) |
| Game art for overlays/UI | ValveResourceFormat + SkiaSharp, read from the local game install's VPK files |
| Rendering | Deadlock (Windows build) via **Proton** on Linux, inside **gamescope** in headless mode; capture with **GStreamer** (PipeWire source, VA-API H.264) and **pw-record** for audio; commands sent via cfg files + an X key press (xdotool) |
| Captions | HTML templates rendered to transparent PNGs by **headless Chromium**, composited with **ffmpeg** |
| Final encode | ffmpeg libx264 `-preset slow -crf 24 -maxrate 12M`, AAC 160k, 1080p60 |

Projects: `DeadlockClipper.Core` (parsing, highlight detectors), `.Steam` (GC client, account pool, downloads),
`.Render` (game control, recording, compositing), `.GameData` (hero data/art from game files), `.Api` (HTTP API,
workers, SQLite), `web/` (UI), CLIs for render and Steam debugging.

## 3. Pipeline in detail

### 3.1 Match discovery (no demo needed)

- Talks to the **Deadlock Game Coordinator (GC)** as a Steam client using **secondary "bot" Steam accounts**
  (never the player's own account, because Steam allows one in-game session per account).
- Each bot account must **have Deadlock** (currently invite-only) and log in with Steam Guard once; only a refresh
  token is stored.
- **Match history** (GC msg 9112, 50 matches per page): only returned if the requested player is a **Steam friend**
  of the bot account. Bots auto-accept friend requests, so every user must friend at least one bot.
- **Match salts** (GC msg 9167): give the replay and metadata URLs. **This is the rate-limited call.**
- **Metadata file** (~160 KB): every death with killer and time, player stats, objectives, winner. No assists, no
  chat. Enough to detect multikills and teamfights and rank matches.
- **Replays expire 21 days after the match** (`replay_valid_through` = start + 21 d exactly, **measured**). Older
  matches can't be downloaded or clipped.

### 3.2 Download and parse

- Demo download from Valve's CDN: **not rate-limited** as far as observed. **Measured:** 355 MB in 9 s.
- Parse: **measured** ~20 s per demo (355 MB, 36-minute match) on a 32-thread desktop CPU; single-threaded per demo.
- Output: a JSON analysis stored in SQLite (players, ~70–130 kills, chat ~20–50 messages, highlights).

### 3.3 Rendering (the expensive part)

Per batch of clips from one demo, in one game session:

| Step | Time |
|---|---|
| Launch game (Proton + gamescope, to main menu) | ~1–2 min, once per batch (**measured**) |
| Load demo | ~10–20 s, once per demo |
| Seek: forward hops of ≤20,000 ticks (long jumps crash the client), 6 s settle each | ~6 s per ~5 min of game time |
| Point camera, settle | ~2 s per segment |
| Warm-up playback (recorded, then cut) | 5 s per segment |
| **Record** | **real time**: a 15 s clip takes 15 s |
| Encode (overlays + x264 slow) | **measured** ~15 s for a 28 s clip on 32 threads; an 80 s, 16-caption reel ~22 s at preset medium |

- **Rendering is locked to real time** because it's a screen capture of live playback. It can't run faster than the
  clip's length. More throughput needs more parallel game instances.
- **Estimate:** a worker does roughly **40–60 short clips per hour**, or one match's full highlight set (3–8 clips)
  in ~10–15 min.
- Multi-segment clips (compilations, teamfights) record each segment in one forward pass; a 13-segment, 80 s reel
  is several minutes of wall time.

**Per-instance resources (measured on the dev box):** game process ~5 CPU cores and ~6–7 GB RAM during playback;
a few GB VRAM at 1080p; ffmpeg encode bursts up to ~10 cores and up to ~2.6 GB RAM for long reels.

**Dev box:** AMD RX 7900 XT (20 GB VRAM, VA-API encode), 32 CPU threads, 62 GB RAM, Linux (CachyOS).
**Estimate:** ~3 concurrent instances would fit on this hardware.

### 3.4 Output

- 1080p60 H.264 at ~12 Mbit/s: **measured** ~42 MB for a 28 s clip (VMAF 95.6 against the previous CRF 20 encode).
- Clip lengths: multikills 10–30 s, teamfights 40–80 s, compilations 60–140 s.

## 4. Measured limits and facts

| Fact | Value |
|---|---|
| GC match-history calls | never refused in testing |
| GC salt/metadata calls, burst | refused after ~45 requests at 2 s spacing (**measured**) |
| Recovery after that burst | ~80 min (**measured**, but refused retries during the block may have extended it) |
| Sustained rate that never got refused | 1 request / 110 s per account (~33/hour): 47 and 20 requests on two accounts over ~2.5 h, 0 refusals (**measured**) |
| Community-reported limits | one published probe: ~17 per 30 min per account; community tools self-cap at 40/day (unverified) |
| Replay lifetime | 21 days |
| Demo size | 130–630 MB, typically 300–360 MB for a 30–40 min match |
| Metadata size | ~160 KB |
| Steam | one in-game session per account; a bot account per concurrent game instance |
| Deadlock access | invite-only playtest (Oct 2026); every bot account needs an invite |

## 5. What blocks scaling (known)

1. **Steam accounts:** one account (with Deadlock access) per concurrently running game instance, plus accounts
   for GC requests (~33 metadata reads/hour each). Users must Steam-friend a bot for history access.
2. **Valve's terms:** running game clients on servers for a commercial service, and automating Steam accounts, are
   grey areas under the Steam Subscriber Agreement. Account bans are a risk at scale. Unknown whether Valve offers
   or would grant any sanctioned access.
3. **Real-time rendering:** throughput scales only with parallel game instances.
4. **Single-instance assumptions in code:** shared game install paths (`console.log`, command cfg files), a fixed
   PipeWire capture target and audio sink, one render worker. Needs per-instance isolation (planned: one
   container per instance with its own Steam client, gamescope, PipeWire, sharing a read-only game install).
5. **GPU vendor:** capture currently uses VA-API (AMD/Intel). NVIDIA needs NVENC (`nvh264enc`); headless
   gamescope + Proton on NVIDIA is less proven.
6. **Game updates:** Valve patches often; every worker's install must update (the app already detects Steam aborting
   a launch for an update and waits). Patches can also change demo formats and protobufs.
7. **Storage/bandwidth:** demos 300+ MB each (delete after rendering); clips ~1.5 MB per second of video.
8. **Replay window:** 21 days, so backfilling old matches is impossible; discovery must keep up.

## 6. Cost data gathered so far (unverified, from aggregators, Oct–Nov 2026)

| Option | Price |
|---|---|
| Vast.ai RTX 3060 (marketplace) | ~$0.03–0.06/GPU-hour (~$44/month 24/7) |
| AWS g4dn.xlarge (NVIDIA T4) | ~$0.53/h on-demand; spot ~$0.09–0.36/h by region |
| Hetzner GEX44 (RTX 4000 SFF Ada 20 GB, i5-13500) | €184/month at launch; ~€212–232/month after 2026 increases |
| Own hardware (used RTX 3060/RX 6600 tower or AMD 780M mini PC) | ~$400–600 one-off + ~$5–15/month power (estimate) |

**Estimate:** compute is under 1 cent per clip on any of these; accounts, terms and engineering are the real costs.

## 7. Questions to research

1. **Valve policy:** Does Valve permit (or have a programme for) third-party services rendering Deadlock replays?
   How do Allstar (CS2, Dota 2, LoL) and similar services handle this: partnerships, sanctioned APIs, or the same
   grey-area approach? Any history of Valve banning bot accounts used for GC data or replay rendering?
2. **Steam accounts at scale:** Practical and policy limits on running many Steam accounts; how deadlock-api.com and
   similar community services source and maintain bot accounts; getting Deadlock access for them while it's
   invite-only.
3. **GC rate limits:** Any better data on the per-account metadata limit (window, bucket size, whether refusals
   extend it), and whether limits are per account or per IP.
4. **Infrastructure:** Best way to run many headless Windows-game instances on Linux GPUs (Proton + gamescope) in
   containers or VMs. GPU sharing/density (several instances per GPU), NVIDIA vs AMD on cloud, and providers that
   allow game clients (some prohibit gaming workloads).
5. **Faster-than-real-time rendering:** Is there any way to render Source 2 demo playback offline frame by frame
   (like Source 1's `startmovie`), or to run playback at >1× speed and still capture smooth video? This would change
   the economics completely.
6. **Alternative architectures:**
   - **Client-side rendering:** users run a desktop app that renders on their own PC; the platform only coordinates.
     Cheap, avoids account issues; how do Allstar/Medal/Outplayed handle installs and trust?
   - **Hybrid:** cloud workers only for users without a capable PC.
7. **Cost model:** cost per clip and per active user under each option, including bot accounts, storage (S3/R2
   egress for video), and GPU hours at realistic utilisation.
8. **Legal/IP:** using game art (hero portraits, fonts read from the user's install) in rendered clips and the web
   UI; distributing clips publicly.

## 8. Current status

Working end to end on one machine: discovery, ranking, download, parse, multikill/takedown/teamfight/compilation
detection, chat reactions in clips, auto mode, and a Leetify/Allstar-style web UI. Two bot accounts in use for GC
access. Parked: one-container-per-instance render worker (the unit for multi-instance and remote workers).

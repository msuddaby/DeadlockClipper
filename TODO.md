# TODO

1. [x] **Real hero names.** Show "Drifter" instead of "Hero #64".
2. [x] **Allstar-style captions.** Styled graphics instead of plain text overlays.
3. [ ] **More clip types:**
   - [x] A compilation of all of one player's kills across the game, spliced together.
   - [ ] Dense fights: a large group of kills within about 5 minutes, clipped with the dead time cut out.
4. [x] **Scan games without downloading demos.** Find matches with good clip potential first. (Matches page: GC match
   history + metadata via secondary Steam accounts; demos downloaded in-app. Throughput is bounded by Valve's
   per-account rate limit, see item 8.)
5. [ ] *(minor)* **Stuttering** in rendered clips: investigate.
6. [x] *(lower priority)* **Count assists in highlights.** (TakedownDetector: kills + assists within 10 s, 3+ with an
   assist, e.g. the 34:39 Dynamo-ult play in match 135279703.) Several assists close together can be the real play: a
   Dynamo ultimate pulling the whole team in while teammates take the kills. Score and clip assist bursts like
   multikills. Note: match metadata only records each death's killer, not assisters, so this can only be detected
   from the demo (`KillEvent.AssisterSteamIds`), not during the no-download scan.
7. [x] **Smaller clip files.** (Now CRF 24 / slow / 12 Mbit/s cap: 35% smaller at VMAF 95.6, approved by eye.
   A size-targeted "share" preset is still open if wanted.) Clips are already H.264 (libx264 `-preset medium -crf 20`, AAC 160k), but that comes out
   at ~15 Mbit/s for 1080p60 (an 80 s reel was 153 MB). Try `-crf 23 -preset slow` with a bitrate cap
   (`-maxrate 8M -bufsize 16M`) and compare quality by eye; roughly half the size is likely. Maybe add a
   "share" preset that targets a file size (e.g. Discord's upload limit) with two-pass encoding. The raw capture is
   already lossy (VA-API at 12 Mbit/s), so don't squeeze so hard that the second generation shows artefacts.
8. [ ] **Remove the temporary GC quota logger** once Valve's metadata limit is pinned down: `QuotaLog`/`QuotaAnalysis`,
   `GET /api/steam/quota`, `Clipper:Steam:QuotaProbe` in appsettings.json. Tune `MetadataIntervalSeconds` and the
   backoff to what it found first.
9. [ ] *(parked)* **Render worker in a container.** One Steam client + bot account + gamescope + PipeWire per container,
   AMD GPU passed through, game install shared read-only. Unlocks rendering while you play (bot account instead of
   yours), ~3 parallel instances on the RX 7900 XT (CPU ~5 cores, RAM ~7 GB each), and the same unit as a remote
   worker for a platform. Needs: per-instance names for console.log/cfg files, render-request, capture target and
   silent sink; one RenderWorker per instance pulling from a queue; a Deadlock invite per bot account.
10. [x] **Teamfights in the Matches ranking** (from match metadata, no demo needed). A fight adds 1.5 x (your kills
    in it)^2, capped at the fight's score; stored matches are re-scored from their metadata files (no GC quota).
11. [x] **Auto mode.** Scans history every 30 min, downloads matches scoring >= a threshold (daily cap), imports new
    local replays, and renders your highlights once parsed. Matches page → Auto mode.

## Bugs
- [x] (fixed: playback start is now detected by frame-difference, not freezedetect) Rendered clips could open with the "GAME IS PAUSED" box fading out for about 0.5 s after resume; trimming only the frozen frames doesn't remove it. Fix: seek about 1 s before the clip start and trim by time, or hide the pause panel.

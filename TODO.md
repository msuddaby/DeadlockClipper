# TODO

1. [x] **Real hero names.** Show "Drifter" instead of "Hero #64".
2. [x] **Allstar-style captions.** Styled graphics instead of plain text overlays.
3. [ ] **More clip types:**
   - [ ] A compilation of all of one player's kills across the game, spliced together.
   - [ ] Dense fights: a large group of kills within about 5 minutes, clipped with the dead time cut out.
4. [ ] **Scan games without downloading demos.** Find matches with good clip potential first.
5. [ ] *(minor)* **Stuttering** in rendered clips: investigate.

## Bugs
- [x] (fixed: playback start is now detected by frame-difference, not freezedetect) Rendered clips could open with the "GAME IS PAUSED" box fading out for about 0.5 s after resume; trimming only the frozen frames doesn't remove it. Fix: seek about 1 s before the clip start and trim by time, or hide the pause panel.

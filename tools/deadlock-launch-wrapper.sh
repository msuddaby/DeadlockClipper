#!/usr/bin/env bash
# Deadlock launch wrapper for Deadlock Clipper.
#
# Set Deadlock's Steam launch options to:
#   /path/to/deadlock-launch-wrapper.sh %command% -console -condebug
#
# Normal launches (pressing Play) run the game unchanged, with your own settings.
# When the clipper wants to render, it writes a one-shot request file first. The next launch then:
#   1. snapshots your Deadlock settings (video.txt, convars) for every Steam account on this machine,
#   2. applies render settings (fixed resolution, borderless),
#   3. runs the game inside headless gamescope (invisible) with its audio sent to a silent sink,
#   4. restores your snapshot when the game exits.
# If a render is killed before step 4, the next launch of either kind restores the snapshot first.

set -u

RUNTIME_DIR="${XDG_RUNTIME_DIR:-/tmp}/deadlock-clipper"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/deadlock-clipper"
REQUEST="$RUNTIME_DIR/render-request"
BACKUP="$STATE_DIR/settings-backup"
LOG="$STATE_DIR/wrapper.log"
SINK="deadlock_clipper_silent"
APPID=1422450
STEAM_ROOT="${STEAM_COMPAT_CLIENT_INSTALL_PATH:-$HOME/.local/share/Steam}"

mkdir -p "$STATE_DIR"
log() { echo "$(date -Is) $*" >> "$LOG"; }

# Per-account files the game rewrites. Paths are relative to userdata/<account>/<appid>/.
SETTINGS_FILES=(
  local/cfg/video.txt
  local/cfg/machine_convars.vcfg
  local/cfg/user_convars_0_slot0.vcfg
  local/cfg/user_convars_0_slot0.vcfg_lastclouded
  remote/user_convars.vcfg
)

account_dirs() {
  local d
  for d in "$STEAM_ROOT"/userdata/*/"$APPID"; do
    [[ -d "$d/local/cfg" ]] && echo "$d"
  done
}

snapshot_settings() {
  rm -rf "$BACKUP.tmp"
  local acct f
  while read -r acct; do
    local id; id=$(basename "$(dirname "$acct")")
    for f in "${SETTINGS_FILES[@]}"; do
      [[ -f "$acct/$f" ]] || continue
      mkdir -p "$BACKUP.tmp/$id/$(dirname "$f")"
      cp -p "$acct/$f" "$BACKUP.tmp/$id/$f"
    done
  done < <(account_dirs)
  # Atomic: a backup either exists completely or not at all.
  mv "$BACKUP.tmp" "$BACKUP"
  log "snapshot saved"
}

restore_settings() {
  [[ -d "$BACKUP" ]] || return 0
  local id f
  for id in "$BACKUP"/*; do
    [[ -d "$id" ]] || continue
    local acct="$STEAM_ROOT/userdata/$(basename "$id")/$APPID"
    while read -r f; do
      cp -p "$id/$f" "$acct/$f"
    done < <(cd "$id" && find . -type f -printf '%P\n')
  done
  rm -rf "$BACKUP"
  log "snapshot restored"
}

apply_render_settings() {
  local acct
  while read -r acct; do
    local v="$acct/local/cfg/video.txt"
    [[ -f "$v" ]] || continue
    sed -i -E \
      -e "s/(\"setting.defaultres\"\s+)\"[0-9]+\"/\1\"$WIDTH\"/" \
      -e "s/(\"setting.defaultresheight\"\s+)\"[0-9]+\"/\1\"$HEIGHT\"/" \
      -e "s/(\"setting.fullscreen\"\s+)\"[0-9]+\"/\1\"0\"/" \
      -e "s/(\"setting.nowindowborder\"\s+)\"[0-9]+\"/\1\"1\"/" \
      "$v"
  done < <(account_dirs)
}

# A leftover backup means a previous render never finished; put the player's settings back first.
restore_settings

if [[ ! -f "$REQUEST" ]]; then
  exec "$@"
fi

# Render mode. Consume the request so a later manual Play launches normally even if this run crashes.
WIDTH=1920 HEIGHT=1080 FPS=60
# shellcheck disable=SC1090
source "$REQUEST"
rm -f "$REQUEST"
log "render launch ${WIDTH}x${HEIGHT}@${FPS}: $*"

snapshot_settings
apply_render_settings

# Silent output for the game's audio. The clipper moves the game's stream here (Wine ignores PULSE_SINK)
# and records this sink's monitor; nothing reaches the speakers.
if ! pactl list short sinks 2>/dev/null | grep -q "$SINK"; then
  pactl load-module module-null-sink "sink_name=$SINK" \
    "sink_properties=device.description=Deadlock-Clipper-Silent" >> "$LOG" 2>&1
fi

# Not exec: the wrapper must outlive the game to restore settings afterwards.
# Steam stops the game with SIGTERM/SIGINT; forward it, then still restore.
trap 'kill -TERM "$child" 2>/dev/null' TERM INT
gamescope --backend headless \
  -w "$WIDTH" -h "$HEIGHT" -W "$WIDTH" -H "$HEIGHT" -r "$FPS" \
  -- "$@" &
child=$!
wait "$child"; status=$?
# wait returns early when a trapped signal arrives; wait again for the real exit.
while kill -0 "$child" 2>/dev/null; do wait "$child"; status=$?; done

restore_settings
log "render session ended (status $status)"
exit "$status"

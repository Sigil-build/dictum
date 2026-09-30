#!/usr/bin/env bash
# Smoke test of the published Linux binary in a headless X11 session: secrets through the Secret
# Service, the daemon over IPC, and the push-to-talk hotkey through XRecord (keys sent with xdotool).
# Usage: linux-daemon-smoke.sh <dir with dictum> <whisper model> <wav with speech>
set -euo pipefail
bin="$1"; model="$2"; wav="$3"
export PATH="$bin:$PATH"

work="$(mktemp -d)"
export DICTUM_CONFIG_DIR="$work/cfg" DICTUM_DATA_DIR="$work/data" DICTUM_AUDIO_FILE="$wav"
export XDG_RUNTIME_DIR="$work/run" DISPLAY=:99 XDG_SESSION_TYPE=x11 XDG_CURRENT_DESKTOP=CI
mkdir -p "$DICTUM_CONFIG_DIR" "$DICTUM_DATA_DIR/models" && mkdir -m 700 "$XDG_RUNTIME_DIR"
cp "$model" "$DICTUM_DATA_DIR/models/"
cat > "$DICTUM_CONFIG_DIR/config.json" <<EOF
{
  "general": { "sounds": false },
  "stt": { "local": { "type": "whisper-local", "model": "$(basename "$model")", "runtime": "cpu" } },
  "profiles": [ { "id": "dictate", "hotkey": "Ctrl+Alt+Space", "stt": "local", "language": "en", "output": "clipboard" } ]
}
EOF

Xvfb :99 -screen 0 1024x768x24 >/dev/null 2>&1 &
sleep 2

dbus-run-session -- bash -euo pipefail -c '
  echo -n ci | gnome-keyring-daemon --unlock --components=secrets >/dev/null

  echo "--- secrets"
  echo sk-ci-123 | dictum secret set ci
  dictum secret check ci
  dictum secret delete ci
  ! dictum secret check ci

  echo "--- daemon"
  dictum daemon > daemon.log 2>&1 &
  daemon=$!
  trap "kill $daemon 2>/dev/null || true; cat daemon.log" EXIT
  for _ in $(seq 20); do dictum status >/dev/null 2>&1 && break; sleep 0.5; done
  dictum status

  echo "--- toggle over IPC"
  dictum toggle; sleep 1; dictum toggle
  for _ in $(seq 30); do xclip -selection clipboard -o 2>/dev/null | grep -qi country && break; sleep 0.5; done
  xclip -selection clipboard -o | grep -qi country

  echo "--- push-to-talk hotkey"
  echo -n cleared | xclip -selection clipboard -i
  xdotool keydown ctrl alt space; sleep 1; xdotool keyup space alt ctrl
  for _ in $(seq 30); do xclip -selection clipboard -o 2>/dev/null | grep -qi country && break; sleep 0.5; done
  xclip -selection clipboard -o | grep -qi country

  dictum doctor
  echo "--- all good"
'

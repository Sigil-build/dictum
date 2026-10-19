#!/usr/bin/env bash
# Removes the per-user Dictum that install.sh put in ~/.local/lib/dictum: the program, its links,
# its menu entry and icons, and the login items and portal entry that start it. Settings and models stay.
set -euo pipefail

lib="$HOME/.local/lib/dictum"
bin="$HOME/.local/bin"
data=${XDG_DATA_HOME:-$HOME/.local/share}
config=${XDG_CONFIG_HOME:-$HOME/.config}

if [ -x "$lib/dictum-gui" ]; then
  "$lib/dictum-gui" --quit || echo "uninstall.sh: Dictum is still running; it stops when you log out." >&2
fi

# A file that starts this install (its Exec or ExecStart points into $lib), never another copy's.
ours() { [ -f "$1" ] && grep -q "\"$lib/" "$1"; }

unit="$config/systemd/user/dictum.service"
if ours "$unit"; then
  systemctl --user disable --now dictum.service > /dev/null 2>&1 || true
  rm -f "$unit"
  systemctl --user daemon-reload > /dev/null 2>&1 || true
fi
for entry in "$config/autostart/dictum.desktop" "$data/applications/dev.dictum.Dictum.desktop"; do
  if ours "$entry"; then rm -f "$entry"; fi
done

for link in "$bin/dictum" "$bin/dictum-gui"; do
  if [ -L "$link" ] && [ "$(readlink "$link")" = "$lib/$(basename "$link")" ]; then rm -f "$link"; fi
done
rm -f "$data/applications/dictum.desktop" "$data/icons/hicolor/256x256/apps/dictum.png" "$data/icons/hicolor/scalable/apps/dictum.svg"
rm -rf "${lib:?}"

echo "Dictum is removed. Your settings ($config/dictum) and models (${XDG_DATA_HOME:-$HOME/.local/share}/dictum) are kept."

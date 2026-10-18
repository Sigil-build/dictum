#!/usr/bin/env bash
# Exercises install.sh and uninstall.sh for one user in a throwaway HOME, with stand-in programs (no build needed):
# a first install, an upgrade over a "running" copy, an uninstall, and another copy's login items left alone.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
[ "$(uname -s)" = Linux ] || { echo "test-install.sh: Linux only (symlinks and XDG folders)" >&2; exit 2; }
work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT
export HOME="$work/home"
unset XDG_CONFIG_HOME XDG_DATA_HOME
mkdir -p "$HOME"
lib="$HOME/.local/lib/dictum"
fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "PASS: $*"; }

# A package folder as the tarball has it, with stand-ins that print their version and log --quit.
package() {
  local dir=$1 version=$2
  mkdir -p "$dir/share" "$dir/runtimes/linux-x64"
  printf '#!/bin/sh\necho %s\n' "$version" > "$dir/dictum"
  printf '#!/bin/sh\nif [ "${1:-}" = --quit ]; then echo quit >> "%s/quit.log"; exit 0; fi\necho gui\n' "$work" > "$dir/dictum-gui"
  chmod +x "$dir/dictum" "$dir/dictum-gui"
  echo native > "$dir/libSkiaSharp.so"
  echo whisper > "$dir/runtimes/linux-x64/libwhisper.so"
  cp "$root/packaging/linux/install.sh" "$root/packaging/linux/uninstall.sh" "$dir/"
  cp "$root/packaging/linux/dictum.desktop" "$root/packaging/icons/dictum.svg" "$root/packaging/icons/dictum-256.png" "$dir/share/"
  chmod +x "$dir/install.sh" "$dir/uninstall.sh"
}

# Review focus 3: only from the unpacked package.
mkdir -p "$work/empty"
cp "$root/packaging/linux/install.sh" "$work/empty/"
if bash "$work/empty/install.sh" > "$work/refused.log" 2>&1; then fail "install.sh ran without the programs next to it"; fi
[ ! -e "$lib" ] || fail "a refused install left $lib"
pass "refuses to run outside the unpacked package"

package "$work/v1" 0.3.0
"$work/v1/install.sh" > "$work/install1.log"
[ "$("$HOME/.local/bin/dictum")" = 0.3.0 ] || fail "~/.local/bin/dictum runs the installed dictum"
[ -L "$HOME/.local/bin/dictum-gui" ] && [ "$(readlink "$HOME/.local/bin/dictum-gui")" = "$lib/dictum-gui" ] || fail "~/.local/bin/dictum-gui links to $lib/dictum-gui"
grep -qx "Exec=\"$lib/dictum-gui\"" "$HOME/.local/share/applications/dictum.desktop" || fail "the menu entry starts $lib/dictum-gui"
grep -qx "Icon=dictum" "$HOME/.local/share/applications/dictum.desktop" || fail "the menu entry uses the dictum icon"
[ -f "$HOME/.local/share/icons/hicolor/256x256/apps/dictum.png" ] || fail "the 256 px icon"
[ -f "$HOME/.local/share/icons/hicolor/scalable/apps/dictum.svg" ] || fail "the scalable icon"
[ -x "$lib/uninstall.sh" ] || fail "uninstall.sh stays with the installed files"
[ ! -e "$work/quit.log" ] || fail "a first install asked something to quit"
pass "install"

# Review focus 1: the installed copy quits first, the folder is swapped, nothing of the old version stays.
echo old > "$lib/only-in-the-old-version.so"
package "$work/v2" 0.3.1
"$work/v2/install.sh" > "$work/install2.log"
[ "$("$HOME/.local/bin/dictum")" = 0.3.1 ] || fail "after the upgrade dictum is the new version"
grep -qx quit "$work/quit.log" || fail "the upgrade asked the installed copy to quit first"
[ ! -e "$lib/only-in-the-old-version.so" ] || fail "the upgrade left a file of the old version"
leftovers=$(find "$HOME/.local/lib" -maxdepth 1 -name 'dictum.*' | head -1)
[ -z "$leftovers" ] || fail "the upgrade left $leftovers"
pass "upgrade"

# Review focus 2: everything that starts this install goes; settings and models stay.
mkdir -p "$HOME/.config/autostart" "$HOME/.config/systemd/user" "$HOME/.config/dictum" "$HOME/.local/share/dictum/models"
printf '[Desktop Entry]\nExec="%s/dictum-gui"\n' "$lib" > "$HOME/.config/autostart/dictum.desktop"
printf '[Service]\nExecStart="%s/dictum" daemon\n' "$lib" > "$HOME/.config/systemd/user/dictum.service"
printf '[Desktop Entry]\nExec="%s/dictum" daemon\nNoDisplay=true\n' "$lib" > "$HOME/.local/share/applications/dev.dictum.Dictum.desktop"
echo keep > "$HOME/.config/dictum/config.json"
echo keep > "$HOME/.local/share/dictum/models/m.bin"
"$lib/uninstall.sh" > "$work/uninstall.log"
for gone in "$lib" "$HOME/.local/bin/dictum" "$HOME/.local/bin/dictum-gui" \
  "$HOME/.local/share/applications/dictum.desktop" "$HOME/.local/share/applications/dev.dictum.Dictum.desktop" \
  "$HOME/.local/share/icons/hicolor/256x256/apps/dictum.png" "$HOME/.local/share/icons/hicolor/scalable/apps/dictum.svg" \
  "$HOME/.config/autostart/dictum.desktop" "$HOME/.config/systemd/user/dictum.service"; do
  { [ -e "$gone" ] || [ -L "$gone" ]; } && fail "uninstall left $gone"
done
[ -f "$HOME/.config/dictum/config.json" ] && [ -f "$HOME/.local/share/dictum/models/m.bin" ] || fail "uninstall removed settings or models"
pass "uninstall"

# Review focus 2: a login item of another copy (a portable folder) is not ours to remove.
"$work/v2/install.sh" > "$work/install3.log"
printf '[Desktop Entry]\nExec="/opt/elsewhere/dictum-gui"\n' > "$HOME/.config/autostart/dictum.desktop"
"$lib/uninstall.sh" > "$work/uninstall2.log"
[ -f "$HOME/.config/autostart/dictum.desktop" ] || fail "uninstall removed another copy's login item"
pass "another copy's login item stays"

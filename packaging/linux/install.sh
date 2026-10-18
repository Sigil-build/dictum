#!/usr/bin/env bash
# Installs Dictum for this user from the unpacked tarball (phase 4 spec 3.3): ~/.local/lib/dictum, links in ~/.local/bin,
# a menu entry and the icon. Running it again upgrades in place: a running Dictum from that folder is asked to quit,
# then the folder is swapped by a rename, so no running process ever has a file rewritten under it.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
lib="$HOME/.local/lib/dictum"
bin="$HOME/.local/bin"
data=${XDG_DATA_HOME:-$HOME/.local/share}

if [ ! -x "$here/dictum" ] || [ ! -x "$here/dictum-gui" ] || [ ! -f "$here/share/dictum.desktop" ]; then
  echo "install.sh: run it from the unpacked dictum-<version>-linux-x64 folder." >&2
  exit 1
fi

# The installed copy stops its desktop app and its daemon and waits for them (dictum-gui --quit); a first install has none.
if [ -x "$lib/dictum-gui" ] && ! "$lib/dictum-gui" --quit; then
  echo "install.sh: Dictum is still running; it keeps its old files until it starts again." >&2
fi

mkdir -p "$(dirname "$lib")" "$bin" "$data/applications" "$data/icons/hicolor/256x256/apps" "$data/icons/hicolor/scalable/apps"
staging="$lib.new.$$"
retired="$lib.old.$$"
rm -rf "${staging:?}"
cp -a "$here/." "$staging"
rm -f "${staging:?}/install.sh"
if [ -e "$lib" ]; then
  mv "$lib" "$retired"
fi
mv "$staging" "$lib"
rm -rf "${retired:?}"

ln -sfn "$lib/dictum" "$bin/dictum"
ln -sfn "$lib/dictum-gui" "$bin/dictum-gui"
sed "s|@EXEC@|$lib/dictum-gui|" "$lib/share/dictum.desktop" > "$data/applications/dictum.desktop"
cp "$lib/share/dictum-256.png" "$data/icons/hicolor/256x256/apps/dictum.png"
cp "$lib/share/dictum.svg" "$data/icons/hicolor/scalable/apps/dictum.svg"
if command -v update-desktop-database > /dev/null; then update-desktop-database -q "$data/applications" || true; fi
if command -v gtk-update-icon-cache > /dev/null; then gtk-update-icon-cache -q -t "$data/icons/hicolor" || true; fi

echo "Dictum $("$lib/dictum" --version | cut -d+ -f1) is installed in $lib."
echo "Start it from the menu, or run: $bin/dictum-gui"
case ":$PATH:" in
  *":$bin:"*) ;;
  *) echo "Add $bin to PATH to run 'dictum' from a terminal." ;;
esac
echo "To remove it: $lib/uninstall.sh"

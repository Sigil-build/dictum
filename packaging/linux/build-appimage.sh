#!/usr/bin/env bash
# Builds the Dictum AppImage from the staged files. appimagetool and the type-2 runtime are
# downloaded once into a cache and used only when their SHA-256 matches the pinned one.
# Usage: build-appimage.sh <stage-dir> <out-file>
set -euo pipefail

stage=${1:?usage: build-appimage.sh <stage-dir> <out-file>}
target=${2:?usage: build-appimage.sh <stage-dir> <out-file>}
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
tools=${DICTUM_APPIMAGE_TOOLS:-${XDG_CACHE_HOME:-$HOME/.cache}/dictum-appimage}

appimagetool="$tools/appimagetool-1.9.1-x86_64.AppImage"
runtime="$tools/runtime-20251108-x86_64"

# A cached or downloaded file is used only when its SHA-256 matches.
fetch() {
  local url=$1 file=$2 sum=$3
  if [ -f "$file" ] && echo "$sum  $file" | sha256sum -c --quiet - 2> /dev/null; then
    return 0
  fi
  curl -fsSL -o "$file.part" "$url"
  if ! echo "$sum  $file.part" | sha256sum -c --quiet - 2> /dev/null; then
    rm -f "${file:?}.part"
    echo "build-appimage.sh: $url does not match its pinned SHA-256" >&2
    exit 1
  fi
  mv "$file.part" "$file"
}

mkdir -p "$tools"
fetch https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage "$appimagetool" \
  ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
fetch https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64 "$runtime" \
  2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d
chmod +x "$appimagetool"

work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT
appdir="$work/Dictum.AppDir"
mkdir -p "$appdir/usr/lib/dictum"
# The programs' folder as staged, LICENSE and THIRD-PARTY-NOTICES.txt included; the tarball's scripts and share/ stay out.
cp -a "$stage/." "$appdir/usr/lib/dictum/"
rm -rf "${appdir:?}/usr/lib/dictum/install.sh" "${appdir:?}/usr/lib/dictum/uninstall.sh" "${appdir:?}/usr/lib/dictum/share"
install -m 755 "$here/AppRun" "$appdir/AppRun"
sed 's|"@EXEC@"|dictum-gui|' "$here/dictum.desktop" > "$appdir/dictum.desktop"
cp "$root/packaging/icons/dictum-256.png" "$appdir/dictum.png"
ln -s dictum.png "$appdir/.DirIcon"

mkdir -p "$(dirname "$target")"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" --no-appstream --runtime-file "$runtime" "$appdir" "$target"
chmod +x "$target"

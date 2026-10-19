#!/usr/bin/env bash
# Builds this OS's packages into a temp folder and checks them. Run it before committing packaging changes; CI runs it too.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) rid=win-x64 ;;
  Linux) rid=linux-x64 ;;
  *) echo "test-package.sh: no packages for $(uname -s)" >&2; exit 2 ;;
esac

out=${DICTUM_PACKAGE_OUT:-$(mktemp -d)}
version=$(dotnet msbuild "$root/src/Dictum.Cli/Dictum.Cli.csproj" -getProperty:VersionPrefix)
name="dictum-$version-$rid"
fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "PASS: $*"; }

if [ -z "${DICTUM_PACKAGE_OUT:-}" ]; then
  "$root/packaging/package.sh" "$rid" "$out" > "$out/package.log" 2>&1 || { tail -40 "$out/package.log"; fail "package.sh"; }
fi

stage="$out/stage/$name"
exe=""
[ "$rid" = win-x64 ] && exe=".exe"
for file in "dictum$exe" "dictum-gui$exe" runtimes uk; do
  [ -e "$stage/$file" ] || fail "the staged files lack $file"
done
symbols=$(find "$stage" \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \) | head -1)
[ -z "$symbols" ] || fail "debug symbols are staged: $symbols"
pass "staged files ($stage)"

if [ "$rid" = win-x64 ]; then
  archive="$out/$name.zip"
  [ -f "$archive" ] || fail "no $archive"
  entries=$(powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; ([IO.Compression.ZipFile]::OpenRead('$(cygpath -w "$archive")')).Entries | ForEach-Object { \$_.FullName }")
else
  archive="$out/$name.tar.gz"
  [ -f "$archive" ] || fail "no $archive"
  entries=$(tar -tzf "$archive")
fi
echo "$entries" | tr '\\' '/' | grep -q "^$name/dictum-gui$exe" || fail "the archive has no $name/dictum-gui$exe"
outside=$(echo "$entries" | tr '\\' '/' | grep -v "^$name/" | head -1 || true)
[ -z "$outside" ] || fail "the archive has an entry outside $name/: $outside"
echo "$entries" | grep -qiE '\.(pdb|dbg)$' && fail "the archive has debug symbols"
pass "portable archive ($archive)"

if [ "$rid" = win-x64 ]; then
  # The zip as a user unpacks it, run from there.
  unzipped=$(mktemp -d)
  powershell -NoProfile -Command "Expand-Archive -LiteralPath '$(cygpath -w "$archive")' -DestinationPath '$(cygpath -w "$unzipped")'" \
    || fail "the zip does not unpack"
  cli=$("$unzipped/$name/dictum.exe" --version | tr -d '\r')
  [ "${cli%%+*}" = "$version" ] || fail "the unzipped 'dictum --version' printed $cli"
  DICTUM_CONFIG_DIR=$(cygpath -w "$unzipped/config") DICTUM_DATA_DIR=$(cygpath -w "$unzipped/data") DICTUM_SOCKET=dictum-package-self-test \
    "$unzipped/$name/dictum-gui.exe" --self-test > "$out/zip-self-test.log" 2>&1 \
    || { cat "$out/zip-self-test.log"; fail "the unzipped dictum-gui's self-test"; }
  pass "the zip unpacks and its programs run ($unzipped)"
fi

if [ "$rid" = linux-x64 ]; then
  for file in install.sh uninstall.sh share/dictum.desktop share/dictum.svg share/dictum-256.png; do
    grep -qx "$name/$file" <<< "$entries" || fail "the tarball has no $name/$file"
  done
  pass "tarball scripts and menu entry"
fi

if [ "$rid" = linux-x64 ]; then
  appimage="$out/$name.AppImage"
  [ -x "$appimage" ] || fail "no executable $appimage"
  # No FUSE in containers and on CI runners: the runtime unpacks itself and runs (it still sets APPIMAGE and APPDIR).
  cli=$(APPIMAGE_EXTRACT_AND_RUN=1 "$appimage" dictum --version)
  [ "${cli%%+*}" = "$version" ] || fail "the AppImage's 'dictum --version' printed $cli"
  inside=$(cd "$(mktemp -d)" && "$appimage" --appimage-extract > /dev/null && find squashfs-root -maxdepth 4 | sort)
  for path in squashfs-root/AppRun squashfs-root/dictum.desktop squashfs-root/dictum.png squashfs-root/usr/lib/dictum/dictum squashfs-root/usr/lib/dictum/dictum-gui; do
    grep -qx "$path" <<< "$inside" || fail "the AppImage has no $path"
  done
  if grep -q "install.sh\|uninstall.sh" <<< "$inside"; then fail "the AppImage carries the tarball's scripts"; fi
  if command -v xvfb-run > /dev/null; then
    APPIMAGE_EXTRACT_AND_RUN=1 xvfb-run -a "$appimage" --self-test > "$out/appimage-self-test.log" 2>&1 || { cat "$out/appimage-self-test.log"; fail "the AppImage's self-test"; }
  fi
  pass "AppImage ($appimage)"
fi

if [ "$rid" = linux-x64 ]; then
  # The tarball as a user installs it: its own install.sh in a throwaway HOME, the programs from ~/.local/bin, then
  # its uninstall.sh. test-install.sh covers upgrades with stand-ins.
  user=$(mktemp -d)
  tar -xzf "$archive" -C "$user"
  mkdir -p "$user/home"
  as_user() { env -u XDG_CONFIG_HOME -u XDG_DATA_HOME HOME="$user/home" "$@"; }
  as_user bash "$user/$name/install.sh" > "$out/install.log" 2>&1 || { cat "$out/install.log"; fail "the tarball's install.sh"; }
  installed=$(as_user "$user/home/.local/bin/dictum" --version)
  [ "${installed%%+*}" = "$version" ] || fail "the installed 'dictum --version' printed $installed"
  grep -qF "\"$user/home/.local/lib/dictum/dictum-gui\"" "$user/home/.local/share/applications/dictum.desktop" \
    || fail "the menu entry does not start the installed dictum-gui"
  if command -v xvfb-run > /dev/null; then
    as_user xvfb-run -a "$user/home/.local/bin/dictum-gui" --self-test > "$out/installed-self-test.log" 2>&1 \
      || { cat "$out/installed-self-test.log"; fail "the installed dictum-gui's self-test"; }
  fi
  as_user bash "$user/home/.local/lib/dictum/uninstall.sh" > "$out/uninstall.log" 2>&1 \
    || { cat "$out/uninstall.log"; fail "the installed uninstall.sh"; }
  for path in .local/lib/dictum .local/bin/dictum .local/bin/dictum-gui .local/share/applications/dictum.desktop \
    .local/share/icons/hicolor/256x256/apps/dictum.png .local/share/icons/hicolor/scalable/apps/dictum.svg; do
    if [ -e "$user/home/$path" ] || [ -L "$user/home/$path" ]; then fail "uninstall.sh left $path"; fi
  done
  pass "the tarball installs, runs from ~/.local/bin and uninstalls"
fi

if [ "$rid" = win-x64 ]; then
  msi="$out/$name.msi"
  powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$root/packaging/windows/check-msi.ps1")" -Msi "$(cygpath -w "$msi")" -Version "$version" -Stage "$(cygpath -w "$stage")" || fail "check-msi.ps1"
  pass "MSI ($msi)"
fi

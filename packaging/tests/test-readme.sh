#!/usr/bin/env bash
# The README's install and removal steps name what the packages really are (phase 4 PR 4d review focus 5).
set -euo pipefail

readme=$(cd "$(dirname "$0")/../.." && pwd)/README.md
fail() { echo "FAIL: the README does not mention $*" >&2; exit 1; }
# The '~' paths are text to find in the README, not paths to expand.
# shellcheck disable=SC2088
for needle in \
  'dictum-<version>-win-x64.msi' 'dictum-<version>-win-x64.zip' '%LOCALAPPDATA%\Programs\Dictum' 'SmartScreen' 'Smart App Control' \
  'dictum-<version>-linux-x64.AppImage' 'dictum-<version>-linux-x64.tar.gz' 'install.sh' '~/.local/lib/dictum/uninstall.sh' \
  'makepkg -si' '.AppImage dictum ' 'glibc 2.38' 'xdg-desktop-portal' 'dictum doctor' 'dictum init' \
  '%APPDATA%\Dictum' '%LOCALAPPDATA%\Dictum' '~/.config/dictum' '~/.local/share/dictum' 'packaging/package.sh'; do
  grep -qF -- "$needle" "$readme" || fail "'$needle'"
done
echo "PASS: the README names the packages, the install folders, the AppImage's CLI word and where data lives"

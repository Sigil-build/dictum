#!/usr/bin/env bash
# Builds and installs dictum-bin in an Arch container from a tarball made by package.sh, then checks the files and
# runs dictum --version. Usage: test-pkgbuild.sh <dictum-<ver>-linux-x64.tar.gz>   (needs docker)
set -euo pipefail

tarball=${1:?usage: test-pkgbuild.sh <tarball>}
here=$(cd "$(dirname "$0")" && pwd)
version=$(basename "$tarball" | sed -E 's/^dictum-(.*)-linux-x64\.tar\.gz$/\1/')
tar -C "$(dirname "$tarball")" -cf - "$(basename "$tarball")" -C "$here" PKGBUILD | docker run -i --rm archlinux:latest bash -c "
  set -euo pipefail
  pacman -Syu --noconfirm --needed base-devel > /dev/null
  useradd -m builder
  mkdir /build && tar -C /build -xf - && chown -R builder /build
  sed -i 's/^pkgver=.*/pkgver=$version/' /build/PKGBUILD
  su builder -c 'cd /build && makepkg --noconfirm --nodeps' > /build/makepkg.log 2>&1 || { tail -30 /build/makepkg.log; exit 1; }
  pacman -U --noconfirm --nodeps /build/dictum-bin-*.pkg.tar.zst > /dev/null
  for path in /opt/dictum/dictum /opt/dictum/dictum-gui /usr/share/applications/dictum.desktop /usr/share/icons/hicolor/256x256/apps/dictum.png /usr/share/icons/hicolor/scalable/apps/dictum.svg \
    /usr/share/licenses/dictum-bin/LICENSE /usr/share/licenses/dictum-bin/THIRD-PARTY-NOTICES.txt; do
    [ -e \"\$path\" ] || { echo \"FAIL: no \$path\"; exit 1; }
  done
  [ \"\$(readlink /usr/bin/dictum)\" = /opt/dictum/dictum ] || { echo 'FAIL: /usr/bin/dictum'; exit 1; }
  grep -qx 'Exec=\"/opt/dictum/dictum-gui\"' /usr/share/applications/dictum.desktop || { echo 'FAIL: the menu entry'; exit 1; }
  [ ! -e /opt/dictum/install.sh ] || { echo 'FAIL: install.sh in /opt/dictum'; exit 1; }
  pacman -S --noconfirm --needed portaudio > /dev/null
  # An assignment from \$(…) stops the script under set -e when dictum fails; echo \$(…) would not.
  installed=\$(/usr/bin/dictum --version)
  [ \"\${installed%%+*}\" = '$version' ] || { echo \"FAIL: dictum --version printed \$installed\"; exit 1; }
  echo \"PASS: dictum-bin installs \$installed\"
"

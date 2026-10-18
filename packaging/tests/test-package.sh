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
  msi="$out/$name.msi"
  powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$root/packaging/windows/check-msi.ps1")" -Msi "$(cygpath -w "$msi")" -Version "$version" -Stage "$(cygpath -w "$stage")" || fail "check-msi.ps1"
  pass "MSI ($msi)"
fi

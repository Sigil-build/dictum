#!/usr/bin/env bash
# Builds Dictum's packages for one runtime, on that runtime's OS (Native AOT cannot cross-compile; phase 4 spec 6).
# Usage: packaging/package.sh <rid> <out-dir>      e.g. packaging/package.sh win-x64 artifacts/packages
# Writes <out>/stage/dictum-<version>-<rid>/ (the files), then dictum-<version>-<rid>.zip (Windows) or .tar.gz (Linux).
set -euo pipefail

rid=${1:?usage: package.sh <rid> <out-dir>}
out=${2:?usage: package.sh <rid> <out-dir>}
root=$(cd "$(dirname "$0")/.." && pwd)
version=$(dotnet msbuild "$root/src/Dictum.Cli/Dictum.Cli.csproj" -getProperty:VersionPrefix)
name="dictum-$version-$rid"
mkdir -p "$out"
out=$(cd "$out" && pwd)
stage="$out/stage/$name"

rm -rf "${stage:?}"
mkdir -p "$stage"
# Both apps in one folder: dictum-gui starts dictum from its own folder (spec 3).
dotnet publish "$root/src/Dictum.Cli" -c Release -r "$rid" -o "$stage"
dotnet publish "$root/src/Dictum.App" -c Release -r "$rid" -o "$stage"
find "$stage" \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \) -prune -exec rm -rf {} +

case "$rid" in
  win-*)
    rm -f "${out:?}/${name:?}.zip"
    powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('$(cygpath -w "$stage")', '$(cygpath -w "$out/$name.zip")', 'Optimal', \$true)"
    rm -rf "${out:?}/msi" "${out:?}/${name:?}.msi"
    dotnet build "$root/packaging/windows/Dictum.Installer.wixproj" -c Release \
      -p:StageDir="$(cygpath -w "$stage")" -o "$(cygpath -w "$out/msi")"
    cp "$out/msi/dictum.msi" "$out/$name.msi"
    ;;
  linux-*)
    tar -C "$out/stage" -czf "$out/$name.tar.gz" "$name"
    ;;
  *)
    echo "package.sh: no packages for $rid" >&2
    exit 2
    ;;
esac

echo "Packages in $out:"
ls -1 "$out" | grep "^$name\."

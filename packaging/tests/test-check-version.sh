#!/usr/bin/env bash
# check-version.sh accepts only the tag that names the version in Directory.Build.props (phase 4 PR 4d review focus 1).
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
version=$(dotnet msbuild "$root/src/Dictum.Cli/Dictum.Cli.csproj" -getProperty:VersionPrefix)
fail() { echo "FAIL: $*" >&2; exit 1; }

printed=$(bash "$root/packaging/check-version.sh" "v$version") || fail "v$version was refused"
[ "$printed" = "$version" ] || fail "v$version printed '$printed'"
echo "PASS: v$version is accepted and prints $version"

for wrong in "$version" "v$version.1" "v0.0.0" "release-$version" ""; do
  if bash "$root/packaging/check-version.sh" "$wrong" > /dev/null 2>&1; then fail "'$wrong' was accepted"; fi
done
echo "PASS: other tags are refused"

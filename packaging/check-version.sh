#!/usr/bin/env bash
# A release tag must name the version the packages are built with (VersionPrefix in Directory.Build.props).
# Usage: packaging/check-version.sh <tag>      prints the version and exits 0, or exits 1.
set -euo pipefail

tag=${1-}
root=$(cd "$(dirname "$0")/.." && pwd)
version=$(dotnet msbuild "$root/src/Dictum.Cli/Dictum.Cli.csproj" -getProperty:VersionPrefix)
if [ "$tag" != "v$version" ]; then
  echo "check-version.sh: the tag '$tag' does not match the version $version (expected v$version)" >&2
  exit 1
fi
echo "$version"

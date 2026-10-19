#!/usr/bin/env bash
# check-version.sh accepts only the tag that names the version in Directory.Build.props.
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

# The release workflow's own step, run the way Actions runs it (bash -eo pipefail): a refused tag must fail the step,
# not just print a message: a command substitution inside echo hides the exit status.
step=$(awk '
  state == 0 && /^ *id: version *$/ { state = 1; next }
  state == 1 && /^ *run:/ {
    indent = match($0, /[^ ]/) - 1
    rest = $0; sub(/^ *run: */, "", rest)
    if (rest != "|" && rest != ">" && rest != "") { print rest; exit }
    state = 2; next
  }
  state == 2 {
    if ($0 ~ /^ *$/) { print ""; next }
    if (match($0, /[^ ]/) - 1 <= indent) exit
    print substr($0, indent + 3)
  }
' "$root/.github/workflows/release.yml")
[ -n "$step" ] || fail "release.yml has no run: in the step with id version"
run_step() {
  : > "$work/output"
  (cd "$root" && GITHUB_REF_NAME=$1 GITHUB_OUTPUT="$work/output" bash --noprofile --norc -eo pipefail -c "$step") > /dev/null 2>&1
}
work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT
run_step "v$version" || fail "release.yml's version step failed on v$version"
[ "$(cat "$work/output")" = "version=$version" ] || fail "release.yml's version step wrote '$(cat "$work/output")' for v$version"
if run_step v0.0.0; then fail "release.yml's version step passed on v0.0.0 and wrote '$(cat "$work/output")'"; fi
echo "PASS: release.yml's version step writes the version and fails on another tag"

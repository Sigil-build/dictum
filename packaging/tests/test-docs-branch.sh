#!/usr/bin/env bash
# docs-branch.sh names the docs branch for a release tag, and docs.yml's branch step cuts it only once.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
fail() { echo "FAIL: $*" >&2; exit 1; }

branch_of() { bash "$root/packaging/docs-branch.sh" "$1"; }

for pair in v0.4.0:docs/0.4 v0.4.1:docs/0.4 v1.10.2:docs/1.10; do
  tag=${pair%%:*}
  want=${pair#*:}
  got=$(branch_of "$tag") || fail "$tag was refused"
  [ "$got" = "$want" ] || fail "$tag printed '$got', expected '$want'"
done
echo "PASS: a release tag names its docs/X.Y branch"

got=$(branch_of v0.5.0-rc.1) || fail "v0.5.0-rc.1 was refused"
[ -z "$got" ] || fail "v0.5.0-rc.1 printed '$got'"
echo "PASS: a pre-release tag names no branch and exits 0"

for bad in 0.4.0 vfoo v0.4 v0.4.0.1 v01.2.3 vx.y.z v0.4.0+build ""; do
  if bash "$root/packaging/docs-branch.sh" "$bad" > /dev/null 2>&1; then fail "'$bad' was accepted"; fi
done
echo "PASS: a malformed tag exits 1"

# The workflow's own step (the one with id cut), run the way Actions runs it (bash -eo pipefail)
# against a local bare repository standing in for origin.
step=$(awk '
  state == 0 && /^ *id: cut *$/ { state = 1; next }
  state == 1 && /^ *run:/ {
    indent = match($0, /[^ ]/) - 1
    state = 2; next
  }
  state == 2 {
    if ($0 ~ /^ *$/) { print ""; next }
    if (match($0, /[^ ]/) - 1 <= indent) exit
    print substr($0, indent + 3)
  }
' "$root/.github/workflows/docs.yml")
[ -n "$step" ] || fail "docs.yml has no run: in the step with id cut"

work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT
git init -q --bare "$work/origin.git"
git init -q "$work/repo"
mkdir "$work/repo/packaging"
cp "$root/packaging/docs-branch.sh" "$work/repo/packaging/"
repo() { git -C "$work/repo" -c user.name=t -c user.email=t@example.com -c commit.gpgsign=false -c tag.gpgsign=false "$@"; }
repo remote add origin "$work/origin.git"
repo commit -q --allow-empty -m one
repo tag v0.4.0
repo commit -q --allow-empty -m two
repo tag v0.4.1
repo commit -q --allow-empty -m three
repo tag v0.5.0-rc.1

run_step() {
  (cd "$work/repo" && TAG=$1 bash --noprofile --norc -eo pipefail -c "$step")
}
origin_heads() { git -C "$work/origin.git" for-each-ref --format='%(refname:short) %(objectname)' refs/heads/; }

run_step v0.4.0 > /dev/null 2>&1 || fail "the step failed on v0.4.0"
[ "$(origin_heads)" = "docs/0.4 $(repo rev-parse v0.4.0)" ] || fail "v0.4.0 did not create docs/0.4 at the tag: $(origin_heads)"
echo "PASS: the first release of a minor creates docs/X.Y at the tag"

out=$(run_step v0.4.1 2>&1) || fail "the step failed on v0.4.1"
[ "$(origin_heads)" = "docs/0.4 $(repo rev-parse v0.4.0)" ] || fail "v0.4.1 moved docs/0.4: $(origin_heads)"
case $out in *"::notice::"*"docs/0.4"*"by hand"*) ;; *) fail "v0.4.1 gave no notice about merging by hand: $out" ;; esac
echo "PASS: a patch release leaves the existing docs branch alone and says so"

run_step v0.5.0-rc.1 > /dev/null 2>&1 || fail "the step failed on v0.5.0-rc.1"
[ "$(origin_heads)" = "docs/0.4 $(repo rev-parse v0.4.0)" ] || fail "the pre-release changed the branches: $(origin_heads)"
echo "PASS: a pre-release creates no docs branch"

if run_step oops > /dev/null 2>&1; then fail "the step passed on a malformed tag"; fi
echo "PASS: a malformed tag fails the step"

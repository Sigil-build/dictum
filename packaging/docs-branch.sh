#!/usr/bin/env bash
# Names the docs branch a release tag belongs to: vX.Y.Z prints docs/X.Y.
# Usage: packaging/docs-branch.sh <tag>
#   vX.Y.Z        prints docs/X.Y and exits 0
#   vX.Y.Z-<pre>  prints nothing and exits 0: a pre-release has no docs branch
#   anything else prints a message to stderr and exits 1
set -euo pipefail

tag=${1-}
num='(0|[1-9][0-9]*)'
if [[ $tag =~ ^v$num\.$num\.$num$ ]]; then
  echo "docs/${BASH_REMATCH[1]}.${BASH_REMATCH[2]}"
elif [[ $tag =~ ^v$num\.$num\.$num-[0-9A-Za-z.-]+$ ]]; then
  exit 0
else
  echo "docs-branch.sh: the tag '$tag' is neither vX.Y.Z nor vX.Y.Z-<pre>" >&2
  exit 1
fi

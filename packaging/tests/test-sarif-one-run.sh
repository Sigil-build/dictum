#!/usr/bin/env bash
# sarif-one-run.sh merges the runs of a SARIF file into one run that code scanning accepts under the upload's category.
# Needs jq; the files are shaped as Snyk writes them.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
fail() { echo "FAIL: $*" >&2; exit 1; }

work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT

rule() { echo "{ \"id\": \"$1\", \"shortDescription\": { \"text\": \"High severity - $1\" } }"; }
result() {
  echo "{ \"ruleId\": \"$1\", \"level\": \"error\", \"message\": { \"text\": \"$1 in $2\" },
    \"locations\": [ { \"physicalLocation\": { \"artifactLocation\": { \"uri\": \"$2\" }, \"region\": { \"startLine\": 1 } } } ] }"
}

# Snyk Open Source with --all-projects: one run per project, with a category per project.
cat > "$work/open-source.sarif" << EOF
{
  "\$schema": "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
  "version": "2.1.0",
  "runs": [
    { "tool": { "driver": { "name": "Snyk Open Source", "version": "1.1308.2", "rules": [ $(rule SNYK-A) ] } },
      "automationDetails": { "id": "Snyk/Open Source/Dictum.App/0/2026-10-22T10:00:00.000Z" },
      "results": [ $(result SNYK-A src/Dictum.App/obj/project.assets.json) ] },
    { "tool": { "driver": { "name": "Snyk Open Source", "version": "1.1308.2", "rules": [] } },
      "automationDetails": { "id": "Snyk/Open Source/Dictum.Core/1/2026-10-22T10:00:00.000Z" },
      "results": [] },
    { "tool": { "driver": { "name": "Snyk Open Source", "version": "1.1308.2", "rules": [ $(rule SNYK-B), $(rule SNYK-A) ] } },
      "automationDetails": { "id": "Snyk/Open Source/Dictum.Host/2/2026-10-22T10:00:00.000Z" },
      "results": [ $(result SNYK-B src/Dictum.Host/obj/project.assets.json), $(result SNYK-A src/Dictum.Host/obj/project.assets.json) ] }
  ]
}
EOF

merge() { bash "$root/.github/scripts/sarif-one-run.sh" "$work/$1" "$work/$2"; }
q() { jq -c "$1" "$work/$2"; }

merge open-source.sarif open-source.out.sarif || fail "three runs of one tool were refused"
[ "$(q '.runs | length' open-source.out.sarif)" = 1 ] || fail "the merge left $(q '.runs | length' open-source.out.sarif) runs"
[ "$(q '.runs[0].tool.driver.name' open-source.out.sarif)" = '"Snyk Open Source"' ] || fail "the merged run lost its tool"
[ "$(q '[.runs[0].tool.driver.rules[].id]' open-source.out.sarif)" = '["SNYK-A","SNYK-B"]' ] || fail "the rules are $(q '[.runs[0].tool.driver.rules[].id]' open-source.out.sarif)"
want='[["SNYK-A","src/Dictum.App/obj/project.assets.json"],["SNYK-B","src/Dictum.Host/obj/project.assets.json"],["SNYK-A","src/Dictum.Host/obj/project.assets.json"]]'
got=$(q '[.runs[0].results[] | [.ruleId, .locations[0].physicalLocation.artifactLocation.uri]]' open-source.out.sarif)
[ "$got" = "$want" ] || fail "the results are $got"
[ "$(q '.runs[0] | has("automationDetails")' open-source.out.sarif)" = false ] || fail "the merged run kept a category"
[ "$(q '.version' open-source.out.sarif)" = '"2.1.0"' ] || fail "the merge dropped the SARIF version"
echo "PASS: the runs of one tool become one run with every rule once and every result, and no category of its own"

# Snyk Code: one run whose results point into its rules by index; only the category goes.
cat > "$work/code.sarif" << EOF
{
  "version": "2.1.0",
  "runs": [
    { "tool": { "driver": { "name": "SnykCode", "rules": [ $(rule csharp/A), $(rule csharp/B) ] } },
      "automationDetails": { "id": "Snyk/Code/2026-10-22T10:00:00.000Z" },
      "results": [ { "ruleId": "csharp/B", "ruleIndex": 1, "message": { "text": "B" } } ] }
  ]
}
EOF
merge code.sarif code.out.sarif || fail "a single run was refused"
[ "$(q '.runs[0].results[0].ruleIndex' code.out.sarif)" = 1 ] || fail "a single run lost its ruleIndex"
[ "$(q '[.runs[0].tool.driver.rules[].id]' code.out.sarif)" = '["csharp/A","csharp/B"]' ] || fail "a single run's rules changed"
[ "$(q '.runs[0] | has("automationDetails")' code.out.sarif)" = false ] || fail "a single run kept its category"
echo "PASS: a single run keeps its rules and indexes and loses only its category"

# Results that index their own run's rules lose the index once the rules are merged.
cat > "$work/indexed.sarif" << EOF
{
  "version": "2.1.0",
  "runs": [
    { "tool": { "driver": { "name": "T", "rules": [ $(rule X) ] } }, "results": [ { "ruleId": "X", "ruleIndex": 0, "message": { "text": "x" } } ] },
    { "tool": { "driver": { "name": "T", "rules": [ $(rule Y) ] } }, "results": [ { "ruleId": "Y", "ruleIndex": 0, "message": { "text": "y" } } ] }
  ]
}
EOF
merge indexed.sarif indexed.out.sarif || fail "two indexed runs were refused"
[ "$(q '[.runs[0].results[] | has("ruleIndex")]' indexed.out.sarif)" = '[false,false]' ] || fail "merged results kept a ruleIndex"
echo "PASS: merged results drop ruleIndex, which pointed into their own run's rules"

echo '{ "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "A" } } }, { "tool": { "driver": { "name": "B" } } } ] }' > "$work/two-tools.sarif"
echo '{ "version": "2.1.0", "runs": [] }' > "$work/no-runs.sarif"
echo 'not json' > "$work/text.sarif"
: > "$work/empty.sarif"
for bad in two-tools.sarif no-runs.sarif text.sarif empty.sarif missing.sarif; do
  status=0; merge "$bad" out.sarif 2> /dev/null || status=$?
  [ "$status" -eq 1 ] || fail "$bad exited $status, expected 1"
  [ ! -e "$work/out.sarif" ] && [ ! -e "$work/out.sarif.part" ] || fail "$bad left an output file"
done
echo "PASS: two tools, no run, an empty file or no SARIF exit 1 and write nothing"

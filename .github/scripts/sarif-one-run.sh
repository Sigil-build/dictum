#!/usr/bin/env bash
# Writes <out> with every run of the SARIF file <in> merged into one, without automationDetails, so that the upload's
# category names it. Snyk Open Source writes a run per project, with a category made of the project's name and index:
# code scanning takes at most 20 runs a file, and a project added before another would move that one's alerts to a new
# category. snyk.yml uploads Snyk's results through it.
# The merged run keeps the first run's tool and fields, the rules of all runs (each id once, the first one seen) and
# the results of all runs, without ruleIndex, which pointed into each run's own rules.
# Usage: sarif-one-run.sh <in.sarif> <out.sarif>
#   exit 0: <out> written
#   exit 1: <in> is not SARIF, has no run, or has runs of more than one tool
set -euo pipefail

in=${1:?usage: sarif-one-run.sh <in.sarif> <out.sarif>}
out=${2:?usage: sarif-one-run.sh <in.sarif> <out.sarif>}

# input, not ., so that an empty file is an error too.
if ! jq -n 'input |
  if (.runs | length) == 0 then error("it has no run")
  elif ([.runs[].tool.driver.name] | unique | length) > 1 then
    error("its runs come from more than one tool: \([.runs[].tool.driver.name] | unique | join(", "))")
  elif (.runs | length) == 1 then .runs[0] |= del(.automationDetails)
  else .runs = [
    reduce .runs[1:][] as $run (.runs[0];
      .tool.driver.rules = ((.tool.driver.rules // []) + ($run.tool.driver.rules // []))
      | .results = ((.results // []) + ($run.results // [])))
    | .tool.driver.rules |= reduce .[] as $rule ([]; if any(.[]; .id == $rule.id) then . else . + [$rule] end)
    | .results |= map(del(.ruleIndex))
    | del(.automationDetails)
  ]
  end' "$in" > "$out.part"; then
  rm -f "${out:?}.part"
  echo "sarif-one-run.sh: the runs of $in cannot be merged" >&2
  exit 1
fi
mv "$out.part" "$out"

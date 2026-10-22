#!/usr/bin/env bash
# Reads the report of `dotnet list package --vulnerable --include-transitive --format json --output-version 1` and
# prints each vulnerable package, and each problem (a project with no assets file, a restore that failed), as a GitHub
# error annotation. security.yml's nuget job runs it.
# Usage: nuget-vulnerable.sh <report.json>
#   exit 0: the report lists projects and none has a vulnerable package or a problem
#   exit 1: a vulnerable package or a problem, or a report that lists no project
#   exit 2: the file is not such a report
set -euo pipefail

report=${1:?usage: nuget-vulnerable.sh <report.json>}

# input, not ., so that an empty file is an error too.
if ! version=$(jq -n -r 'input | .version' "$report" 2> /dev/null) || [ "$version" != 1 ]; then
  echo "::error::$report is not a version 1 report of dotnet list package"
  exit 2
fi

# One line per finding; % and line breaks escaped as workflow commands need them.
findings=$(jq -r '
  def escape: gsub("%"; "%25") | gsub("\r"; "%0D") | gsub("\n"; "%0A");
  def file: split("/") | last;
  [ (.problems[]? | "\(if .project then (.project | file) else "dotnet list package" end): \(.text)"),
    (.projects[]? as $project | $project.frameworks[]? as $framework
      | (($framework.topLevelPackages[]? | . + {kind: "top-level"}),
         ($framework.transitivePackages[]? | . + {kind: "transitive"}))
      | . as $package | .vulnerabilities[]?
      | "\($project.path | file) [\($framework.framework)]: \($package.id) \($package.resolvedVersion) (\($package.kind)) has a \(.severity) severity advisory, \(.advisoryurl)")
  ] | .[] | escape' "$report")
projects=$(jq '[.projects[]?] | length' "$report")

if [ -n "$findings" ]; then
  while IFS= read -r line; do echo "::error::$line"; done <<< "$findings"
  exit 1
fi
if [ "$projects" -eq 0 ]; then
  echo "::error::The report lists no project, so nothing was checked."
  exit 1
fi
echo "No vulnerable packages in $projects projects, transitive ones included."

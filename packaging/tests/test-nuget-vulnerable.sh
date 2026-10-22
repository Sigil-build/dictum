#!/usr/bin/env bash
# nuget-vulnerable.sh fails on every vulnerable package and problem in a dotnet list package report, and security.yml's
# step fails whenever the listing or the report does. Needs jq; the reports are shaped as the .NET 10 SDK writes them.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
fail() { echo "FAIL: $*" >&2; exit 1; }

work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT
proj=/home/runner/work/dictum/dictum

cat > "$work/clean.json" << EOF
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": [ "https://api.nuget.org/v3/index.json" ],
  "projects": [
    { "path": "$proj/src/Dictum.App/Dictum.App.csproj" },
    { "path": "$proj/src/Dictum.Core/Dictum.Core.csproj" },
    { "path": "$proj/tests/Dictum.Core.Tests/Dictum.Core.Tests.csproj" }
  ]
}
EOF

cat > "$work/vulnerable.json" << EOF
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": [ "https://api.nuget.org/v3/index.json" ],
  "projects": [
    {
      "path": "$proj/src/Dictum.Host/Dictum.Host.csproj",
      "frameworks": [
        {
          "framework": "net10.0",
          "topLevelPackages": [
            {
              "id": "Newtonsoft.Json",
              "requestedVersion": "12.0.1",
              "resolvedVersion": "12.0.1",
              "vulnerabilities": [
                { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" }
              ]
            }
          ]
        }
      ]
    },
    {
      "path": "$proj/src/Dictum.Setup/Dictum.Setup.csproj",
      "frameworks": [
        {
          "framework": "net10.0",
          "transitivePackages": [
            {
              "id": "Newtonsoft.Json",
              "resolvedVersion": "9.0.1",
              "vulnerabilities": [
                { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" },
                { "severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-0000-0000-0000" }
              ]
            }
          ]
        }
      ]
    },
    { "path": "$proj/src/Dictum.Core/Dictum.Core.csproj" }
  ]
}
EOF

cat > "$work/unrestored.json" << EOF
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "problems": [
    {
      "project": "$proj/src/Dictum.Cli/Dictum.Cli.csproj",
      "level": "error",
      "text": "No assets file was found for \`$proj/src/Dictum.Cli/Dictum.Cli.csproj\`. Please run restore before running this command."
    }
  ],
  "sources": [ "https://api.nuget.org/v3/index.json" ],
  "projects": [ { "path": "$proj/src/Dictum.Core/Dictum.Core.csproj" } ]
}
EOF

cat > "$work/restore-failed.json" << 'EOF'
{
  "version": 1,
  "problems": [
    { "text": "Restore failed. Run `dotnet restore` for more details on the issue.", "level": "error" }
  ]
}
EOF

cat > "$work/escapes.json" << EOF
{
  "version": 1,
  "problems": [ { "project": "$proj/src/Dictum.Core/Dictum.Core.csproj", "level": "warning", "text": "100% of\nthe sources failed" } ],
  "projects": [ { "path": "$proj/src/Dictum.Core/Dictum.Core.csproj" } ]
}
EOF

echo '{ "version": 1, "parameters": "--vulnerable --include-transitive", "projects": [] }' > "$work/no-projects.json"
echo '{ "version": 2, "projects": [] }' > "$work/version-2.json"
echo 'Restore failed.' > "$work/text.json"
: > "$work/empty.json"

check() { bash "$root/.github/scripts/nuget-vulnerable.sh" "$work/$1"; }

out=$(check clean.json) || fail "a clean report failed: $out"
[ "$out" = "No vulnerable packages in 3 projects, transitive ones included." ] || fail "a clean report printed: $out"
echo "PASS: a report without findings passes and counts its projects"

status=0; out=$(check vulnerable.json) || status=$?
[ "$status" -eq 1 ] || fail "vulnerable packages exited $status"
want="::error::Dictum.Host.csproj [net10.0]: Newtonsoft.Json 12.0.1 (top-level) has a High severity advisory, https://github.com/advisories/GHSA-5crp-9r3c-p9vr
::error::Dictum.Setup.csproj [net10.0]: Newtonsoft.Json 9.0.1 (transitive) has a High severity advisory, https://github.com/advisories/GHSA-5crp-9r3c-p9vr
::error::Dictum.Setup.csproj [net10.0]: Newtonsoft.Json 9.0.1 (transitive) has a Moderate severity advisory, https://github.com/advisories/GHSA-0000-0000-0000"
[ "$out" = "$want" ] || fail "vulnerable packages printed:
$out"
echo "PASS: each advisory of a top-level or transitive package is an error annotation"

status=0; out=$(check unrestored.json) || status=$?
[ "$status" -eq 1 ] || fail "a project without an assets file exited $status"
case $out in "::error::Dictum.Cli.csproj: No assets file was found"*) ;; *) fail "a project without an assets file printed: $out" ;; esac
echo "PASS: a project the listing could not read fails the check"

status=0; out=$(check restore-failed.json) || status=$?
[ "$status" -eq 1 ] || fail "a failed restore exited $status"
[ "$out" = "::error::dotnet list package: Restore failed. Run \`dotnet restore\` for more details on the issue." ] || fail "a failed restore printed: $out"
echo "PASS: a failed restore fails the check"

status=0; out=$(check escapes.json) || status=$?
[ "$status" -eq 1 ] || fail "a warning exited $status"
[ "$out" = "::error::Dictum.Core.csproj: 100%25 of%0Athe sources failed" ] || fail "a warning with % and a line break printed: $out"
echo "PASS: a warning fails the check too, as one annotation with % and line breaks escaped"

status=0; out=$(check no-projects.json) || status=$?
[ "$status" -eq 1 ] || fail "a report without projects exited $status"
echo "PASS: a report that lists no project fails"

for bad in version-2.json text.json empty.json missing.json; do
  status=0; check "$bad" > /dev/null || status=$?
  [ "$status" -eq 2 ] || fail "$bad exited $status, expected 2"
done
echo "PASS: a file that is not a version 1 report exits 2"

# security.yml's own step (id: vulnerable), run the way Actions runs it (bash -e), with a stand-in dotnet that prints a
# report and exits with the status the real one would.
step=$(awk '
  state == 0 && /^ *id: vulnerable *$/ { state = 1; next }
  state == 1 && /^ *run:/ {
    indent = match($0, /[^ ]/) - 1
    state = 2; next
  }
  state == 2 {
    if ($0 ~ /^ *$/) { print ""; next }
    if (match($0, /[^ ]/) - 1 <= indent) exit
    print substr($0, indent + 3)
  }
' "$root/.github/workflows/security.yml")
[ -n "$step" ] || fail "security.yml has no run: in the step with id vulnerable"
case $step in *"--vulnerable --include-transitive"*"--format json --output-version 1"*) ;; *) fail "the step does not list transitive packages as a version 1 JSON report: $step" ;; esac

mkdir -p "$work/bin" "$work/temp"
cat > "$work/bin/dotnet" << 'EOF'
#!/usr/bin/env bash
cat "$FAKE_REPORT"
exit "$FAKE_STATUS"
EOF
chmod +x "$work/bin/dotnet"
run_step() {
  (cd "$root" && FAKE_REPORT="$work/$1" FAKE_STATUS=$2 RUNNER_TEMP="$work/temp" PATH="$work/bin:$PATH" \
    bash --noprofile --norc -e -c "$step")
}

run_step clean.json 0 > /dev/null 2>&1 || fail "the step failed on a clean report"
echo "PASS: the step passes on a clean report"

if run_step vulnerable.json 0 > /dev/null 2>&1; then fail "the step passed on vulnerable packages"; fi
echo "PASS: the step fails on vulnerable packages"

out=$(run_step restore-failed.json 1 2>&1) && fail "the step passed when dotnet list package failed"
case $out in *"::error::dotnet list package: Restore failed"*) ;; *) fail "the step did not report the problem: $out" ;; esac
echo "PASS: the step fails, with the report's problem, when the listing exits 1"

out=$(run_step clean.json 3 2>&1) && fail "the step passed when dotnet list package exited 3 with a clean report"
case $out in *"::error::dotnet list package exited with 3"*) ;; *) fail "the step did not name the exit status: $out" ;; esac
echo "PASS: the step fails when the listing exits non-zero, even with a clean report"

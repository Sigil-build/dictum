#!/usr/bin/env bash
# .github/scripts/virustotal.sh: the uploads within VirusTotal's rate limit, against a stand-in curl that answers from
# a queue and a stand-in sleep that only notes how long it was asked to wait; and the release notes' section, which a
# second run replaces. No network. Needs jq, as the runner has.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
script=$root/.github/scripts/virustotal.sh
fail() { echo "FAIL: $*" >&2; exit 1; }
command -v jq > /dev/null || fail "jq is not installed"

work=$(mktemp -d)
trap 'rm -rf "${work:?}"' EXIT
mkdir "$work/bin" "$work/tmp"
export STUB=$work/stub

# Logs "GET <url>" or "POST <url> <file name>" and prints the next answer's status, writing its body to -o's file.
# An answer is a line "<status> <body>"; "none" is no answer at all, as when curl times out.
cat > "$work/bin/curl" << 'EOF'
#!/usr/bin/env bash
out="" url="" form="" header=""
for arg in "$@"; do
  case $arg in *test-key*) echo "THE KEY IS ON THE COMMAND LINE" >> "$STUB/log" ;; esac
done
while [ $# -gt 0 ]; do
  case $1 in
    -o) out=$2; shift ;;
    -H) header=$2; shift ;;
    -F) form=$2; shift ;;
    -w | --max-time) shift ;;
    -*) ;;
    *) url=$1 ;;
  esac
  shift
done
[ "$(cat "${header#@}")" = "x-apikey: test-key" ] || echo "NO KEY HEADER" >> "$STUB/log"
if [ -n "$form" ]; then
  path=${form#file=@}
  echo "POST $url ${path##*/}" >> "$STUB/log"
else
  echo "GET $url" >> "$STUB/log"
fi
answer=$(head -n 1 "$STUB/answers")
if [ -z "$answer" ]; then
  echo "UNEXPECTED REQUEST" >> "$STUB/log"
  printf 000
  exit 7
fi
sed -i 1d "$STUB/answers"
if [ "$answer" = none ]; then
  printf 000
  exit 28
fi
printf '%s' "${answer#* }" > "$out"
printf '%s' "${answer%% *}"
EOF
cat > "$work/bin/sleep" << 'EOF'
#!/usr/bin/env bash
echo "sleep $1" >> "$STUB/log"
EOF
chmod +x "$work/bin/curl" "$work/bin/sleep"

api=https://www.virustotal.com/api/v3
sha() { sha256sum < "$1" | cut -d' ' -f1; }
# A line of the results: name, SHA-256, analysis id, why not sent.
row() { local IFS=$'\t'; echo "$*"; }

# A folder of release files: a name with "big" in it is over the 32 MB that POST /files takes.
files() {
  rm -rf "$work/files" && mkdir "$work/files"
  local name
  for name in "$@"; do
    case $name in
      *big*) truncate -s 32000001 "$work/files/$name" ;;
      *) echo "$name" > "$work/files/$name" ;;
    esac
  done
}

# Runs the upload with the answers given, one per argument; the log is in $STUB/log and the results in
# $work/results. Waits are the defaults unless the caller sets VT_* in the environment.
run_upload() {
  rm -rf "$STUB" && mkdir "$STUB"
  : > "$STUB/log"
  printf '%s\n' "$@" > "$STUB/answers"
  PATH="$work/bin:$PATH" TMPDIR="$work/tmp" VIRUSTOTAL_API_KEY=test-key \
    bash "$script" upload "$work/files" "$work/results" > "$work/out" 2>&1 || fail "upload exited $?: $(cat "$work/out")"
  [ -z "$(ls -A "$work/tmp")" ] || fail "upload left files in the temp folder: $(ls -A "$work/tmp")"
}

expect_log() {
  local want
  want=$(printf '%s\n' "$@")
  [ "$(cat "$STUB/log")" = "$want" ] || fail "$1 ...: the requests were
$(cat "$STUB/log")
expected
$want"
}

expect_results() {
  local want
  want=$(printf '%s\n' "$@")
  [ "$(cat "$work/results")" = "$want" ] || fail "the results were
$(cat "$work/results")
expected
$want"
}

files dictum-1.0.0-big-linux-x64.tar.gz dictum-1.0.0-win-x64.msi SHA256SUMS
big_sha=$(sha "$work/files/dictum-1.0.0-big-linux-x64.tar.gz")
msi_sha=$(sha "$work/files/dictum-1.0.0-win-x64.msi")
sums_sha=$(sha "$work/files/SHA256SUMS")
zip_sha=$(echo dictum-1.0.0-win-x64.zip | sha256sum | cut -d' ' -f1)
run_upload \
  '200 {"data": "https://www.virustotal.com/_ah/upload/one/"}' \
  '200 {"data": {"type": "analysis", "id": "id-big"}}' \
  '200 {"data": {"type": "analysis", "id": "id-msi"}}' \
  '200 {"data": {"type": "analysis", "id": "id-sums"}}'
expect_log \
  "GET $api/files/upload_url" \
  "sleep 15" \
  "POST https://www.virustotal.com/_ah/upload/one/ dictum-1.0.0-big-linux-x64.tar.gz" \
  "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.msi" \
  "sleep 15" \
  "POST $api/files SHA256SUMS"
expect_results \
  "$(row "dictum-1.0.0-big-linux-x64.tar.gz" "$big_sha" "id-big" "")" \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "id-msi" "")" \
  "$(row "SHA256SUMS" "$sums_sha" "id-sums" "")"
echo "PASS: a file over 32 MB goes to an upload URL, the rest to /files, packages first, 15 s apart, the key in a header file"

files dictum-1.0.0-win-x64.msi
run_upload '429 {"error": {"code": "QuotaExceededError"}}' '503 {}' none '200 {"data": {"id": "id-msi"}}'
expect_log \
  "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 60" \
  "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.msi"
expect_results "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "id-msi" "")"
echo "PASS: a 429 is tried again after 60 s, a 5xx and no answer after 15 s"

files dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip PKGBUILD
VT_RETRIES=1 run_upload '429 {"error": {"code": "QuotaExceededError"}}' '429 {"error": {"code": "QuotaExceededError"}}'
expect_log "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 60" "POST $api/files dictum-1.0.0-win-x64.msi"
expect_results \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "" "VirusTotal's request quota was used up")" \
  "$(row dictum-1.0.0-win-x64.zip "$zip_sha" "" "VirusTotal's request quota was used up")" \
  "$(row PKGBUILD "$(sha "$work/files/PKGBUILD")" "" "VirusTotal's request quota was used up")"
grep -q "::warning::PKGBUILD was not sent to VirusTotal: VirusTotal's request quota was used up." "$work/out" \
  || fail "no warning for a file not sent: $(cat "$work/out")"
echo "PASS: a quota still used up after the retries ends the uploads, and every file left says why"

files dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip
run_upload '401 {"error": {"code": "WrongCredentialsError"}}'
expect_log "POST $api/files dictum-1.0.0-win-x64.msi"
[ "$(cut -f4 "$work/results" | sort -u)" = "VirusTotal refused the API key" ] || fail "a refused key: $(cat "$work/results")"
echo "PASS: a refused key is not tried again and ends the uploads"

files dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip
run_upload '400 {"error": {"code": "BadRequestError"}}' '200 {"data": {"id": "id-zip"}}'
expect_log "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" "POST $api/files dictum-1.0.0-win-x64.zip"
expect_results \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "" "VirusTotal answered HTTP 400")" \
  "$(row dictum-1.0.0-win-x64.zip "$zip_sha" "id-zip" "")"
echo "PASS: a file VirusTotal refuses does not stop the next one"

files dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip
VT_DEADLINE=10 run_upload '200 {"data": {"id": "id-msi"}}'
expect_log "POST $api/files dictum-1.0.0-win-x64.msi"
expect_results \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "id-msi" "")" \
  "$(row dictum-1.0.0-win-x64.zip "$zip_sha" "" "the time limit came first")"
echo "PASS: no request starts after the time limit, and the files left say so"

files dictum-1.0.0-big-a.AppImage dictum-1.0.0-big-b.tar.gz dictum-1.0.0-win-x64.msi
run_upload \
  '200 {"data": "http://www.virustotal.com/_ah/upload/two/"}' '200 {"data": {"id": "id-a"}}' \
  '200 {"data": null}' \
  '200 {"data": {"type": "analysis"}}'
expect_log \
  "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/two/ dictum-1.0.0-big-a.AppImage" \
  "sleep 15" "GET $api/files/upload_url" \
  "sleep 15" "POST $api/files dictum-1.0.0-win-x64.msi"
[ "$(cut -f4 "$work/results")" = "
VirusTotal gave no upload URL
VirusTotal's answer had no analysis id" ] || fail "odd answers: $(cat "$work/results")"
echo "PASS: an http:// upload URL is used over https://, and an answer without a URL or an id fails that file only"

rm -rf "$STUB" && mkdir "$STUB" && : > "$STUB/answers"
if PATH="$work/bin:$PATH" VIRUSTOTAL_API_KEY="" bash "$script" upload "$work/files" "$work/results" > /dev/null 2>&1; then
  fail "the upload ran without a key"
fi
mkdir "$work/empty"
if PATH="$work/bin:$PATH" VIRUSTOTAL_API_KEY=test-key bash "$script" upload "$work/empty" "$work/results" > /dev/null 2>&1; then
  fail "the upload passed on a folder without files"
fi
[ ! -s "$STUB/log" ] || fail "requests were made without a key or files: $(cat "$STUB/log")"
echo "PASS: without a key or without files the upload fails before any request"

# The section.
section() { bash "$script" section "$work/section-results"; }
results() { printf '%s\n' "$@" > "$work/section-results"; }
link() { echo "- [\`$1\`](https://www.virustotal.com/gui/file/$2)"; }
a=$(printf 'a%.0s' {1..64})
b=$(printf 'b%.0s' {1..64})

results "$(row "dictum-1.0.0-win-x64.msi" "$a" "id-a" "")" "$(row "PKGBUILD" "$b" "" "VirusTotal did not answer")"
notes=$'Dictum 1.0.0.\n\n## License\n\nMIT.\n'
first=$(printf '%s' "$notes" | section) || fail "section failed on notes without a section"
case $first in
  $'Dictum 1.0.0.\n\n## License\n\nMIT.\n\n<!-- virustotal:start -->\n## VirusTotal\n'*$'\n<!-- virustotal:end -->') ;;
  *) fail "the section was not appended after a blank line:
$first" ;;
esac
grep -qxF -e "$(link dictum-1.0.0-win-x64.msi "$a")" <<< "$first" || fail "no link to the MSI's report: $first"
grep -qxF -e "- \`PKGBUILD\`: not sent yet (VirusTotal did not answer)" <<< "$first" || fail "no reason for PKGBUILD: $first"
grep -q 'antivirus engines' <<< "$first" || fail "the section does not say what a report is"
grep -q 'not signed' <<< "$first" || fail "the section does not say an unsigned app may be flagged"
echo "PASS: notes without a section get one at the end: a link per file sent, and why for a file not sent"

again=$(printf '%s\n' "$first" | section) || fail "section failed on its own output"
[ "$again" = "$first" ] || fail "a second run changed the notes:
$again"
results "$(row "dictum-1.0.0-win-x64.msi" "$a" "id-a" "")" "$(row "PKGBUILD" "$b" "id-b" "")"
second=$(printf '%s\n' "$first" | section) || fail "section failed on a second run with new results"
[ "$(grep -c 'virustotal:start' <<< "$second")" = 1 ] || fail "the section is there twice: $second"
grep -qxF -e "$(link PKGBUILD "$b")" <<< "$second" || fail "the new run's link is missing: $second"
if grep -q 'not sent yet' <<< "$second"; then fail "the old run's line is still there: $second"; fi
echo "PASS: a second run replaces the section rather than adding one"

middle=$'Intro.\n\n<!-- virustotal:start -->\nold\n<!-- virustotal:end -->\n\n## License\n\nMIT.'
out=$(printf '%s\n' "$middle" | section) || fail "section failed on a section in the middle"
case $out in
  $'Intro.\n\n<!-- virustotal:start -->\n## VirusTotal\n'*$'<!-- virustotal:end -->\n\n## License\n\nMIT.') ;;
  *) fail "a section in the middle did not stay there:
$out" ;;
esac
if grep -q '^old$' <<< "$out"; then fail "the old section's text is still there: $out"; fi
echo "PASS: the section is replaced where it is, and the notes after it stay"

crlf=$'Intro.\r\n\r\n<!-- virustotal:start -->\r\nold\r\n<!-- virustotal:end -->\r\n\r\n## License\r\n'
out=$(printf '%s' "$crlf" | section) || fail "section failed on CRLF notes"
[ "$(grep -c 'virustotal:start' <<< "$out")" = 1 ] || fail "CRLF notes have the section twice: $out"
if grep -q '^old' <<< "$out"; then fail "the old section in CRLF notes is still there: $out"; fi
case $out in $'Intro.\r\n\r\n<!-- virustotal:start -->\n'*$'<!-- virustotal:end -->\n\r\n## License') ;; *) fail "CRLF notes: $out" ;; esac
echo "PASS: notes saved with CRLF, as GitHub's editor does, have their section found and replaced"

twice=$'Intro.\n<!-- virustotal:start -->\none\n<!-- virustotal:end -->\nMiddle.\n<!-- virustotal:start -->\ntwo\n<!-- virustotal:end -->\nEnd.'
out=$(printf '%s\n' "$twice" | section) || fail "section failed on two sections"
[ "$(grep -c 'virustotal:start' <<< "$out")" = 1 ] || fail "two sections stayed two: $out"
case $out in $'Intro.\n<!-- virustotal:start -->\n'*$'<!-- virustotal:end -->\nMiddle.\nEnd.') ;; *) fail "two sections: $out" ;; esac
echo "PASS: a section pasted twice is written once, where the first was"

results "$(row "dictum-1.0.0-win-x64.msi" "$a" "" "VirusTotal's request quota was used up")"
out=$(printf '%s\n' "$first" | section 2> /dev/null) || fail "section failed when no file was sent"
[ "$out" = "$first" ] || fail "with no file sent, the section that was there changed:
$out"
out=$(printf '%s' "$notes" | section) || fail "section failed on new notes when no file was sent"
grep -qxF -e "- \`dictum-1.0.0-win-x64.msi\`: not sent yet (VirusTotal's request quota was used up)" <<< "$out" \
  || fail "new notes do not say why there are no reports: $out"
echo "PASS: with no file sent, a section that is there stays, and notes without one say why there are no reports"

out=$(printf '' | section) || fail "section failed on empty notes"
case $out in '<!-- virustotal:start -->'*'<!-- virustotal:end -->') ;; *) fail "empty notes: $out" ;; esac
echo "PASS: empty notes become the section alone"

for broken in $'A\n<!-- virustotal:start -->\nB' $'A\n<!-- virustotal:end -->\nB' \
  $'<!-- virustotal:start -->\n<!-- virustotal:start -->\n<!-- virustotal:end -->'; do
  if printf '%s\n' "$broken" | section > "$work/out" 2> /dev/null; then fail "broken markers were accepted: $broken"; fi
  [ ! -s "$work/out" ] || fail "broken markers printed notes: $(cat "$work/out")"
done
echo "PASS: notes with a marker missing or nested fail and print nothing, so they are not overwritten"

# The workflows: the key reaches a step only through env, never an if:, and release.yml starts the uploads only once
# the draft release exists.
workflows=("$root/.github/workflows/virustotal.yml" "$root/.github/workflows/release.yml")
uses=$(grep -h 'secrets\.VIRUSTOTAL_API_KEY' "${workflows[@]}" | sed 's/^ *//')
others=$(grep -v -x -F "VIRUSTOTAL_API_KEY: \${{ secrets.VIRUSTOTAL_API_KEY }}" <<< "$uses" || true)
[ -n "$uses" ] && [ -z "$others" ] || fail "the key is used other than as VIRUSTOTAL_API_KEY: in env: $others"
needs=$(awk '
  /^  virustotal:/ { inside = 1; next }
  inside && /^  [^ ]/ { exit }
  inside && /^    needs:/ { print; exit }
' "$root/.github/workflows/release.yml")
[ "$needs" = "    needs: release" ] || fail "release.yml's virustotal job does not wait for the release job: '$needs'"
echo "PASS: the key is passed only through env, and release.yml sends the files once the draft exists"

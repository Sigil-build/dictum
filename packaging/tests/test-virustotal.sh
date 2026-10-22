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
    -w | --max-time | --proto) shift ;;
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

# Before a package goes up, GET /files/<sha256> asks whether VirusTotal has it: 404 is a file it has never seen.
unknown='404 {"error": {"code": "NotFoundError"}}'
known='200 {"data": {"type": "file"}}'
lookup() { echo "GET $api/files/$(sha "$work/files/$1")"; }

files dictum-1.0.0-big-linux-x64.tar.gz dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip
big_sha=$(sha "$work/files/dictum-1.0.0-big-linux-x64.tar.gz")
msi_sha=$(sha "$work/files/dictum-1.0.0-win-x64.msi")
zip_sha=$(sha "$work/files/dictum-1.0.0-win-x64.zip")
run_upload \
  "$unknown" '200 {"data": "https://www.virustotal.com/_ah/upload/one/"}' \
  '200 {"data": {"type": "analysis", "id": "id-big"}}' \
  "$unknown" '200 {"data": {"type": "analysis", "id": "id-msi"}}' \
  "$unknown" '200 {"data": {"type": "analysis", "id": "id-zip"}}'
expect_log \
  "$(lookup dictum-1.0.0-big-linux-x64.tar.gz)" "sleep 15" \
  "GET $api/files/upload_url" "sleep 15" \
  "POST https://www.virustotal.com/_ah/upload/one/ dictum-1.0.0-big-linux-x64.tar.gz" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.zip)" "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.zip"
expect_results \
  "$(row "dictum-1.0.0-big-linux-x64.tar.gz" "$big_sha" "id-big" "")" \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "id-msi" "")" \
  "$(row "dictum-1.0.0-win-x64.zip" "$zip_sha" "id-zip" "")"
echo "PASS: a package VirusTotal does not know goes up: over 32 MB to an upload URL, the rest to /files, 15 s apart, the key in a header file"

run_upload "$known" "$unknown" '200 {"data": {"id": "id-msi"}}' "$known"
expect_log \
  "$(lookup dictum-1.0.0-big-linux-x64.tar.gz)" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.zip)"
expect_results \
  "$(row "dictum-1.0.0-big-linux-x64.tar.gz" "$big_sha" "known" "")" \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "id-msi" "")" \
  "$(row "dictum-1.0.0-win-x64.zip" "$zip_sha" "known" "")"
grep -q "dictum-1.0.0-win-x64.zip: VirusTotal has it already, report https://www.virustotal.com/gui/file/$zip_sha" "$work/out" \
  || fail "a known package's report is not in the log: $(cat "$work/out")"
echo "PASS: a package VirusTotal knows already is not sent again, which saves the quota"

files dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip
run_upload '503 {}' '503 {}' '503 {}' '503 {}' "$unknown" '200 {"data": {"id": "id-zip"}}'
expect_log \
  "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.zip)" "sleep 15" "POST $api/files dictum-1.0.0-win-x64.zip"
expect_results \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "" "VirusTotal answered HTTP 503")" \
  "$(row dictum-1.0.0-win-x64.zip "$zip_sha" "id-zip" "")"
echo "PASS: a lookup that keeps failing sends nothing for that package, and the next one goes on"

files dictum-1.0.0-win-x64.msi
run_upload "$unknown" '429 {"error": {"code": "QuotaExceededError"}}' '503 {}' none '200 {"data": {"id": "id-msi"}}'
expect_log \
  "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 60" \
  "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" \
  "POST $api/files dictum-1.0.0-win-x64.msi"
expect_results "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "id-msi" "")"
echo "PASS: a 429 is tried again after 60 s, a 5xx and no answer after 15 s"

# An upload URL takes one upload, which a try that failed may have used: the next try asks for a new one.
files dictum-1.0.0-big-linux-x64.tar.gz
run_upload "$unknown" \
  '200 {"data": "https://www.virustotal.com/_ah/upload/one/"}' none \
  '200 {"data": "https://www.virustotal.com/_ah/upload/two/"}' '503 {}' \
  '200 {"data": "https://www.virustotal.com/_ah/upload/three/"}' '429 {"error": {"code": "QuotaExceededError"}}' \
  '200 {"data": "https://www.virustotal.com/_ah/upload/four/"}' '200 {"data": {"id": "id-big"}}'
expect_log "$(lookup dictum-1.0.0-big-linux-x64.tar.gz)" "sleep 15" \
  "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/one/ dictum-1.0.0-big-linux-x64.tar.gz" \
  "sleep 15" "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/two/ dictum-1.0.0-big-linux-x64.tar.gz" \
  "sleep 15" "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/three/ dictum-1.0.0-big-linux-x64.tar.gz" \
  "sleep 60" "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/four/ dictum-1.0.0-big-linux-x64.tar.gz"
expect_results "$(row dictum-1.0.0-big-linux-x64.tar.gz "$big_sha" "id-big" "")"
VT_RETRIES=1 run_upload "$unknown" \
  '200 {"data": "https://www.virustotal.com/_ah/upload/one/"}' none \
  '200 {"data": "https://www.virustotal.com/_ah/upload/two/"}' '502 {}'
expect_log "$(lookup dictum-1.0.0-big-linux-x64.tar.gz)" "sleep 15" \
  "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/one/ dictum-1.0.0-big-linux-x64.tar.gz" \
  "sleep 15" "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/two/ dictum-1.0.0-big-linux-x64.tar.gz"
expect_results "$(row dictum-1.0.0-big-linux-x64.tar.gz "$big_sha" "" "VirusTotal answered HTTP 502")"
echo "PASS: an upload to an upload URL that fails is tried again with a new URL, within the same number of tries"

files dictum-1.0.0-linux-x64.AppImage dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip
appimage_sha=$(sha "$work/files/dictum-1.0.0-linux-x64.AppImage")
VT_RETRIES=1 run_upload '429 {"error": {"code": "QuotaExceededError"}}' '429 {"error": {"code": "QuotaExceededError"}}'
expect_log "$(lookup dictum-1.0.0-linux-x64.AppImage)" "sleep 60" "$(lookup dictum-1.0.0-linux-x64.AppImage)"
expect_results \
  "$(row dictum-1.0.0-linux-x64.AppImage "$appimage_sha" "" "VirusTotal's request quota was used up")" \
  "$(row dictum-1.0.0-win-x64.msi "$msi_sha" "" "VirusTotal's request quota was used up")" \
  "$(row dictum-1.0.0-win-x64.zip "$zip_sha" "" "VirusTotal's request quota was used up")"
grep -q "::warning::dictum-1.0.0-win-x64.zip was not sent to VirusTotal: VirusTotal's request quota was used up." "$work/out" \
  || fail "no warning for a package not sent: $(cat "$work/out")"
echo "PASS: a quota still used up after the retries ends the uploads, and every package left says why"

files dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip
run_upload '401 {"error": {"code": "WrongCredentialsError"}}'
expect_log "$(lookup dictum-1.0.0-win-x64.msi)"
[ "$(cut -f4 "$work/results" | sort -u)" = "VirusTotal refused the API key" ] || fail "a refused key: $(cat "$work/results")"
echo "PASS: a refused key is not tried again and ends the uploads"

run_upload "$unknown" '400 {"error": {"code": "BadRequestError"}}' "$unknown" '200 {"data": {"id": "id-zip"}}'
expect_log \
  "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" "POST $api/files dictum-1.0.0-win-x64.msi" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.zip)" "sleep 15" "POST $api/files dictum-1.0.0-win-x64.zip"
expect_results \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "" "VirusTotal answered HTTP 400")" \
  "$(row dictum-1.0.0-win-x64.zip "$zip_sha" "id-zip" "")"
echo "PASS: a file VirusTotal refuses does not stop the next one"

# The stand-in sleep takes no time, so every request after the first starts 15 s past a 10 s limit.
VT_DEADLINE=10 run_upload "$known"
expect_log "$(lookup dictum-1.0.0-win-x64.msi)"
expect_results \
  "$(row "dictum-1.0.0-win-x64.msi" "$msi_sha" "known" "")" \
  "$(row dictum-1.0.0-win-x64.zip "$zip_sha" "" "the time limit came first")"
echo "PASS: no request starts after the time limit, and the files left say so"

files dictum-1.0.0-big-a.AppImage dictum-1.0.0-big-b.tar.gz dictum-1.0.0-win-x64.msi
run_upload \
  "$unknown" '200 {"data": "http://www.virustotal.com/_ah/upload/two/"}' '200 {"data": {"id": "id-a"}}' \
  "$unknown" '200 {"data": null}' \
  "$unknown" '200 {"data": {"type": "analysis"}}'
expect_log \
  "$(lookup dictum-1.0.0-big-a.AppImage)" "sleep 15" \
  "GET $api/files/upload_url" "sleep 15" "POST https://www.virustotal.com/_ah/upload/two/ dictum-1.0.0-big-a.AppImage" "sleep 15" \
  "$(lookup dictum-1.0.0-big-b.tar.gz)" "sleep 15" "GET $api/files/upload_url" "sleep 15" \
  "$(lookup dictum-1.0.0-win-x64.msi)" "sleep 15" "POST $api/files dictum-1.0.0-win-x64.msi"
[ "$(cut -f4 "$work/results")" = "
VirusTotal gave no upload URL
VirusTotal's answer had no analysis id" ] || fail "odd answers: $(cat "$work/results")"
echo "PASS: an http:// upload URL is used over https://, and an answer without a URL or an id fails that file only"

rm -rf "$STUB" && mkdir "$STUB" && : > "$STUB/answers"
if PATH="$work/bin:$PATH" VIRUSTOTAL_API_KEY="" bash "$script" upload "$work/files" "$work/results" > /dev/null 2>&1; then
  fail "the upload ran without a key"
fi
mkdir "$work/texts"
echo sums > "$work/texts/SHA256SUMS"
echo build > "$work/texts/PKGBUILD"
if PATH="$work/bin:$PATH" VIRUSTOTAL_API_KEY=test-key bash "$script" upload "$work/texts" "$work/results" > /dev/null 2>&1; then
  fail "the upload passed on a folder without packages"
fi
[ ! -s "$STUB/log" ] || fail "requests were made without a key or packages: $(cat "$STUB/log")"
echo "PASS: without a key or without packages the upload fails before any request"

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
# The link needs only the SHA-256, so a package not sent has one too, with why it was not sent this time.
grep -qxF -e "$(link PKGBUILD "$b") (not sent in the last run: VirusTotal did not answer)" <<< "$first" \
  || fail "no link and reason for PKGBUILD: $first"
grep -q 'antivirus engines' <<< "$first" || fail "the section does not say what a report is"
grep -q 'not signed' <<< "$first" || fail "the section does not say an unsigned app may be flagged"
echo "PASS: notes without a section get one at the end: a link per file, and why for a file not sent"

again=$(printf '%s\n' "$first" | section) || fail "section failed on its own output"
[ "$again" = "$first" ] || fail "a second run changed the notes:
$again"
results "$(row "dictum-1.0.0-win-x64.msi" "$a" "id-a" "")" "$(row "PKGBUILD" "$b" "id-b" "")"
second=$(printf '%s\n' "$first" | section) || fail "section failed on a second run with new results"
[ "$(grep -c 'virustotal:start' <<< "$second")" = 1 ] || fail "the section is there twice: $second"
grep -qxF -e "$(link PKGBUILD "$b")" <<< "$second" || fail "the new run's link is missing: $second"
if grep -q 'not sent' <<< "$second"; then fail "the old run's line is still there: $second"; fi
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
grep -qxF -e "$(link dictum-1.0.0-win-x64.msi "$a") (not sent in the last run: VirusTotal's request quota was used up)" <<< "$out" \
  || fail "new notes do not say why the report may not be there: $out"
echo "PASS: with no file sent, a section that is there stays, and notes without one say why there are no reports"

results "$(row "dictum-1.0.0-win-x64.msi" "$a" "id-a" "")" "$(row "dictum-1.0.0-win-x64.zip" "$b" "" "the time limit came first")"
out=$(printf '%s\n' "$second" | section) || fail "section failed on a run that sent one package of two"
grep -qxF -e "$(link dictum-1.0.0-win-x64.msi "$a")" <<< "$out" || fail "no link for the package sent: $out"
grep -qxF -e "$(link dictum-1.0.0-win-x64.zip "$b") (not sent in the last run: the time limit came first)" <<< "$out" \
  || fail "the package not sent this time lost its link: $out"
echo "PASS: a run that sends some packages keeps the link of each one it did not send, and says why"

results "$(row "dictum-1.0.0-win-x64.msi" "$a" "known" "")" "$(row "dictum-1.0.0-win-x64.zip" "$b" "known" "")"
out=$(printf '%s\n' "$second" | section) || fail "section failed on packages VirusTotal knew"
[ "$(grep '^- ' <<< "$out")" = "$(link dictum-1.0.0-win-x64.msi "$a")
$(link dictum-1.0.0-win-x64.zip "$b")" ] || fail "packages VirusTotal knew are not linked as sent: $out"
echo "PASS: packages VirusTotal knew already are linked as if this run sent them"

out=$(printf '' | section) || fail "section failed on empty notes"
case $out in '<!-- virustotal:start -->'*'<!-- virustotal:end -->') ;; *) fail "empty notes: $out" ;; esac
echo "PASS: empty notes become the section alone"

# A whole release: the four packages go to VirusTotal; SHA256SUMS, the PKGBUILD and the SBOM are text, and stay out.
files dictum-1.0.0-linux-x64.AppImage dictum-1.0.0-linux-x64.tar.gz dictum-1.0.0-win-x64.msi dictum-1.0.0-win-x64.zip \
  PKGBUILD SHA256SUMS dictum-1.0.0-sbom.spdx.json
run_upload "$unknown" '200 {"data": {"id": "1"}}' "$unknown" '200 {"data": {"id": "2"}}' \
  "$unknown" '200 {"data": {"id": "3"}}' "$unknown" '200 {"data": {"id": "4"}}'
sent=$(awk '$1 == "POST" { print $3 }' "$STUB/log" | sort)
packages=$(printf '%s\n' dictum-1.0.0-linux-x64.AppImage dictum-1.0.0-linux-x64.tar.gz dictum-1.0.0-win-x64.msi \
  dictum-1.0.0-win-x64.zip | sort)
[ "$sent" = "$packages" ] || fail "a release sent other files than its four packages: $sent"
cp "$work/results" "$work/section-results"
out=$(printf 'Dictum 1.0.0.\n' | section) || fail "section failed on a whole release"
links=$(grep '^- ' <<< "$out")
[ "$(wc -l <<< "$links")" -eq 4 ] || fail "a release of seven files did not get four links: $out"
for package in $packages; do
  grep -qxF -e "$(link "$package" "$(sha "$work/files/$package")")" <<< "$links" || fail "no link for $package: $out"
done
echo "PASS: of a release's seven files, the four packages are sent and linked; SHA256SUMS, the PKGBUILD and the SBOM are not"

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

# Publishing a release, whose notes a person may have replaced, writes the section again.
vt=$root/.github/workflows/virustotal.yml
on_release=$(awk '
  /^on:/ { inside = 1; next }
  inside && /^[^ ]/ { exit }
  inside && /^  release:/ { found = 1; next }
  found && /^    types:/ { print; exit }
' "$vt")
[ "$on_release" = "    types: [published]" ] || fail "virustotal.yml does not run when a release is published: '$on_release'"
tag="\${{ inputs.tag || github.event.release.tag_name }}"
grep -qxF "      TAG: $tag" "$vt" || fail "virustotal.yml does not take the published release's tag"
grep -qxF "      group: virustotal-$tag" "$vt" || fail "virustotal.yml's runs for one release do not wait for each other"
if grep -q 'inputs\.tag }}' "$vt"; then fail "virustotal.yml still reads inputs.tag alone somewhere"; fi
echo "PASS: virustotal.yml runs again when a release is published, for that release's tag"

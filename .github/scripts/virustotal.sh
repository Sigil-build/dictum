#!/usr/bin/env bash
# Sends a release's packages to VirusTotal and writes links to their reports into the release notes.
# virustotal.yml runs it; packaging/tests/test-virustotal.sh tests it with stand-ins for curl and sleep.
#
# virustotal.sh upload <dir> <results>
#   Uploads the packages in <dir>, *.msi, *.zip, *.AppImage and *.tar.gz, with the API key in VIRUSTOTAL_API_KEY. The
#   other release files (SHA256SUMS, the PKGBUILD, the SBOM) are text a person can read, and would only spend quota.
#   A package VirusTotal knows already (GET /files/<sha256>) is not sent again, so a second run for a release costs a
#   request per package. Writes a line per package to <results>, in tabs: its name, its SHA-256, the id of
#   VirusTotal's analysis or "known", and why it was not sent when it was not. POST /files takes files up to 32 MB; a
#   larger one goes to the one-time URL that GET /files/upload_url gives, which is one request more. The free API
#   allows 4 requests a minute and 500 a day:
#   - a request starts VT_INTERVAL (15) seconds after the previous one ended;
#   - a 429 is tried again after VT_RETRY_WAIT (60) seconds, and no answer or a 5xx after VT_INTERVAL, up to
#     VT_RETRIES (3) times; a quota still used up after that, or a refused key, ends the uploads;
#   - an upload URL takes one upload, so each try of a large file asks for a new one;
#   - no request starts after VT_DEADLINE (1200) seconds, and none takes longer than 5 minutes.
#   It exits 0 once every package has its line, sent or not, and 1 only without packages or a key.
#
# virustotal.sh section <results> < notes > new-notes
#   Prints the notes with a "VirusTotal" section between <!-- virustotal:start --> and <!-- virustotal:end -->: a
#   link to https://www.virustotal.com/gui/file/<sha256> for each package, which needs only its SHA-256, and for one
#   this run did not send, why.
#   The section replaces the one an earlier run wrote, where it is, or else goes at the end. When nothing was sent
#   and the notes have a section already, they are printed unchanged: the reports it links are still there.
set -euo pipefail

start_marker='<!-- virustotal:start -->'
end_marker='<!-- virustotal:end -->'
api=https://www.virustotal.com/api/v3
direct_limit=32000000

die() { echo "virustotal.sh: $*" >&2; exit 1; }

# One try of a request to VirusTotal, once it is its turn: leaves the answer in $response and its HTTP status in $code
# (000 for no answer), and sets the pause before the next request. Returns 1, with code=deadline, when the time is up
# first.
attempt() {
  local max_time=$1
  shift
  if (( SECONDS + pause > deadline )); then
    code=deadline
    return 1
  fi
  if (( pause > 0 )); then sleep "$pause"; fi
  : > "$response"
  code=$(curl --proto '=https' --tlsv1.2 -sS --max-time "$max_time" -H "@$headers" -o "$response" -w '%{http_code}' "$@") || code=000
  pause=$interval
  [ "$code" != 429 ] || pause=$retry_wait
}

# Whether the last answer is worth another try: a 429, a 5xx or none.
again() {
  case $code in 429 | 000 | 5??) return 0 ;; *) return 1 ;; esac
}

# One request, tried again after a 429, a 5xx or no answer, up to VT_RETRIES times. Returns 1 when the time is up.
request() {
  local tries=0
  while attempt "$@"; do
    if ! again || (( tries++ >= retries )); then return 0; fi
    echo "  VirusTotal answered $code; trying again in ${pause}s." >&2
  done
  return 1
}

# Why the last request failed, in words for the release notes.
reason() {
  case $code in
    deadline) echo "the time limit came first" ;;
    429) echo "VirusTotal's request quota was used up" ;;
    401 | 403) echo "VirusTotal refused the API key" ;;
    000) echo "VirusTotal did not answer" ;;
    *) echo "VirusTotal answered HTTP $code" ;;
  esac
}

# Sends one file. Sets $id, or $why when the file was not sent.
send() {
  local path=$1 url tries=0
  if (( $(wc -c < "$path") <= direct_limit )); then
    if ! request 300 -F "file=@$path" "$api/files"; then
      why=$(reason)
      return
    fi
  else
    # Each upload URL takes one upload, and a try that failed may have used it: every try gets a new one.
    while :; do
      if ! request 60 "$api/files/upload_url" || [ "$code" != 200 ]; then
        why=$(reason)
        return
      fi
      url=$(jq -r '.data | strings' "$response" 2> /dev/null) || url=""
      # The key goes with the upload, so never over plain HTTP.
      url=${url/#http:\/\//https://}
      if [[ $url != https://* ]]; then
        why="VirusTotal gave no upload URL"
        return
      fi
      if ! attempt 300 -F "file=@$path" "$url"; then
        why=$(reason)
        return
      fi
      if ! again || (( tries++ >= retries )); then break; fi
      echo "  VirusTotal answered $code; trying again with a new upload URL in ${pause}s." >&2
    done
  fi
  if [ "$code" != 200 ]; then
    why=$(reason)
    return
  fi
  id=$(jq -r '.data.id | strings' "$response" 2> /dev/null) || id=""
  [ -n "$id" ] || why="VirusTotal's answer had no analysis id"
}

# Sends one package unless VirusTotal has it already: GET /files/<sha256> answers 200 for a file it knows and 404 for
# one it has never seen. Sets $id to the analysis id, or to "known" when nothing had to be sent, or else $why.
check() {
  local path=$1 sha=$2
  if ! request 60 "$api/files/$sha"; then
    why=$(reason)
    return
  fi
  case $code in
    200) id=known ;;
    404) send "$path" ;;
    *) why=$(reason) ;;
  esac
}

upload() {
  local dir=$1 results=$2 path name sha id why stop="" sent=0 known=0 total=0
  [ -d "$dir" ] || die "$dir is not a folder"
  [ -n "${VIRUSTOTAL_API_KEY:-}" ] || die "VIRUSTOTAL_API_KEY is empty"
  interval=${VT_INTERVAL:-15} retry_wait=${VT_RETRY_WAIT:-60} retries=${VT_RETRIES:-3} deadline=${VT_DEADLINE:-1200}
  pause=0

  work=$(mktemp -d)
  trap 'rm -rf "${work:?}"' EXIT
  headers=$work/headers response=$work/response
  # In a file, so that the key is not on curl's command line, where other processes could read it.
  (umask 077 && printf 'x-apikey: %s\n' "$VIRUSTOTAL_API_KEY" > "$headers")

  : > "$results"
  for path in "$dir"/*; do
    name=${path##*/}
    case $name in *.msi | *.zip | *.AppImage | *.tar.gz) ;; *) continue ;; esac
    [ -f "$path" ] || continue
    sha=$(sha256sum < "$path" | cut -d' ' -f1)
    total=$((total + 1))
    id="" why=$stop
    if [ -z "$stop" ]; then
      check "$path" "$sha"
      # The quota, the key and the time limit would fail every later package the same way.
      case $code in 429 | 401 | 403 | deadline) stop=$why ;; esac
    fi
    if [ "$id" = known ]; then
      known=$((known + 1))
      echo "$name: VirusTotal has it already, report https://www.virustotal.com/gui/file/$sha"
    elif [ -n "$id" ]; then
      sent=$((sent + 1))
      echo "$name: sent, analysis https://www.virustotal.com/gui/file-analysis/$id"
    else
      echo "::warning::$name was not sent to VirusTotal: $why."
    fi
    printf '%s\t%s\t%s\t%s\n' "$name" "$sha" "$id" "$why" >> "$results"
  done
  (( total > 0 )) || die "$dir has no packages (*.msi, *.zip, *.AppImage, *.tar.gz)"
  echo "Of $total packages, $sent sent to VirusTotal and $known it had already."
}

# The section, markers included, for the files in <results>.
render() {
  echo "$start_marker"
  echo "## VirusTotal"
  echo
  echo "[VirusTotal](https://www.virustotal.com/) scans a file with dozens of antivirus engines and publishes what each of them found. Each link below opens the report on one package of this release; a new report takes a few minutes to appear."
  echo
  echo "Dictum is a new app and its files are not signed yet. Some engines judge a file by its reputation rather than by what it does, so one or two of them may flag such a file while the rest find nothing."
  echo
  # The link needs only the SHA-256, so a package this run did not send keeps it: an earlier run may have sent it.
  # awk, as read would merge the two tabs around an empty analysis id: a tab is white space to IFS.
  awk -F'\t' '{
    printf "- [`%s`](https://www.virustotal.com/gui/file/%s)", $1, $2
    if ($3 == "") printf " (not sent in the last run: %s)", $4
    printf "\n"
  }' "$1"
  echo "$end_marker"
}

section() {
  local results=$1 block notes line bare out="" inside=0 placed=0
  [ -f "$results" ] || die "$results does not exist"
  block=$(render "$results")
  notes=$(cat)

  # Line by line, as GitHub's editor saves notes with CRLF: a marker is matched without its \r and spaces.
  while IFS= read -r line; do
    bare=${line%$'\r'}
    bare=${bare#"${bare%%[![:space:]]*}"}
    bare=${bare%"${bare##*[![:space:]]}"}
    if (( inside )); then
      [ "$bare" != "$start_marker" ] || die "the notes have a $start_marker inside the section"
      [ "$bare" != "$end_marker" ] || inside=0
      continue
    fi
    if [ "$bare" = "$start_marker" ]; then
      inside=1
      # A section pasted twice by hand is kept once.
      (( placed )) || out+=$block$'\n'
      placed=1
      continue
    fi
    [ "$bare" != "$end_marker" ] || die "the notes have a $end_marker without a $start_marker before it"
    out+=$line$'\n'
  done <<< "$notes"
  (( !inside )) || die "the notes have a $start_marker without a $end_marker after it"

  if (( placed )) && ! awk -F'\t' '$3 != "" { sent = 1 } END { exit !sent }' "$results"; then
    echo "virustotal.sh: no file was sent, so the notes keep the section they have." >&2
    printf '%s\n' "$notes"
    return
  fi
  # Trailing blank lines go, so that a second run prints what the first did.
  out=${out%"${out##*[![:space:]]}"}
  if (( !placed )); then
    out=${out:+$out$'\n\n'}$block
  fi
  printf '%s\n' "$out"
}

case ${1-} in
  upload) [ $# -eq 3 ] || die "usage: virustotal.sh upload <dir> <results>"; upload "$2" "$3" ;;
  section) [ $# -eq 2 ] || die "usage: virustotal.sh section <results> < notes > new-notes"; section "$2" ;;
  *) die "usage: virustotal.sh upload <dir> <results> | section <results>" ;;
esac

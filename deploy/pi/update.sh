#!/usr/bin/env bash
# Crystal Radio's updater: installs the newest CI build (the GitHub release "pi-latest") when it is
# newer than what runs here and nobody is listening. Run by crystal-radio-update.timer as the
# service's user; by hand:  bash ~/crystal-radio/deploy/update.sh
#
#   1. asks the running app what it is and whether it's in use (GET /api/status);
#   2. skips if the release's commit is the one running, or if audio is playing or a DJ session
#      is on;
#   3. downloads and checks the package, then asks again (a download takes a while);
#   4. installs it through install.sh, which keeps the old app as ~/crystal-radio.prev;
#   5. rolls back to ~/crystal-radio.prev if the new build doesn't come up, and remembers the bad
#      commit so it isn't retried every run.
#
# Needs no password: install.sh and the rollback only stop/start the service, which setup.sh's
# sudo rule allows. Output goes to the journal: journalctl -u crystal-radio-update
set -euo pipefail

repo=hansee99/crystal-radio
release="https://github.com/$repo/releases/download/pi-latest"
status_url=http://localhost:5000/api/status
unit=crystal-radio.service
app="$HOME/crystal-radio"
staging="$HOME/crystal-radio.new"
download="$HOME/crystal-radio.download"   # not /tmp: that's RAM on the Pi, and the package is big
state="$HOME/.local/state/crystal-radio-update"
failed_file="$state/failed-commit"

mkdir -p "$state"
# One run at a time, also against a second manual run.
exec 9>"$state/lock"
flock -n 9 || { echo "Another update is running."; exit 0; }

log() { echo "$*"; }
short() { [ -n "$1" ] && echo "${1:0:7}" || echo nothing; }

# The status JSON, or empty with the HTTP code in $status_code ("000" = not answering).
read_status() {
    local body
    body="$(curl -sS -m 10 -w '\n%{http_code}' "$status_url" 2>/dev/null || true)"
    status_code="${body##*$'\n'}"
    status_json="${body%$'\n'*}"
    [ "$status_code" = 200 ] || status_json=""
}

json_value() { sed -n "s/.*\"$1\": *\"\{0,1\}\([^\",}]*\).*/\1/p" <<<"$2" | head -n1 | sed "s/^null$//"; }   # null reads as empty

# Nobody listening? Sets $busy_reason when somebody is.
check_idle() {
    read_status
    busy_reason=""
    case "$status_code" in
        200)
            [ "$(json_value playing "$status_json")" = true ] && busy_reason="audio is playing"
            [ "$(json_value djRunning "$status_json")" = true ] && busy_reason="a DJ session is on"
            ;;
        000) ;;   # not running: nothing to interrupt, and a new build may be the fix
        404) busy_reason="the running build has no /api/status (deploy once with publish-pi.ps1)" ;;
        *)   busy_reason="/api/status answered HTTP $status_code" ;;
    esac
    [ -z "$busy_reason" ]
}

# The service answers /api/status within ~60 s, reporting commit $1 (any commit when $1 is empty).
wait_for_commit() {
    for _ in $(seq 1 60); do
        read_status
        [ "$status_code" = 200 ] && { [ -z "$1" ] || [ "$(json_value commit "$status_json")" = "$1" ]; } && return 0
        sleep 1
    done
    return 1
}

# --- What's there, what's running ------------------------------------------------------------

latest_info="$(curl -fsSL -m 30 "$release/build-info.json" 2>/dev/null || true)"
latest="$(json_value commit "$latest_info")"
if [ -z "$latest" ]; then
    # Also the normal answer for a few seconds while CI replaces the release.
    log "No pi-latest release to read right now; nothing to do."
    exit 0
fi

read_status
running="$(json_value commit "$status_json")"
if [ "$latest" = "$running" ]; then
    log "Up to date (${latest:0:7})."
    exit 0
fi
if [ -f "$failed_file" ] && [ "$(cat "$failed_file")" = "$latest" ]; then
    log "${latest:0:7} failed to start here before; waiting for a newer build."
    exit 0
fi

log "Release ${latest:0:7} ($(json_value version "$latest_info")), running $(short "$running")."
if ! check_idle; then
    log "Not updating: $busy_reason."
    exit 0
fi

# --- Download and check ----------------------------------------------------------------------

rm -rf "$download" "$staging"
mkdir -p "$download"
log "Downloading"
curl -fsSL -m 900 --retry 3 -o "$download/crystal-radio-pi.tar.gz" "$release/crystal-radio-pi.tar.gz"
curl -fsSL -m 30 --retry 3 -o "$download/crystal-radio-pi.tar.gz.sha256" "$release/crystal-radio-pi.tar.gz.sha256"
if ! (cd "$download" && sha256sum -c --status crystal-radio-pi.tar.gz.sha256); then
    # Most likely CI replaced the release mid-download; the next run gets a consistent pair.
    log "Checksum mismatch; not installing."
    rm -rf "$download"
    exit 1
fi
mkdir -p "$staging"
tar -xzf "$download/crystal-radio-pi.tar.gz" -C "$staging"
rm -rf "$download"

got="$(json_value commit "$(cat "$staging/pi/build-info.json" 2>/dev/null || true)")"
if [ "$got" != "$latest" ]; then
    log "The package is ${got:0:7}, not ${latest:0:7} (release replaced meanwhile); trying next run."
    rm -rf "$staging"
    exit 0
fi

if ! check_idle; then   # someone may have pressed play during the download
    log "Not updating: $busy_reason."
    rm -rf "$staging"
    exit 0
fi

# --- Install, or roll back -------------------------------------------------------------------

log "Installing ${latest:0:7}"
if bash "$staging/install.sh" && wait_for_commit "$latest"; then
    rm -f "$failed_file"
    log "Updated to ${latest:0:7}."
    exit 0
fi

rm -rf "$staging"
if [ "$(json_value commit "$(cat "$app/build-info.json" 2>/dev/null || true)")" != "$latest" ]; then
    # install.sh stopped before swapping the app (e.g. couldn't stop the service): the old one
    # is still in place, so there's nothing to roll back and the build isn't to blame.
    log "!! Install did not complete; the previous app is untouched."
    sudo -n systemctl start "$unit" || true
    exit 1
fi

log "!! ${latest:0:7} did not come up; rolling back."
echo "$latest" > "$failed_file"
if [ ! -d "$app.prev" ]; then
    log "!! No $app.prev to roll back to. The service needs a manual deploy."
    exit 1
fi
sudo -n systemctl stop "$unit" || true
rm -rf "$app.failed"
[ -d "$app" ] && mv "$app" "$app.failed"   # kept for a look; replaced by the next failure
mv "$app.prev" "$app"
sudo -n systemctl start "$unit" || true
if wait_for_commit "$running"; then
    log "Rolled back to ${running:0:7}. The failed build is in $app.failed."
else
    log "!! Rolled back, but the old build isn't answering either. See: journalctl -u $unit"
fi
exit 1

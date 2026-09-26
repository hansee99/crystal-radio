#!/usr/bin/env bash
# Installs or updates Crystal Radio's app files on the Pi, from a staging folder holding the
# published app in ./pi and the deploy files next to it. scripts/publish-pi.ps1 -Deploy uploads
# that folder and runs this; by hand:  bash ~/crystal-radio.new/install.sh
#
# Needs no password. Root-level setup (service, firewall, the sudo rule that lets this script
# restart the service) is setup.sh's job, run once by a person.
#
# App files are replaced wholesale. User data is not touched: settings, stations and history live
# in ~/.config/RadioPlayer, the catalogs and caches in ~/.local/share/RadioPlayer.
set -euo pipefail

staging="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
app="$HOME/crystal-radio"
unit=crystal-radio.service
installed="/etc/systemd/system/$unit"

if [ ! -f "$staging/pi/RadioPlayer.Web" ]; then
    echo "No published app in $staging/pi — run scripts/publish-pi.ps1 first." >&2
    exit 1
fi

first_install=true
[ -f "$installed" ] && first_install=false

if ! $first_install; then
    echo "==> Stopping the service"
    if ! sudo -n systemctl stop "$unit"; then
        echo "Can't stop $unit without a password — run setup.sh once (see deploy/pi/README.md)." >&2
        exit 1
    fi
fi

echo "==> Replacing $app"
rm -rf "$app"
mv "$staging/pi" "$app"
chmod +x "$app/RadioPlayer.Web"
mkdir -p "$app/deploy"
cp "$staging/setup.sh" "$staging/install.sh" "$staging/$unit" "$app/deploy/"
rm -rf "$staging"

if $first_install; then
    echo ""
    echo "App installed. One-time setup needs your sudo password — from Windows run:"
    echo "    ssh -t $(whoami)@$(hostname) sudo bash ~/crystal-radio/deploy/setup.sh"
    exit 10   # "installed, but not running yet" — publish-pi.ps1 tells this apart from failure
fi

if ! cmp -s "$app/deploy/$unit" "$installed"; then
    echo "!! $unit has changed; re-run setup.sh to install the new one:" >&2
    echo "   ssh -t $(whoami)@$(hostname) sudo bash ~/crystal-radio/deploy/setup.sh" >&2
fi

echo "==> Starting"
sudo -n systemctl start "$unit"

# The player builds its whole service graph before Kestrel listens; give it a moment.
for _ in $(seq 1 30); do
    if curl -fsS -o /dev/null "http://localhost:5000/" 2>/dev/null; then   # quiet: "not yet" is expected
        echo "==> Up: http://$(hostname):5000"
        exit 0
    fi
    sleep 1
done
echo "The service did not answer on :5000 within 30 s. Last log lines:" >&2
journalctl -u "$unit" -n 30 --no-pager >&2 || true   # hans is in group adm, which may read the journal
exit 1

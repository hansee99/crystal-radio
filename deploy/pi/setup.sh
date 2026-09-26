#!/usr/bin/env bash
# One-time setup for Crystal Radio on a Pi. Needs root, so a person runs it (sudo asks for the
# password) — from Windows:  ssh -t hans@ras4 sudo bash ~/crystal-radio/deploy/setup.sh
#
#   1. installs and enables the systemd service (re-run after crystal-radio.service changes);
#   2. lets the service's user stop/start/restart THIS service without a password, so updates
#      (install.sh, via scripts/publish-pi.ps1 -Deploy) run unattended. The unit file lives in
#      root-owned /etc, so this grants no way to change what the service runs as;
#   3. opens port 5000 in ufw, for the local network only.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "Run with sudo: sudo bash $0" >&2
    exit 1
fi
user="${SUDO_USER:-}"
if [ -z "$user" ] || [ "$user" = root ]; then
    echo "Run via sudo from the account that runs the service (e.g. hans), not as root directly." >&2
    exit 1
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
unit=crystal-radio.service
port=5000

echo "==> Installing $unit"
install -m 644 "$here/$unit" "/etc/systemd/system/$unit"
systemctl daemon-reload
systemctl enable "$unit"

echo "==> Allowing $user to stop/start/restart $unit without a password"
rule=/etc/sudoers.d/crystal-radio
tmp="$(mktemp)"
cat > "$tmp" <<EOF
# Written by Crystal Radio's deploy/pi/setup.sh — lets updates restart the service unattended.
$user ALL=(root) NOPASSWD: /usr/bin/systemctl stop $unit, /usr/bin/systemctl start $unit, /usr/bin/systemctl restart $unit
EOF
visudo -cf "$tmp" >/dev/null   # never install a sudoers file that doesn't parse
install -m 440 "$tmp" "$rule"
rm -f "$tmp"

if command -v ufw >/dev/null && ufw status | grep -q "^Status: active"; then
    # The subnet of the interface the default route uses, e.g. 192.168.0.0/24.
    iface="$(ip -4 route show default | awk '{print $5; exit}')"
    subnet="$(ip -4 route show dev "$iface" scope link | awk '{print $1; exit}')"
    if [ -n "$subnet" ]; then
        echo "==> ufw: allowing $subnet to port $port/tcp"
        ufw allow from "$subnet" to any port "$port" proto tcp comment 'Crystal Radio web UI'
    else
        echo "!! Couldn't work out the LAN subnet; open the port yourself:" >&2
        echo "   sudo ufw allow from <your-lan>/24 to any port $port proto tcp" >&2
    fi
else
    echo "==> ufw not active; no firewall rule needed"
fi

echo "==> Starting"
systemctl restart "$unit"
echo "Done. From the LAN: http://$(hostname):$port"

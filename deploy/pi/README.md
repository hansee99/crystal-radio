# Crystal Radio on a Raspberry Pi

The web head (`src/RadioPlayer.Web`) running as a service on a Pi: the Pi plays the radio out of
its own audio output, and any phone or computer on the home network controls it at
`http://<pi>:5000`. Browsers never receive audio.

## Requirements

- Raspberry Pi 4 or 5 with the **64-bit** Raspberry Pi OS (`uname -m` → `aarch64`). No .NET install
  needed; the build is self-contained.
- ALSA and ICU, present on a standard image (Debian 13 "trixie": `libasound2t64`, `libicu76`).
- Key-based SSH from the Windows machine, with the key loaded in the **Windows** ssh-agent
  (`ssh-add`). Git Bash's `ssh` can't use that agent; the scripts call Windows' `ssh.exe` directly.
- The service's user in the `audio` group (default for the first user on Raspberry Pi OS).

The scripts are written for user `hans` on host `ras4`. For another user, change `User=` and the two
`/home/...` paths in `crystal-radio.service`, and pass `-Target user@host` to the publish script.

## First install

From the repo root on Windows:

```powershell
.\scripts\publish-pi.ps1 -Deploy                               # test, publish, upload
ssh -t hans@ras4 sudo bash ~/crystal-radio/deploy/setup.sh     # once; asks for the sudo password
```

`setup.sh` (the only step that needs root):

1. installs and enables `crystal-radio.service`, so it starts at boot;
2. adds `/etc/sudoers.d/crystal-radio`, letting that user stop/start/restart **this service only**
   without a password, so updates run unattended. The unit file lives in root-owned `/etc`, so the
   rule can't be used to change what runs as root;
3. opens port 5000 in `ufw` for the local subnet only (skipped if ufw isn't active).

Then open `http://ras4:5000`, add the Anthropic API key under **Settings** (for AI search), and pick
the audio output if needed: `sudo raspi-config` → System Options → Audio.

## Updates

```powershell
.\scripts\publish-pi.ps1 -Deploy
```

No password needed. `install.sh` stops the service, replaces `~/crystal-radio`, starts it again and
waits for it to answer. If `crystal-radio.service` itself changed, it says so: re-run `setup.sh`.

## Where things live on the Pi

| Path | What |
| --- | --- |
| `~/crystal-radio/` | The app. Replaced on every update — never put data here. |
| `~/crystal-radio/deploy/` | `setup.sh`, `install.sh` and the unit file from the last deploy. |
| `~/.config/RadioPlayer/` | `settings.json` (API key, base64 in an owner-only file), `stations.json`, `history.json`. |
| `~/.local/share/RadioPlayer/` | The catalog and library databases, harvest cache, `logs/`. |

## Troubleshooting

```bash
systemctl status crystal-radio
journalctl -u crystal-radio -f                         # service output (hans can read it: group adm)
tail -f ~/.local/share/RadioPlayer/logs/app-*.log      # the app's own log
```

More in `doc/PI-PORT-PLAN.md`, "Troubleshooting on the Pi" — notably: distorted audio means the
device opened with more than two channels (`grep channels /proc/asound/card0/pcm0p/sub0/hw_params`
while playing must say `2`).

There is **no login** on the web UI. It is meant for a home network, like most network audio
players; don't forward port 5000 from the router.

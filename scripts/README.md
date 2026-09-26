# Build scripts

Run from anywhere; both find the repo root themselves.

## Ship a version

```powershell
.\scripts\build-installer.ps1
```

Runs the tests, publishes self-contained x64, builds the station catalog the installer ships, and
compiles `crystal-radio-setup-<version>.exe` into `build\dist\`.

The version comes from `<Version>` in `crystal-radio.csproj` — bump it there, nowhere else.

**Inno Setup is blacklisted here**, so the script borrows it: if it is missing it offers to install
it with winget and offers to remove it again when the build finishes. Both are asked. An Inno Setup
that was already installed is left alone.

Useful switches:

| | |
|---|---|
| `-Install` | Also update this machine's installed copy (see below) |
| `-SkipTests` | Skip the test run |
| `-FullSeed` | Describe the shipped catalog with the LLM instead of tags. Costs money, needs `ANTHROPIC_API_KEY` |
| `-SkipSeed` | Ship no catalog — new installs then start cold |
| `-InstallInnoSetup` / `-KeepInnoSetup` | Answer the two prompts up front, for unattended runs |

## Update this machine after a `git pull`

```powershell
git pull
.\scripts\build-installer.ps1 -Install
```

Builds, then runs the setup silently to upgrade in place. Prompts for elevation. The 86 MB language
model is not re-downloaded when it is already there, so this is fast.

Close the app first — a silent install cannot ask you to.

## Run a build without installing it

```powershell
.\scripts\build-release.ps1
```

Mirrors a Release build into `C:\Program Files\crystal-radio` and writes a Start Menu shortcut.
Needs an **elevated** terminal.

This is a plain folder copy with no uninstaller and no Add/Remove Programs entry — a developer's
own copy, deliberately separate from the setup-managed install at `C:\Program Files\Crystal Radio`.
**Do not point it at the setup-managed folder:** it mirrors, so it would delete the uninstaller.
Use `build-installer.ps1 -Install` to update that one.

## Deploy to the Raspberry Pi

```powershell
.\scripts\publish-pi.ps1 -Deploy                 # -Target user@host, default hans@ras4
```

Tests, publishes the web head self-contained for linux-arm64 into `build\pi`, uploads it and runs
`deploy\pi\install.sh` on the Pi, which swaps the app folder and restarts the service. Without
`-Deploy` it only publishes. The first install ends by printing a one-time `setup.sh` command that
needs the Pi's sudo password; see `deploy\pi\README.md`.

## Notes

- All scripts are ASCII-only with a BOM. PowerShell 5.1 reads a BOM-less file as ANSI, and one
  em-dash in a string is enough to break parsing — it has happened twice.
- `build\` is output only and is git-ignored.

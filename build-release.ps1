<#
.SYNOPSIS
    Publishes a Release build of Crystal Radio into a fixed folder under Program Files.

.DESCRIPTION
    One folder, no version number in its name, overwritten every time — so a shortcut to it keeps
    working and you always have exactly one "installed" build to test against while you keep
    working in the repo.

    Builds Release, so it does not disturb a Debug build running out of bin\Debug.

    Note: the installed build and a dev build share their user data — settings.json, library.db,
    and the harvest cache all live under %LocalAppData%\RadioPlayer. That is fine for running one
    at a time, which is the point of this script; running both AT ONCE means two processes writing
    one SQLite file and one harvest folder, which is asking for trouble.

.PARAMETER Destination
    Where to install. Defaults to "C:\Program Files\crystal-radio".

.PARAMETER SkipTests
    Publish without running the test suite first. The suite takes a few seconds; skipping it means
    you can install a build that doesn't pass.

.PARAMETER Force
    Stop a running instance out of the destination folder instead of refusing.

.EXAMPLE
    .\build-release.ps1
    Run from an elevated terminal. Tests, publishes, mirrors into Program Files.

.EXAMPLE
    .\build-release.ps1 -Destination C:\Temp\crystal-radio-test -SkipTests
    A throwaway install somewhere that needs no elevation.
#>
[CmdletBinding()]
param(
    [string] $Destination = "C:\Program Files\crystal-radio",
    [switch] $SkipTests,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$repo    = $PSScriptRoot
$project = Join-Path $repo 'crystal-radio.csproj'
$tests   = Join-Path $repo 'tests\RadioPlayer.Tests\RadioPlayer.Tests.csproj'
$staging = Join-Path $env:TEMP ('crystal-radio-publish-' + [Guid]::NewGuid().ToString('n').Substring(0, 8))

function Fail($message) {
    Write-Host ""
    Write-Host "  $message" -ForegroundColor Red
    Write-Host ""
    exit 1
}

function Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

if (-not (Test-Path $project)) { Fail "No crystal-radio.csproj next to this script ($repo)." }

# --- Can we actually write there? --------------------------------------------------------------
# Checked up front rather than after a 30-second publish: Program Files needs elevation, and
# finding that out at the copy step wastes the build.

$needsAdmin = $Destination -like "$env:ProgramFiles*" -or $Destination -like "${env:ProgramFiles(x86)}*"
if ($needsAdmin) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin  = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Fail ("Writing to `"$Destination`" needs an elevated terminal. Run this from " +
              "'Terminal (Admin)', or pass -Destination somewhere writable.")
    }
}

# --- Is the installed build running? -----------------------------------------------------------
# Its files would be locked, and robocopy would half-mirror before failing.

$running = @(Get-Process -Name 'crystal-radio' -ErrorAction SilentlyContinue | Where-Object {
    $path = $null
    try { $path = $_.Path } catch { }        # another user's process — not ours to worry about
    $path -and $path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase)
})

if ($running.Count -gt 0) {
    if (-not $Force) {
        Fail ("Crystal Radio is running from `"$Destination`" (PID " +
              (($running | ForEach-Object { $_.Id }) -join ', ') +
              "). Close it, or re-run with -Force to stop it.")
    }
    Step "Stopping the running instance"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500          # let the handles actually drop before we mirror
}

# --- Tests ------------------------------------------------------------------------------------

if (-not $SkipTests) {
    Step "Running tests"
    & dotnet test $tests -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { Fail "Tests failed — nothing installed." }
} else {
    Write-Host ""
    Write-Host "  Skipping tests (-SkipTests)." -ForegroundColor Yellow
}

# --- Publish to staging ------------------------------------------------------------------------
# Into a temp folder first, so a failed build leaves the installed version untouched.

Step "Publishing Release"
& dotnet publish $project -c Release -o $staging --nologo
if ($LASTEXITCODE -ne 0) { Fail "Publish failed — nothing installed." }

$exe = Join-Path $staging 'crystal-radio.exe'
if (-not (Test-Path $exe)) { Fail "Publish produced no crystal-radio.exe. Nothing installed." }
# ProductVersion carries the commit SHA as build metadata ("1.8.2+a4e86a7..."); the number is
# what anyone reading this line wants.
$version = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]

# --- Mirror into place -------------------------------------------------------------------------
# /MIR so files dropped between versions don't linger. Robocopy's exit codes are a bit field;
# anything under 8 means it did its job (1 = copied, 2 = extras removed, 3 = both).

Step "Installing to $Destination"
& robocopy $staging $Destination /MIR /NJH /NJS /NP /NDL /R:2 /W:1 | Out-Null
$robocopy = $LASTEXITCODE
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

if ($robocopy -ge 8) { Fail "robocopy failed (exit $robocopy) — the installed build may be incomplete." }

$files = @(Get-ChildItem $Destination -Recurse -File).Count
Write-Host ""
Write-Host "  Crystal Radio $version installed" -ForegroundColor Green
Write-Host "  $Destination  ($files files)"
Write-Host "  $(Join-Path $Destination 'crystal-radio.exe')"
Write-Host ""

# robocopy's success codes are non-zero (1 = files copied), and a script's exit code defaults to
# the last native command's. Without this, every successful install reports failure to a caller.
exit 0

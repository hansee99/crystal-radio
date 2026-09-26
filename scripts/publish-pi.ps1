<#
.SYNOPSIS
    Publishes Crystal Radio's web head for a Raspberry Pi, and optionally deploys it.

.DESCRIPTION
    Builds src\RadioPlayer.Web self-contained for linux-arm64 into build\pi (no .NET needed on the
    Pi). With -Deploy it also uploads the build to the Pi and runs deploy\pi\install.sh there,
    which replaces ~/crystal-radio and restarts the service. User data on the Pi
    (~/.config/RadioPlayer, ~/.local/share/RadioPlayer) is never touched.

    Needs ssh key login to the Pi through the WINDOWS ssh-agent (Git Bash's ssh can't reach it).
    The very first deploy also needs deploy\pi\setup.sh run once by hand, with the sudo password;
    install.sh prints the command. See deploy\pi\README.md.

.PARAMETER Target
    user@host of the Pi. Defaults to hans@ras4.

.PARAMETER Deploy
    Upload and install after publishing. Without it, only build\pi is produced.

.PARAMETER SkipTests
    Publish without running the test suite first.

.EXAMPLE
    .\scripts\publish-pi.ps1 -Deploy
    Test, publish, upload to hans@ras4 and restart the service.
#>
[CmdletBinding()]
param(
    [string] $Target = 'hans@ras4',
    [switch] $Deploy,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\RadioPlayer.Web\RadioPlayer.Web.csproj'
# Both test projects: Core's (portable, the bulk) and the WPF head's (Windows-only UI tests).
$tests   = @((Join-Path $repo 'tests\RadioPlayer.Core.Tests\RadioPlayer.Core.Tests.csproj'),
             (Join-Path $repo 'tests\RadioPlayer.Tests\RadioPlayer.Tests.csproj'))
$out     = Join-Path $repo 'build\pi'
$deployDir = Join-Path $repo 'deploy\pi'
$model   = Join-Path $repo 'MlAssets\all-MiniLM-L6-v2.onnx'

# Windows' own tools, by full path: Git's ssh can't use the Windows ssh-agent, and Git's tar
# would be found first on some PATHs.
$ssh = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
$tar = Join-Path $env:WINDIR 'System32\tar.exe'

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

if (-not (Test-Path $project)) { Fail "No web project at $project." }

# The model is git-ignored and ~90 MB; without it the app runs but local semantic search is off.
if (-not (Test-Path $model)) {
    Write-Warning "MlAssets\all-MiniLM-L6-v2.onnx is missing - the Pi build will run without local semantic search. See MlAssets\README."
}

if ($Deploy) {
    Step "Checking $Target"
    foreach ($tool in $ssh, $tar) { if (-not (Test-Path $tool)) { Fail "Missing $tool." } }
    # A few tries: "ras4" resolves only through mDNS here (not the router's DNS), and a missed
    # multicast reply on Wi-Fi shows up as a transient "No such host is known".
    for ($try = 1; $try -le 4; $try++) {
        & $ssh -o BatchMode=yes -o ConnectTimeout=8 $Target 'true' 2>$null
        if ($LASTEXITCODE -eq 0) { break }
        if ($try -lt 4) { Write-Host "    not reachable yet, retrying..."; Start-Sleep -Seconds 3 }
    }
    if ($LASTEXITCODE -ne 0) {
        Fail "Can't log in to $Target over ssh. Check the key is loaded in the Windows ssh-agent (ssh-add)."
    }
}

if (-not $SkipTests) {
    Step "Running tests"
    foreach ($testProject in $tests) {
        & dotnet test $testProject -c Release --nologo
        if ($LASTEXITCODE -ne 0) { Fail "Tests failed - nothing published." }
    }
}

Step "Publishing linux-arm64 -> $out"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& dotnet publish $project -c Release -r linux-arm64 --self-contained -o $out --nologo
if ($LASTEXITCODE -ne 0) { Fail "Publish failed." }

if (-not $Deploy) {
    Write-Host ""
    Write-Host "Published to $out. Run again with -Deploy to install it on $Target." -ForegroundColor Green
    exit 0
}

Step "Uploading to $Target and installing"
# One tar stream holds the app (as pi/) and the deploy files; install.sh does the rest on the Pi.
# Piped through cmd.exe on purpose: Windows PowerShell re-encodes data piped between two native
# programs, which corrupts a binary stream.
$build = Split-Path $out -Parent
$remote = 'rm -rf ~/crystal-radio.new && mkdir ~/crystal-radio.new && tar -xf - -C ~/crystal-radio.new && bash ~/crystal-radio.new/install.sh'
# 'Continue' for this call only: Windows PowerShell turns every stderr line of a native program into
# a terminating error under 'Stop', which would abort mid-install. The exit code is the verdict.
$ErrorActionPreference = 'Continue'
# The deploy files by pattern, so a new script or unit travels without editing this list.
$deployFiles = (Get-ChildItem $deployDir -File | Where-Object { $_.Extension -in '.sh', '.service', '.timer' } |
    ForEach-Object { $_.Name }) -join ' '
& cmd.exe /c "`"$tar`" -cf - -C `"$build`" pi -C `"$deployDir`" $deployFiles | `"$ssh`" -o BatchMode=yes $Target `"$remote`" 2>&1"
$ErrorActionPreference = 'Stop'
$hostName = ($Target -split '@')[-1]
if ($LASTEXITCODE -eq 10) {
    # install.sh's "first install": app files are in place, the service isn't set up yet.
    Write-Host ""
    Write-Host "Installed, not running yet. Run the one-time setup (asks for the sudo password):" -ForegroundColor Yellow
    Write-Host "    ssh -t $Target sudo bash ~/crystal-radio/deploy/setup.sh"
    exit 0
}
if ($LASTEXITCODE -ne 0) { Fail "Install on $Target failed (see the output above)." }
Write-Host ""
Write-Host "Done: http://${hostName}:5000" -ForegroundColor Green

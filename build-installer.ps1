<#
.SYNOPSIS
    Builds crystal-radio-setup-<version>.exe (#43).

.DESCRIPTION
    Publishes a self-contained x64 Release build, generates the station catalog the installer
    ships, and compiles installer\crystal-radio.iss with Inno Setup.

    Self-contained on purpose: the alternative is detecting the .NET Desktop Runtime and
    bootstrapping Microsoft's installer, which is one more thing that can fail on a machine you
    cannot see. The payload already carries an 86 MB model download, so the runtime is not what
    makes this big.

    Unlike build-release.ps1 (which mirrors a build into Program Files on THIS machine and needs
    elevation), this produces a redistributable and needs no special privileges.

.PARAMETER SkipTests
    Skip the test run. The default is to run it — shipping an installer off a red build is worse
    than waiting twenty seconds.

.PARAMETER SeedCount
    How many stations to put in the shipped catalog. These are the most-clicked stations on Radio
    Browser, so they are what a new user is most likely to search for.

.PARAMETER FullSeed
    Describe the seeded stations with the LLM instead of building descriptions from tags. Needs
    ANTHROPIC_API_KEY and costs real money, but produces a far better catalog: tags-only rows are
    thin, and they stay that way for the 30-day staleness window before anything re-enriches them.

.PARAMETER SkipSeed
    Ship no catalog. The app then starts cold, which is exactly the situation #40 exists to avoid.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -FullSeed -SeedCount 800
#>
[CmdletBinding()]
param(
    [switch] $SkipTests,
    [int]    $SeedCount = 500,
    [switch] $FullSeed,
    [switch] $SkipSeed
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo     = $PSScriptRoot
$project  = Join-Path $repo 'crystal-radio.csproj'
$script   = Join-Path $repo 'installer\crystal-radio.iss'
$build    = Join-Path $repo 'build'
$staging  = Join-Path $build 'publish'
$dist     = Join-Path $build 'dist'

function Fail($message) {
    Write-Host ''
    Write-Host "  $message" -ForegroundColor Red
    Write-Host ''
    exit 1
}

function Step($message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

if (-not (Test-Path $project)) { Fail "No crystal-radio.csproj next to this script ($repo)." }
if (-not (Test-Path $script))  { Fail "No installer script at $script." }

# --- The Inno Setup compiler -------------------------------------------------------------------
# winget installs it per-user by default and per-machine with --scope machine, so look in both
# rather than assuming one.
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Fail ("Inno Setup 6 not found. Install it with:`n`n" +
          "    winget install --id JRSoftware.InnoSetup`n`n" +
          "Looked in:`n  " + ($isccCandidates -join "`n  "))
}

# --- Version, from the csproj so there is one source of truth ----------------------------------
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Fail "No <Version> in crystal-radio.csproj." }
Write-Host ''
Write-Host "  Crystal Radio $version" -ForegroundColor Green
Write-Host "  Inno Setup: $iscc" -ForegroundColor DarkGray

# --- Tests ---------------------------------------------------------------------------------------
if (-not $SkipTests) {
    Step 'Running tests'
    & dotnet test (Join-Path $repo 'tests\RadioPlayer.Tests\RadioPlayer.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed — not building an installer from this.' }
}

# --- Publish --------------------------------------------------------------------------------------
Step 'Publishing (self-contained, x64)'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging, $dist | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging --nologo
if ($LASTEXITCODE -ne 0) { Fail 'Publish failed.' }
if (-not (Test-Path (Join-Path $staging 'crystal-radio.exe'))) { Fail 'Publish produced no crystal-radio.exe.' }

# --- The catalog the installer ships ---------------------------------------------------------------
# Built fresh rather than committed: it is derived data, it would be a 7 MB binary in git, and a
# developer's own catalog reflects their listening rather than a sensible starting point.
$seedDir = Join-Path $staging 'seed'
if ($SkipSeed) {
    Write-Host '  Skipping the station catalog (-SkipSeed) — new installs will start cold.' -ForegroundColor Yellow
} else {
    Step "Building the shipped station catalog ($SeedCount stations)"
    New-Item -ItemType Directory -Force -Path $seedDir | Out-Null
    $seedDb = Join-Path $seedDir 'enrichment.db'
    if (Test-Path $seedDb) { Remove-Item $seedDb -Force }

    $seedArgs = @('--count', $SeedCount, '--db', $seedDb)
    if (-not $FullSeed) { $seedArgs += '--tags-only' }
    elseif (-not $env:ANTHROPIC_API_KEY) { Fail '-FullSeed needs ANTHROPIC_API_KEY in the environment.' }

    & dotnet run --project (Join-Path $repo 'tools\SeedEnrichment') -c Release -- @seedArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $seedDb)) {
        Fail 'Seeding failed. Re-run with -SkipSeed to build an installer without a catalog.'
    }
    # WAL sidecars would ship a half-written database; checkpoint by copying the main file only.
    Get-ChildItem $seedDir -Filter 'enrichment.db-*' -ErrorAction SilentlyContinue | Remove-Item -Force
    $seedMb = [math]::Round((Get-Item $seedDb).Length / 1MB, 1)
    Write-Host "  Catalog: $seedMb MB" -ForegroundColor DarkGray
}

# --- Compile the installer --------------------------------------------------------------------
Step 'Compiling the installer'
& $iscc "/DStagingDir=$staging" "/DAppVersion=$version" "/O$dist" $script
if ($LASTEXITCODE -ne 0) { Fail "Inno Setup failed (exit $LASTEXITCODE)." }

$setup = Get-ChildItem $dist -Filter "crystal-radio-setup-$version.exe" | Select-Object -First 1
if (-not $setup) { Fail "Inno Setup reported success but produced no installer in $dist." }

Write-Host ''
Write-Host "  Done: $($setup.FullName)" -ForegroundColor Green
Write-Host ("  {0} MB — the 86 MB model is downloaded during install, not carried." -f [math]::Round($setup.Length / 1MB, 1)) -ForegroundColor DarkGray
Write-Host ''

exit 0

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

    INNO SETUP IS BLACKLISTED in this organization, so the script treats it as something to borrow
    rather than something to have installed: if it is missing, it offers to install it with winget,
    and if it installed it, it offers to remove it again when the build is done. Both are asked,
    never assumed, and the removal is offered even when the build fails - a failed build is no
    reason to leave blacklisted software behind. Inno Setup that was ALREADY present is left
    strictly alone; it belongs to whoever put it there.

    ASCII only, deliberately. PowerShell 5.1 reads a BOM-less file as ANSI, and a single em-dash in
    a string is enough to break parsing - which it has done twice here, once because a later edit
    rewrote the file without its BOM. Keeping the text ASCII means the encoding cannot matter.

.PARAMETER SkipTests
    Skip the test run. The default is to run it - shipping an installer off a red build is worse
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

.PARAMETER InstallInnoSetup
    Answer yes to the "install Inno Setup?" prompt. For unattended runs; without it the script asks.

.PARAMETER KeepInnoSetup
    Answer no to the "remove Inno Setup again?" prompt, leaving an install this script made. Only
    meaningful when the script installed it.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -FullSeed -SeedCount 800
    .\build-installer.ps1 -InstallInnoSetup          # unattended: borrow it, then put it back
#>
[CmdletBinding()]
param(
    [switch] $SkipTests,
    [int]    $SeedCount = 500,
    [switch] $FullSeed,
    [switch] $SkipSeed,
    [switch] $InstallInnoSetup,
    [switch] $KeepInnoSetup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo     = $PSScriptRoot
$project  = Join-Path $repo 'crystal-radio.csproj'
$script   = Join-Path $repo 'installer\crystal-radio.iss'
$build    = Join-Path $repo 'build'
$staging  = Join-Path $build 'publish'
$dist     = Join-Path $build 'dist'

$WingetId = 'JRSoftware.InnoSetup'

# Thrown rather than exited, so the cleanup in the finally below still runs on a failure.
function Fail($message) {
    throw [System.InvalidOperationException]::new($message)
}

function Step($message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

<#
    Yes/no on the console, defaulting to yes on a bare Enter.

    A host that cannot prompt (a pipeline, a CI agent) answers NO rather than throwing: the two
    callers both have a safe no - fail with instructions, or leave the install in place and say so.
    That is why -InstallInnoSetup and -KeepInnoSetup exist; an unattended run states its intent up
    front instead of being guessed at.
#>
function Confirm-Yes([string] $question) {
    Write-Host ''
    try {
        $answer = Read-Host "  $question [Y/n]"
    }
    catch {
        Write-Host '  (no console to ask on - taking that as no)' -ForegroundColor DarkGray
        return $false
    }
    if ([string]::IsNullOrWhiteSpace($answer)) { return $true }
    return $answer.Trim() -match '^(y|yes)$'
}

# winget installs it per-user by default and per-machine with --scope machine, so look in both
# rather than assuming one. One list, used by both the search and the "looked in" message - two
# copies of it would drift the moment a path changed.
function Get-InnoSetupSearchPaths {
    return @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
}

function Find-InnoSetup {
    return Get-InnoSetupSearchPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Install-InnoSetup {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail ("Inno Setup 6 is not installed and winget is not available to install it.`n`n" +
              "Install it manually, or from another machine:`n" +
              "    winget install --id $WingetId")
    }

    Step 'Installing Inno Setup (temporarily)'
    & winget install --id $WingetId --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -ne 0) { Fail "winget install $WingetId failed (exit $LASTEXITCODE)." }
}

function Uninstall-InnoSetup {
    Step 'Removing Inno Setup again'
    & winget uninstall --id $WingetId --silent
    if ($LASTEXITCODE -ne 0) {
        # Not fatal: the installer has already been built, and the caller can finish the job by
        # hand. Saying so beats failing a successful build over a cleanup step.
        Write-Host "  winget uninstall returned $LASTEXITCODE - remove it manually with:" -ForegroundColor Yellow
        Write-Host "    winget uninstall --id $WingetId" -ForegroundColor Yellow
        return
    }
    Write-Host '  Removed.' -ForegroundColor DarkGray
}

try {
    if (-not (Test-Path $project)) { Fail "No crystal-radio.csproj next to this script ($repo)." }
    if (-not (Test-Path $script))  { Fail "No installer script at $script." }

    # --- The Inno Setup compiler ---------------------------------------------------------------
    $iscc = Find-InnoSetup
    $innoInstalledByUs = $false

    if (-not $iscc) {
        Write-Host ''
        Write-Host '  Inno Setup 6 is not installed. It is needed only to compile the installer,' -ForegroundColor Yellow
        Write-Host '  and this script can remove it again afterwards.' -ForegroundColor Yellow
        Write-Host '  Looked in:' -ForegroundColor DarkGray
        Get-InnoSetupSearchPaths | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

        if (-not ($InstallInnoSetup -or (Confirm-Yes "Install Inno Setup now with winget ($WingetId)?"))) {
            Fail ("Inno Setup 6 is required to compile the installer.`n`n" +
                  "Install it with:`n    winget install --id $WingetId")
        }

        Install-InnoSetup
        $iscc = Find-InnoSetup
        if (-not $iscc) {
            Fail ("winget reported success but ISCC.exe still isn't where it was expected.`n" +
                  "Looked in:`n  " + ((Get-InnoSetupSearchPaths) -join "`n  "))
        }
        # Only now: this is what makes the cleanup safe. Anything found on the first look belongs
        # to whoever installed it and is never touched.
        $innoInstalledByUs = $true
    }

    # --- Version, from the csproj so there is one source of truth ------------------------------
    $version = ([xml](Get-Content $project)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { Fail "No <Version> in crystal-radio.csproj." }
    Write-Host ''
    Write-Host "  Crystal Radio $version" -ForegroundColor Green
    Write-Host "  Inno Setup: $iscc" -ForegroundColor DarkGray
    if ($innoInstalledByUs) {
        Write-Host '  (installed by this script; you will be asked to remove it at the end)' -ForegroundColor DarkGray
    }

    try {
        # --- Tests -----------------------------------------------------------------------------
        if (-not $SkipTests) {
            Step 'Running tests'
            & dotnet test (Join-Path $repo 'tests\RadioPlayer.Tests\RadioPlayer.Tests.csproj') -c Release --nologo
            if ($LASTEXITCODE -ne 0) { Fail 'Tests failed - not building an installer from this.' }
        }

        # --- Publish ---------------------------------------------------------------------------
        Step 'Publishing (self-contained, x64)'
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $staging, $dist | Out-Null

        # ReadyToRun: precompiled to native, so a cold first run isn't spent JIT-compiling WPF and
        # the app on the target's CPU. That is most of the minute #53 reported on an older laptop -
        # the rest is reading the 86 MB model off its disk, which no build flag can help with.
        # Costs payload size, which matters less here than anywhere: the installer already
        # downloads a model twice its size.
        & dotnet publish $project -c Release -r win-x64 --self-contained true `
            -p:PublishReadyToRun=true -o $staging --nologo
        if ($LASTEXITCODE -ne 0) { Fail 'Publish failed.' }
        if (-not (Test-Path (Join-Path $staging 'crystal-radio.exe'))) {
            Fail 'Publish produced no crystal-radio.exe.'
        }

        # --- The catalog the installer ships ---------------------------------------------------
        # Built fresh rather than committed: it is derived data, it would be a 7 MB binary in git,
        # and a developer's own catalog reflects their listening rather than a sensible start.
        $seedDir = Join-Path $staging 'seed'
        if ($SkipSeed) {
            Write-Host '  Skipping the station catalog (-SkipSeed) - new installs will start cold.' -ForegroundColor Yellow
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
            # WAL sidecars would ship a half-written database; keep the main file only.
            Get-ChildItem $seedDir -Filter 'enrichment.db-*' -ErrorAction SilentlyContinue | Remove-Item -Force
            $seedMb = [math]::Round((Get-Item $seedDb).Length / 1MB, 1)
            Write-Host "  Catalog: $seedMb MB" -ForegroundColor DarkGray
        }

        # --- Compile the installer -------------------------------------------------------------
        Step 'Compiling the installer'
        & $iscc "/DStagingDir=$staging" "/DAppVersion=$version" "/O$dist" $script
        if ($LASTEXITCODE -ne 0) { Fail "Inno Setup failed (exit $LASTEXITCODE)." }

        $setup = Get-ChildItem $dist -Filter "crystal-radio-setup-$version.exe" | Select-Object -First 1
        if (-not $setup) { Fail "Inno Setup reported success but produced no installer in $dist." }

        Write-Host ''
        Write-Host "  Done: $($setup.FullName)" -ForegroundColor Green
        Write-Host ("  {0} MB - the 86 MB model is downloaded during install, not carried." -f `
            [math]::Round($setup.Length / 1MB, 1)) -ForegroundColor DarkGray
        Write-Host ''
    }
    finally {
        # finally, not "after a successful build": a build that failed is no reason to leave
        # blacklisted software installed.
        if ($innoInstalledByUs) {
            if ($KeepInnoSetup) {
                Write-Host ''
                Write-Host '  Leaving Inno Setup installed (-KeepInnoSetup). Remove it with:' -ForegroundColor Yellow
                Write-Host "    winget uninstall --id $WingetId" -ForegroundColor Yellow
            }
            elseif (Confirm-Yes 'Remove Inno Setup again now?') {
                Uninstall-InnoSetup
            }
            else {
                Write-Host ''
                Write-Host '  Left installed. Remove it later with:' -ForegroundColor Yellow
                Write-Host "    winget uninstall --id $WingetId" -ForegroundColor Yellow
            }
        }
    }

    exit 0
}
catch {
    Write-Host ''
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    exit 1
}

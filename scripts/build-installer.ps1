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

.PARAMETER Install
    Run the installer it just built, silently, to update this machine's installed copy. This is the
    git-pull-then-update loop: build, then let Inno upgrade in place.

    Deliberately the INSTALLER and not a file copy. build-release.ps1 mirrors a build into its own
    folder with robocopy /MIR, which is right for what that script is, but pointed at a
    setup-managed install it would delete unins000.exe and the uninstall log along with anything
    else it did not produce. Even without that, Windows would still believe the old version is
    installed: Add/Remove Programs and the uninstaller's file list are Inno's to maintain. Running
    the installer keeps all of it correct - same AppId means an in-place upgrade, the registered
    version moves, the uninstaller keeps working, and the Start Menu shortcut keeps its
    AppUserModelID.

    Needs elevation, since the installer writes to Program Files; UAC prompts if this shell is not
    already elevated. The 86 MB model is not re-downloaded when it is already there at full size.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -FullSeed -SeedCount 800
    .\build-installer.ps1 -InstallInnoSetup          # unattended: borrow it, then put it back
    git pull; .\build-installer.ps1 -Install         # update this machine to the new build
#>
[CmdletBinding()]
param(
    [switch] $SkipTests,
    [int]    $SeedCount = 500,
    [switch] $FullSeed,
    [switch] $SkipSeed,
    [switch] $InstallInnoSetup,
    [switch] $KeepInnoSetup,
    [switch] $Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo     = Split-Path $PSScriptRoot -Parent
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

<#
    Upgrades this machine's installed copy by running the setup we just built.

    /VERYSILENT with no /DIR: Inno finds the existing install from the AppId and upgrades it where
    it already is, rather than second-guessing the location. A first-time install lands in the
    default Program Files folder.

    Elevation is the installer's own business (PrivilegesRequired=admin): started with -Verb RunAs
    it raises UAC when this shell is not elevated, and simply proceeds when it is.
#>
function Install-Build([string] $setupPath) {
    Step 'Updating the installed copy'

    if (Get-Process -Name 'crystal-radio' -ErrorAction SilentlyContinue) {
        # The installer's CloseApplications would offer to do this, but not while it is silent.
        Fail ('Crystal Radio is running - close it first, then re-run with -Install.' + "`n" +
              "The installer is built either way: $setupPath")
    }

    $proc = Start-Process -FilePath $setupPath `
        -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' `
        -Verb RunAs -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Fail "Setup returned $($proc.ExitCode). The installer is built: $setupPath"
    }

    # Most uninstall keys have no DisplayName at all, and under Set-StrictMode reading a property
    # that is not there is an error rather than $null - so test for it before comparing.
    $installed = Get-ItemProperty `
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' `
        -ErrorAction SilentlyContinue |
        Where-Object {
            $_.PSObject.Properties.Name -contains 'DisplayName' -and $_.DisplayName -like 'Crystal Radio*'
        } | Select-Object -First 1

    if ($installed) {
        $version = if ($installed.PSObject.Properties.Name -contains 'DisplayVersion') { $installed.DisplayVersion } else { '?' }
        $where = if ($installed.PSObject.Properties.Name -contains 'InstallLocation') { $installed.InstallLocation } else { '' }
        Write-Host "  Installed: $($installed.DisplayName) $version" -ForegroundColor Green
        if ($where) { Write-Host "  $where" -ForegroundColor DarkGray }
    } else {
        # Not fatal - setup said it succeeded - but worth saying, because it means the version
        # Windows reports is not the one just built.
        Write-Host '  Setup succeeded but no Add/Remove Programs entry was found.' -ForegroundColor Yellow
    }
    Write-Host ''
}

try {
    if (-not (Test-Path $project)) { Fail "No crystal-radio.csproj in the repo root ($repo)." }
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
            foreach ($testProject in 'tests\RadioPlayer.Core.Tests\RadioPlayer.Core.Tests.csproj',
                                     'tests\RadioPlayer.Tests\RadioPlayer.Tests.csproj') {
                & dotnet test (Join-Path $repo $testProject) -c Release --nologo
                if ($LASTEXITCODE -ne 0) { Fail 'Tests failed - not building an installer from this.' }
            }
        }

        # --- Publish ---------------------------------------------------------------------------
        Step 'Publishing (self-contained, x64)'
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $staging, $dist | Out-Null

        # NO ReadyToRun here, and it must stay that way (#57).
        #
        # It was added for #53's cold-start time, on the assumption it would precompile WPF. It
        # does not: a self-contained publish already ships the framework R2R-compiled AND
        # Microsoft-signed from the runtime pack. The flag only rewrote our own assembly plus 12
        # small third-party ones - and rewriting is the problem. Windows Smart App Control runs
        # unsigned binaries on reputation, and a locally recompiled DLL is a file that exists
        # nowhere else on earth, so it has none. An install was blocked dead on
        # SQLitePCLRaw.provider.e_sqlite3.dll, and any of the 13 could have been the one.
        #
        # Measured on this build: turning it off gives 7 of those files their publisher's
        # Authenticode signature back (Google, Microsoft, ONNX ship them signed; R2R stripped it),
        # taking the payload from 241 valid signatures to 248, and produces byte-identical output
        # to a plain build - the copy with a clean history on the machine that was blocked.
        & dotnet publish $project -c Release -r win-x64 --self-contained true `
            -o $staging --nologo
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

        # --- Update this machine ---------------------------------------------------------------
        if ($Install) { Install-Build $setup.FullName }
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

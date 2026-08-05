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

# Must match WindowsNotificationService.AppUserModelId exactly. The string IS the pairing between
# the running process and this shortcut; a mismatch fails silently in both directions.
$AppUserModelId = 'HansSeebacher.CrystalRadio'

$repo    = $PSScriptRoot
$project = Join-Path $repo 'crystal-radio.csproj'
$tests   = Join-Path $repo 'tests\RadioPlayer.Tests\RadioPlayer.Tests.csproj'
$staging = Join-Path $env:TEMP ('crystal-radio-publish-' + [Guid]::NewGuid().ToString('n').Substring(0, 8))

<#
.SYNOPSIS
    Writes a .lnk carrying System.AppUserModel.ID.
.DESCRIPTION
    WScript.Shell can create a shortcut but cannot set that property, and it is the only part that
    matters here — so this goes through IShellLink + IPropertyStore. The inline C# is the shortest
    honest way to reach them from PowerShell.

    The shortcut lands in the INVOKING user's Start Menu. Elevating the same account via UAC keeps
    the same profile, which is the normal case; running this as a different admin account would put
    it in that account's Start Menu instead, and toasts would not appear for you.
#>
function New-Shortcut {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Target,
        [Parameter(Mandatory)] [string] $AppId,
        [string] $Description = ''
    )

    if (-not ('CrystalRadio.Shortcut' -as [type])) {
        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace CrystalRadio {
  [ComImport, Guid("00021401-0000-0000-C000-000000000046")] internal class CShellLink { }

  [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IShellLinkW {
    void GetPath(System.Text.StringBuilder f, int c, IntPtr d, uint g);
    void GetIDList(out IntPtr ppidl); void SetIDList(IntPtr pidl);
    void GetDescription(System.Text.StringBuilder n, int c);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory(System.Text.StringBuilder d, int c);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments(System.Text.StringBuilder a, int c);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short k); void SetHotkey(short k);
    void GetShowCmd(out int c); void SetShowCmd(int c);
    void GetIconLocation(System.Text.StringBuilder i, int c, out int idx);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    void Resolve(IntPtr hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
  }

  [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IPersistFile {
    void GetClassID(out Guid clsid); [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
  }

  [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  internal interface IPropertyStore {
    void GetCount(out uint c); void GetAt(uint i, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant v);
    void SetValue(ref PropertyKey key, ref PropVariant v);
    void Commit();
  }

  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  internal struct PropertyKey { public Guid FormatId; public int PropertyId; }

  [StructLayout(LayoutKind.Explicit)]
  internal struct PropVariant {
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Ptr;
  }

  public static class Shortcut {
    public static void Create(string path, string target, string appId, string description) {
      var link = (IShellLinkW)new CShellLink();
      link.SetPath(target);
      link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target));
      if (!string.IsNullOrEmpty(description)) link.SetDescription(description);

      // PKEY_AppUserModel_ID
      var key = new PropertyKey {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
      var value = new PropVariant { Type = 31 /* VT_LPWSTR */,
                                    Ptr = Marshal.StringToCoTaskMemUni(appId) };
      var store = (IPropertyStore)link;
      store.SetValue(ref key, ref value);
      store.Commit();
      Marshal.FreeCoTaskMem(value.Ptr);

      ((IPersistFile)link).Save(path, true);
    }
  }
}
'@
    }

    [CrystalRadio.Shortcut]::Create($Path, $Target, $AppId, $Description)
    Write-Host "  $Path"
}

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

# --- Start Menu shortcut ------------------------------------------------------------------------
# Not a convenience: Windows will not display a toast from an unpackaged app unless a Start Menu
# shortcut carries the same AppUserModelID the process sets. Established by experiment — with the
# AUMID alone the API reports success and nothing appears. See WindowsNotificationService.
#
# Also makes the app launchable from Start, which a folder copy into Program Files otherwise isn't.

Step "Creating the Start Menu shortcut"
New-Shortcut -Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'Crystal Radio.lnk') `
             -Target (Join-Path $Destination 'crystal-radio.exe') `
             -AppId $AppUserModelId -Description 'Crystal Radio'

$files = @(Get-ChildItem $Destination -Recurse -File).Count
Write-Host ""
Write-Host "  Crystal Radio $version installed" -ForegroundColor Green
Write-Host "  $Destination  ($files files)"
Write-Host "  $(Join-Path $Destination 'crystal-radio.exe')"
Write-Host "  Start Menu shortcut written (required for notifications)"
Write-Host ""

# robocopy's success codes are non-zero (1 = files copied), and a script's exit code defaults to
# the last native command's. Without this, every successful install reports failure to a caller.
exit 0

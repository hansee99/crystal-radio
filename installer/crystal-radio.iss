; Crystal Radio — Windows installer (#43)
;
; Built by scripts/build-installer.ps1, which publishes a self-contained x64 build into a staging folder
; and passes it in as /DStagingDir. Compiling this file directly will fail on purpose: the payload
; has to be a fresh publish, not whatever happens to be lying in bin/.
;
; Three things this does beyond copying files:
;
;   1. Sets AppUserModelID on the Start Menu shortcut. Toast notifications from an unpackaged app
;      are attributed by that ID, and Windows reads it from the shortcut — without it the app can
;      raise toasts that never appear. It must match WindowsNotificationService.Aumid exactly.
;      Note this shortcut alone is NOT enough: the lookup is per-user and these go to the all-users
;      Start Menu, so the app writes its own copy at startup (Services/StartMenuShortcut.cs). This
;      one is what makes Crystal Radio findable in Start for every user of the machine.
;   2. Downloads the 86 MB ONNX embedding model at install time rather than carrying it. It is a
;      third-party artifact under its own licence, it is git-ignored in this repo for the same
;      reason, and bundling it would triple the installer.
;   3. Ships a pre-built station catalog, which the app copies into the user's profile on first run
;      (see CatalogSeed). Per-user, so it cannot be a plain install-time file copy.

#ifndef StagingDir
  #error Build this with scripts/build-installer.ps1 — it needs /DStagingDir=<published output>
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName        "Crystal Radio"
#define AppPublisher   "Hans Seebacher"
#define AppExe         "crystal-radio.exe"
#define AumId          "HansSeebacher.CrystalRadio"
#define ModelFile      "all-MiniLM-L6-v2.onnx"
#define ModelUrl       "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx"
; Verified against the file this project has been running with all along.
#define ModelSha256    "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"
; Its exact size, so a reinstall can tell "already there" from "half a download" without hashing.
#define ModelBytes     "90405214"

[Setup]
; Never change AppId — it is what makes the next release an upgrade rather than a second copy.
AppId={{7E4C1F3A-9B2D-4E85-A1C7-63D0F5A8B412}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#StagingDir}\..\dist
OutputBaseFilename=crystal-radio-setup-{#AppVersion}
SetupIconFile={#SourcePath}\..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The app is x64 because the native BASS DLLs are.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; An upgrade over a running copy would leave half the files locked.
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything from the publish except the model, which is downloaded below. Excluding *.pdb keeps
; ~30 MB of symbols out of a user-facing installer.
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,MlAssets\{#ModelFile}"

; Downloaded to {tmp} by the [Code] below; "external" means "not compiled into setup.exe".
; skipifsourcedoesntexist covers the case where the user chose to install without the model.
Source: "{tmp}\{#ModelFile}"; DestDir: "{app}\MlAssets"; Flags: external ignoreversion skipifsourcedoesntexist

[Icons]
; AppUserModelID is what makes toast notifications work for an unpackaged app — see the header.
; Per-machine, so it reaches every user; the per-user copy the shell actually resolves against is
; written by the app itself.
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AumId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AumId}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The downloaded model is not tracked by the installer (it is an "external" file), so say so
; explicitly or an uninstall leaves 86 MB behind.
Type: files; Name: "{app}\MlAssets\{#ModelFile}"
Type: dirifempty; Name: "{app}\MlAssets"

[Code]
var
  DownloadPage: TDownloadWizardPage;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then
    Log(Format('Downloaded %s', [FileName]));
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    'Downloading the language model',
    'Crystal Radio matches stations to what you describe using a local model, so nothing you type leaves your machine.',
    @OnDownloadProgress);
end;

{
  True when this machine already has the model, at its full size.

  Reinstalling over an existing install is the normal way to update (scripts/build-installer.ps1 -Install
  after a git pull), and re-fetching 86 MB every time to write the same bytes is most of the wait
  for a change of a few kilobytes. Size rather than hash: a hash of 86 MB costs seconds on the kind
  of machine this exists for, and the failure it would catch - a corrupt model - already degrades
  gracefully, because the embedding provider reports itself unavailable and search carries on
  without it. A truncated download is what size catches, and that is the realistic one.
}
function ModelAlreadyInstalled: Boolean;
var
  path: String;
  size: Int64;
begin
  Result := False;
  path := ExpandConstant('{app}\MlAssets\{#ModelFile}');
  if not FileExists(path) then
    Exit;
  if not FileSize64(path, size) then
    Exit;
  Result := size = {#ModelBytes};
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID <> wpReady then
  begin
    Result := True;
    Exit;
  end;

  if ModelAlreadyInstalled then
  begin
    // Nothing to fetch. The Files entry sources from the temp folder, so skipifsourcedoesntexist
    // leaves the installed copy exactly where it is.
    // (Line comments on purpose: a brace comment would end at the first closing brace, and an
    //  Inno constant written inline would be one - which is what broke this the first time.)
    Log('Language model already present at full size - skipping the download.');
    Result := True;
    Exit;
  end;

  DownloadPage.Clear;
  { The hash is checked by Inno; a corrupted or substituted download fails the install. }
  DownloadPage.Add('{#ModelUrl}', '{#ModelFile}', '{#ModelSha256}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Result := True;
    except
      { Offline, or Hugging Face is down. Installing without the model is a real option: the app
        degrades to directory search and says so, and the model can be fetched later. }
      Log(GetExceptionMessage);
      Result := MsgBox(
        'The language model could not be downloaded.' + #13#10#13#10 +
        GetExceptionMessage + #13#10#13#10 +
        'Install without it? Crystal Radio will still play radio and search the station directory, '
        + 'but matching stations to a description needs the model. Re-run this installer later to add it.',
        mbConfirmation, MB_YESNO) = IDYES;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

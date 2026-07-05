; Inno Setup script for Claude Revit
;
; Build manually:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\ClaudeRevit.iss
;
; CI passes the version via /DAppVersion=v1.2 — see .github/workflows/release.yml
;
; The installer:
;   - Targets per-user install (no admin)
;   - Lets the user tick which installed Revit versions (2025/2026/2027) to install for;
;     detected versions are pre-checked. The matching per-version build (net8/net10) is
;     copied into that version's %AppData%\Autodesk\Revit\Addins\<year>\ folder.
;   - Detects if Revit is running, asks to close
;   - Clean uninstall removes our files (but leaves user's history + API key in
;     %AppData%\ClaudeRevit)

#define MyAppName "Claude Revit"
#define MyAppPublisher "roubaudal-maker"
#define MyAppURL "https://github.com/roubaudal-maker/ClaudeRevit"

#ifndef AppVersion
  #define AppVersion "v0.0-dev"
#endif

; Strip leading 'v' from tag for AppVersion field (Inno wants digits like 1.2.0)
#define VersionNumeric Copy(AppVersion, 2, 99)

[Setup]
AppId={{C8A3E9F4-7D2B-4E16-9A5C-3F8B6D4E2A1C}}
AppName={#MyAppName}
AppVersion={#VersionNumeric}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
; {app} is only a registration anchor now — files go to each Revit version's Addins
; folder via absolute DestDir below, so the directory page is hidden.
DefaultDirName={userappdata}\ClaudeRevit\install
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=ClaudeRevit-Setup-{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName}
CloseApplications=yes
CloseApplicationsFilter=*.dll
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; Each version's payload is copied only when its checkbox is ticked (Check: WantVersion),
; into that Revit version's per-user Addins folder.
[Files]
Source: "payload\2025\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: WantVersion('2025')
Source: "payload\2026\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: WantVersion('2026')
Source: "payload\2027\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: WantVersion('2027')

[Run]
Filename: "{#MyAppURL}/blob/main/README.md"; Description: "Open the README"; Flags: postinstall shellexec skipifsilent unchecked

[Code]
var
  RevitWasRunning: Boolean;
  VersionsPage: TWizardPage;
  ChkVer: array[0..2] of TNewCheckBox;

const
  Versions: array[0..2] of String = ('2025', '2026', '2027');

// A Revit version is "installed" if either its all-users add-ins folder (created by the
// Revit installer) or its program folder exists.
function RevitInstalled(Ver: String): Boolean;
begin
  Result := DirExists(ExpandConstant('{commonappdata}\Autodesk\Revit\Addins\' + Ver))
         or DirExists(ExpandConstant('{commonpf}\Autodesk\Revit ' + Ver))
         or DirExists(ExpandConstant('{commonpf64}\Autodesk\Revit ' + Ver));
end;

function IsRevitRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec('powershell.exe',
          '-NoProfile -NonInteractive -Command "exit ([int](Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0)"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 1);
end;

procedure InitializeWizard;
var
  i, y: Integer;
  anyDetected: Boolean;
  intro: TNewStaticText;
begin
  VersionsPage := CreateCustomPage(
    wpWelcome,
    'Choose Revit versions',
    'Tick the Revit versions you want to install Claude for.');

  intro := TNewStaticText.Create(VersionsPage);
  intro.Parent := VersionsPage.Surface;
  intro.Top := 0;
  intro.Left := 0;
  intro.Width := VersionsPage.SurfaceWidth;
  intro.AutoSize := False;
  intro.Height := 34;
  intro.WordWrap := True;
  intro.Caption := 'Installed versions are detected and pre-selected. Each version gets the '
    + 'build that matches its .NET runtime (2025/2026 = .NET 8, 2027 = .NET 10).';

  anyDetected := False;
  y := 42;
  for i := 0 to 2 do
  begin
    ChkVer[i] := TNewCheckBox.Create(VersionsPage);
    ChkVer[i].Parent := VersionsPage.Surface;
    ChkVer[i].Top := y;
    ChkVer[i].Left := 0;
    ChkVer[i].Width := VersionsPage.SurfaceWidth;
    if RevitInstalled(Versions[i]) then
    begin
      ChkVer[i].Caption := 'Revit ' + Versions[i] + '  (detected)';
      ChkVer[i].Checked := True;
      anyDetected := True;
    end
    else
      ChkVer[i].Caption := 'Revit ' + Versions[i] + '  (not detected — tick to install anyway)';
    y := y + 26;
  end;

  // Nothing detected: default to the newest so a working install is one click away.
  if not anyDetected then
    ChkVer[2].Checked := True;
end;

function WantVersion(Ver: String): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 0 to 2 do
    if Versions[i] = Ver then
      Result := ChkVer[i].Checked;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = VersionsPage.ID then
  begin
    if not (ChkVer[0].Checked or ChkVer[1].Checked or ChkVer[2].Checked) then
    begin
      MsgBox('Select at least one Revit version to install for.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  RevitWasRunning := IsRevitRunning;
  if RevitWasRunning then
  begin
    if MsgBox(
         'Autodesk Revit is currently running.' + #13#10 + #13#10 +
         'The installer can copy files now but they won''t take effect ' +
         'until you close and re-open Revit.' + #13#10 + #13#10 +
         'Continue anyway?',
         mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if RevitWasRunning then
      MsgBox(
        'Installed. Close and re-open Revit to load the new version.' + #13#10 + #13#10 +
        'After Revit restarts, look for the Claude tab. ' +
        'If this is your first install, click the gear icon in the chat pane to set your Anthropic API key.',
        mbInformation, MB_OK);
  end;
end;

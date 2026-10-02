// Compiled by Inno and run silently. InitializeSetup always aborts: no install,
// uninstall registration or production add-in files are created.
[Setup]
AppName=ClaudeRevit installer detection check
AppVersion=1.0
VersionInfoVersion=27.0.0.0
DefaultDirName={tmp}\Unused
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=DetectionCheck
SetupLogging=yes

[Code]
#include "..\RevitDetection.iss"

procedure Check(Value: Boolean; Message: String);
begin
  if not Value then RaiseException(Message);
end;

function InitializeSetup: Boolean;
var
  FixtureDir, FixtureExe, Key, Actual: String;
  OwnKey: Boolean;
begin
  Result := False;
  OwnKey := False;
  FixtureDir := ExpandConstant('{param:FIXTURE|}');
  FixtureExe := AddBackslash(FixtureDir) + 'Revit.exe';
  Key := 'Software\ClaudeRevit\InstallerChecks\' + ExtractFileName(ExpandConstant('{tmp}'));
  try
    Check(not RegKeyExists(HKCU64, Key), 'Test registry key already exists');
    Check(not RegKeyExists(HKCU32, Key), '32-bit test registry key already exists');
    OwnKey := True;
    Check(FixtureDir <> '', 'Fixture path was not provided');
    Check(FileExists(FixtureExe), 'Versioned fixture executable is missing');
    Check(RevitExeCandidate('2027', FixtureDir) = FixtureExe, 'Custom executable path was missed');
    Check(RevitExeCandidate('2026', FixtureDir) = '', 'Wrong executable year was accepted');
    Check(RevitExeCandidate('2027', FixtureDir + '\missing') = '', 'Missing executable was accepted');
    Check(RegWriteStringValue(HKCU64, Key + '\2027\REVIT-05:0419', 'InstallationLocation', FixtureDir + '\'), 'Cannot write fixture registry');
    Check(RegisteredRevitExe(HKCU64, Key + '\2027', '2027', 0) = FixtureExe, 'Localized custom installation registry path was missed');
    Check(RegisteredRevitExe(HKCU64, Key + '\2027', '2026', 0) = '', 'Registry detection accepted wrong year');
    Check(RegWriteStringValue(HKCU32, Key + '\32view', 'InstallLocation', FixtureDir), 'Cannot write 32-bit fixture');
    Check(RegisteredRevitExe(HKCU32, Key + '\32view', '2027', 0) = FixtureExe, '32-bit registry view was missed');
    Check(RegWriteStringValue(HKCU64, Key + '\stale', 'InstallationLocation', FixtureDir + '\missing'), 'Cannot write stale fixture');
    Check(RegisteredRevitExe(HKCU64, Key + '\stale', '2027', 0) = '', 'Stale installation registration was accepted');
    Actual := FindRevitExe('2027');
    if Pos('/REQUIRE2027', UpperCase(GetCmdTail)) > 0 then
      Check(Actual <> '', 'Actual Revit 2027 installation was missed');
    Log('INSTALLER_DETECTION_CHECKS_PASSED; actual_2027=' + Actual);
  except
    Log('INSTALLER_DETECTION_CHECKS_FAILED: ' + GetExceptionMessage);
  end;
  if OwnKey then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU64, Key);
    RegDeleteKeyIncludingSubkeys(HKCU32, Key);
  end;
end;

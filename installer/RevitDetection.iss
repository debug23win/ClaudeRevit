// Read-only discovery. A registry key or add-ins/content folder alone is not an
// installation: require an existing Revit.exe with the requested year version.
function RevitExeCandidate(Ver, Candidate: String): String;
var
  ExePath: String;
  VersionMS, VersionLS: Cardinal;
begin
  Result := '';
  Candidate := RemoveQuotes(Trim(Candidate));
  if Candidate = '' then Exit;
  if CompareText(ExtractFileName(Candidate), 'Revit.exe') = 0 then
    ExePath := Candidate
  else
    ExePath := AddBackslash(Candidate) + 'Revit.exe';
  if FileExists(ExePath) and GetVersionNumbers(ExePath, VersionMS, VersionLS) then
    if (VersionMS shr 16) = StrToInt(Ver) - 2000 then Result := ExePath;
end;

function RegisteredRevitExe(RootKey: Integer; Key, Ver: String; Depth: Integer): String;
var
  Names: TArrayOfString;
  Value, ValueName: String;
  i: Integer;
begin
  Result := '';
  // Autodesk uses localized children such as 2027\REVIT-05:0419.
  for i := 0 to 4 do
  begin
    case i of
      0: ValueName := 'InstallationLocation';
      1: ValueName := 'InstallLocation';
      2: ValueName := 'InstallPath';
      3: ValueName := 'Path';
      4: ValueName := 'InstallRoot';
    end;
    if RegQueryStringValue(RootKey, Key, ValueName, Value) then
    begin
      Result := RevitExeCandidate(Ver, Value);
      if (Result = '') and (ValueName = 'InstallRoot') then
        Result := RevitExeCandidate(Ver, AddBackslash(Value) + 'Revit ' + Ver);
      if Result <> '' then Exit;
    end;
  end;
  if (Depth < 3) and RegGetSubkeyNames(RootKey, Key, Names) then
    for i := 0 to GetArrayLength(Names) - 1 do
    begin
      Result := RegisteredRevitExe(RootKey, Key + '\' + Names[i], Ver, Depth + 1);
      if Result <> '' then Exit;
    end;
end;

function RevitExeForRegistryView(RootKey: Integer; Ver: String): String;
begin
  Result := RegisteredRevitExe(RootKey, 'SOFTWARE\Autodesk\Revit\' + Ver, Ver, 0);
  if Result = '' then
    Result := RegisteredRevitExe(RootKey, 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + Ver, Ver, 0);
end;

function FindRevitExe(Ver: String): String;
begin
  Result := '';
  if IsWin64 then
  begin
    Result := RevitExeForRegistryView(HKLM64, Ver);
    if Result = '' then Result := RevitExeForRegistryView(HKCU64, Ver);
  end;
  if Result = '' then Result := RevitExeForRegistryView(HKLM32, Ver);
  if Result = '' then Result := RevitExeForRegistryView(HKCU32, Ver);
  if Result = '' then Result := RevitExeCandidate(Ver, ExpandConstant('{commonpf}\Autodesk\Revit ' + Ver));
  if (Result = '') and IsWin64 then
    Result := RevitExeCandidate(Ver, ExpandConstant('{commonpf64}\Autodesk\Revit ' + Ver));
  if Result <> '' then Log('Detected Revit ' + Ver + ': ' + Result)
  else Log('Revit ' + Ver + ' not detected; manual selection remains available.');
end;

; ----------------------------------------------------------------------------
; Sutra — Revit MCP (SCIO DC) — Windows Setup Installer
; Clean-room installer & in-place upgrader for Revit 2023–2027 + MCP Server
; ----------------------------------------------------------------------------

#define AppName        "Sutra — Revit MCP"
#define AppPublisher   "SCIO DC"
#define AppUrl         "https://github.com/mpomav/sutra-revit-mcp"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef StageDir
  #define StageDir "..\dist\sutra-1.1.0"
#endif

[Setup]
AppId={{8F7B4E21-9A3C-4D1B-B62E-5C04A1F9E204}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\Scio\MCP
UsePreviousAppDir=yes
DefaultGroupName=Sutra — Revit MCP
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\dist
OutputBaseFilename=sutra-revit-mcp-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayName={#AppName} {#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
es.RevitRunning=Autodesk Revit está abierto. Cierra todas las ventanas de Revit antes de continuar con la instalación o actualización de Sutra.
en.RevitRunning=Autodesk Revit is currently running. Close all Revit windows before continuing Sutra installation or upgrade.
es.FinishedInstall=Sutra — Revit MCP v%1 se instaló correctamente.%n%nRuta del servidor MCP:%n%2%n%nVersiones de Revit actualizadas: %3%n%nAbre Autodesk Revit y acepta "Cargar siempre" (Always Load) si aparece el aviso de seguridad.
en.FinishedInstall=Sutra — Revit MCP v%1 was installed successfully.%n%nMCP Server path:%n%2%n%nUpdated Revit versions: %3%n%nOpen Autodesk Revit and choose "Always Load" if prompted by the security dialog.
es.FinishedUpgrade=Sutra — Revit MCP se actualizó correctamente (%1 -> v%2).%n%nRuta del servidor MCP:%n%3%n%nVersiones de Revit actualizadas: %4%n%nAbre Autodesk Revit para cargar la nueva versión.
en.FinishedUpgrade=Sutra — Revit MCP was upgraded successfully (%1 -> v%2).%n%nMCP Server path:%n%3%n%nUpdated Revit versions: %4%n%nOpen Autodesk Revit to load the new version.
es.NoRevitFound=ninguna detectada en C:\Program Files\Autodesk\Revit 2023–2027 (solo se instaló el servidor MCP)
en.NoRevitFound=none detected under C:\Program Files\Autodesk\Revit 2023–2027 (MCP server installed only)

[Files]
; Self-contained MCP server (win-x64)
Source: "{#StageDir}\server\*"; DestDir: "{app}\server"; Flags: ignoreversion recursesubdirs createallsubdirs

; Docs & legal notices
Source: "{#StageDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\README.es.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\PROVENANCE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

; Per-year Revit add-in payloads staged in {tmp} and deployed only for installed Revit years
Source: "{#StageDir}\addins\2023\*"; DestDir: "{tmp}\SutraAddins\2023"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "{#StageDir}\addins\2024\*"; DestDir: "{tmp}\SutraAddins\2024"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "{#StageDir}\addins\2025\*"; DestDir: "{tmp}\SutraAddins\2025"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "{#StageDir}\addins\2026\*"; DestDir: "{tmp}\SutraAddins\2026"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "{#StageDir}\addins\2027\*"; DestDir: "{tmp}\SutraAddins\2027"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall

[InstallDelete]
; Clean previous server and legacy staging directories to guarantee a clean in-place upgrade
Type: filesandordirs; Name: "{app}\server"
Type: filesandordirs; Name: "{app}\server.previous"
Type: filesandordirs; Name: "{app}\server.installing"
Type: filesandordirs; Name: "{app}\plugin"
Type: files; Name: "{app}\manifest.json"
Type: files; Name: "{app}\manifest.previous.json"

[Code]
var
  InstalledYearsList: string;
  PreviousVersion: string;

function IsProcessRunning(const ImageName: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{sys}\cmd.exe'),
          '/C tasklist /FI "IMAGENAME eq ' + ImageName + '" 2>NUL | find /I "' + ImageName + '" >NUL',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := (ResultCode = 0);
  end;
end;

procedure StopServerProcess;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM scio-revit-mcp.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function DetectPreviousVersion(): string;
var
  Ver: string;
begin
  Result := '';
  if RegQueryStringValue(HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F7B4E21-9A3C-4D1B-B62E-5C04A1F9E204}_is1',
       'DisplayVersion', Ver) and (Ver <> '') then
  begin
    Result := 'v' + Ver;
    Exit;
  end;
  if RegQueryStringValue(HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D28EC3E0-E345-5166-958D-F86EABBD0EAE}_is1',
       'DisplayVersion', Ver) and (Ver <> '') then
  begin
    Result := 'SCIO v' + Ver;
    Exit;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  PreviousVersion := DetectPreviousVersion();
  while IsProcessRunning('Revit.exe') do
  begin
    if MsgBox(CustomMessage('RevitRunning'), mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
  StopServerProcess;
end;

procedure CopyDirRecursive(const SrcDir, DstDir: string);
var
  FindRec: TFindRec;
  SrcPath, DstPath: string;
begin
  ForceDirectories(DstDir);
  if FindFirst(AddBackslash(SrcDir) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          SrcPath := AddBackslash(SrcDir) + FindRec.Name;
          DstPath := AddBackslash(DstDir) + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            CopyDirRecursive(SrcPath, DstPath)
          else
            FileCopy(SrcPath, DstPath, False);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure DeployRevitYear(const YearStr: string);
var
  RevitApiPath, SrcDir, AddinRoot, PayloadDir, OldManifest, ManifestPath, ManifestXml: string;
begin
  RevitApiPath := 'C:\Program Files\Autodesk\Revit ' + YearStr + '\RevitAPI.dll';
  if not FileExists(RevitApiPath) then
    Exit;

  SrcDir := ExpandConstant('{tmp}\SutraAddins\') + YearStr;
  if not DirExists(SrcDir) then
    Exit;

  AddinRoot := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\') + YearStr;
  ForceDirectories(AddinRoot);

  { Remove legacy manifests and old Scio payload directories so upgrades never mix versions }
  OldManifest := AddBackslash(AddinRoot) + 'Scio.addin';
  if FileExists(OldManifest) then DeleteFile(OldManifest);
  OldManifest := AddBackslash(AddinRoot) + 'Scio.addin.previous';
  if FileExists(OldManifest) then DeleteFile(OldManifest);
  ManifestPath := AddBackslash(AddinRoot) + 'ScioRevitMcp.addin';
  if FileExists(ManifestPath) then DeleteFile(ManifestPath);

  PayloadDir := AddBackslash(AddinRoot) + 'Scio';
  if DirExists(PayloadDir) then DelTree(PayloadDir, True, True, True);
  if DirExists(AddBackslash(AddinRoot) + 'Scio.previous') then
    DelTree(AddBackslash(AddinRoot) + 'Scio.previous', True, True, True);
  if DirExists(AddBackslash(AddinRoot) + 'Scio.installing') then
    DelTree(AddBackslash(AddinRoot) + 'Scio.installing', True, True, True);

  CopyDirRecursive(SrcDir, PayloadDir);

  ManifestXml :=
    '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
    '<RevitAddIns>' + #13#10 +
    '  <AddIn Type="Application">' + #13#10 +
    '    <Name>Sutra - Revit MCP (SCIO DC)</Name>' + #13#10 +
    '    <Assembly>' + AddBackslash(PayloadDir) + 'Scio.Revit.Host.dll</Assembly>' + #13#10 +
    '    <AddInId>8F7B4E21-9A3C-4D1B-B62E-5C04A1F9E204</AddInId>' + #13#10 +
    '    <FullClassName>Scio.Revit.Host.RevitBridgeApp</FullClassName>' + #13#10 +
    '    <VendorId>SCIO</VendorId>' + #13#10 +
    '    <VendorDescription>SCIO DC (Grupo POVA) - https://sciodc.com</VendorDescription>' + #13#10 +
    '  </AddIn>' + #13#10 +
    '</RevitAddIns>' + #13#10;

  SaveStringToFile(ManifestPath, ManifestXml, False);

  if InstalledYearsList <> '' then
    InstalledYearsList := InstalledYearsList + ', ';
  InstalledYearsList := InstalledYearsList + YearStr;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Years: array[0..4] of string;
  I: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    InstalledYearsList := '';
    Years[0] := '2023';
    Years[1] := '2024';
    Years[2] := '2025';
    Years[3] := '2026';
    Years[4] := '2027';
    for I := 0 to 4 do
      DeployRevitYear(Years[I]);

    SaveStringToFile(ExpandConstant('{app}\version.txt'), '{#AppVersion}' + #13#10, False);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  ServerExe, YearsDisplay: string;
begin
  if CurPageID = wpFinished then
  begin
    ServerExe := ExpandConstant('{app}\server\scio-revit-mcp.exe');
    if InstalledYearsList = '' then
      YearsDisplay := CustomMessage('NoRevitFound')
    else
      YearsDisplay := InstalledYearsList;

    if PreviousVersion <> '' then
      WizardForm.FinishedLabel.Caption :=
        FmtMessage(CustomMessage('FinishedUpgrade'), [PreviousVersion, '{#AppVersion}', ServerExe, YearsDisplay])
    else
      WizardForm.FinishedLabel.Caption :=
        FmtMessage(CustomMessage('FinishedInstall'), ['{#AppVersion}', ServerExe, YearsDisplay]);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Years: array[0..4] of string;
  I: Integer;
  AddinRoot, PayloadDir, ManifestPath: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopServerProcess;
    Years[0] := '2023';
    Years[1] := '2024';
    Years[2] := '2025';
    Years[3] := '2026';
    Years[4] := '2027';
    for I := 0 to 4 do
    begin
      AddinRoot := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\') + Years[I];
      ManifestPath := AddBackslash(AddinRoot) + 'ScioRevitMcp.addin';
      if FileExists(ManifestPath) then DeleteFile(ManifestPath);
      PayloadDir := AddBackslash(AddinRoot) + 'Scio';
      if DirExists(PayloadDir) then DelTree(PayloadDir, True, True, True);
    end;
  end;
end;

; ----------------------------------------------------------------------------
; Sutra — Revit MCP (SCIO DC) — Windows Setup Installer
; Clean-room installer & in-place upgrader for Revit 2023–2027 + MCP Server
; ----------------------------------------------------------------------------

#define AppName        "Sutra — Revit MCP"
#define AppPublisher   "SCIO DC"
#define AppUrl         "https://github.com/mpomav/sutra-revit-mcp"
#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#ifndef StageDir
  #define StageDir "..\dist\sutra-1.3.0"
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
SetupIconFile=assets\sutra-setup.ico
WizardImageFile=assets\sutra-wizard-100.bmp,assets\sutra-wizard-125.bmp,assets\sutra-wizard-150.bmp,assets\sutra-wizard-175.bmp,assets\sutra-wizard-200.bmp,assets\sutra-wizard-225.bmp,assets\sutra-wizard-250.bmp
WizardImageStretch=no
WizardSmallImageFile=assets\wizard-small.bmp
PrivilegesRequired=lowest
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\server\scio-revit-mcp.exe
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
es.CadRunning=AutoCAD / Civil 3D está abierto. Cierra todas las ventanas de AutoCAD / Civil 3D antes de continuar con la instalación o actualización de Sutra.
en.CadRunning=AutoCAD / Civil 3D is currently running. Close all AutoCAD / Civil 3D windows before continuing Sutra installation or upgrade.
es.FinishedInstall=Sutra MCP v%1 se instaló correctamente.%n%nRuta del servidor MCP:%n%2%n%nRevit actualizado: %3%nAutoCAD / Civil 3D actualizado: %4%nClientes MCP configurados: %5%n%nCierra completamente tu cliente de IA (en Claude Desktop: clic derecho en el ícono de la bandeja del sistema -> Salir/Quit), vuelve a abrirlo y abre Revit, AutoCAD o Civil 3D (acepta "Cargar siempre" si aparece el aviso).
en.FinishedInstall=Sutra MCP v%1 was installed successfully.%n%nMCP Server path:%n%2%n%nUpdated Revit versions: %3%nUpdated AutoCAD / Civil 3D versions: %4%nConfigured MCP clients: %5%n%nFully quit your AI client (in Claude Desktop: right-click the system tray icon -> Quit), relaunch it, and open Revit, AutoCAD, or Civil 3D (choose "Always Load" if prompted).
es.FinishedUpgrade=Sutra MCP se actualizó correctamente (%1 -> v%2).%n%nRuta del servidor MCP:%n%3%n%nRevit actualizado: %4%nAutoCAD / Civil 3D actualizado: %5%nClientes MCP actualizados: %6%n%nCierra completamente tu cliente de IA (en Claude Desktop: clic derecho en la bandeja del sistema -> Salir/Quit), vuelve a abrirlo y abre Revit, AutoCAD o Civil 3D para cargar la nueva versión.
en.FinishedUpgrade=Sutra MCP was upgraded successfully (%1 -> v%2).%n%nMCP Server path:%n%3%n%nUpdated Revit versions: %4%nUpdated AutoCAD / Civil 3D versions: %5%nUpdated MCP clients: %6%n%nFully quit your AI client (in Claude Desktop: right-click the system tray icon -> Quit), relaunch it, and open Revit, AutoCAD, or Civil 3D to load the new version.
es.NoRevitFound=ninguna detectada (2023–2027)
en.NoRevitFound=none detected (2023–2027)
es.NoCadFound=ninguna detectada (2023–2026)
en.NoCadFound=none detected (2023–2026)
es.NoMcpConfigured=ninguno seleccionado (puedes ejecutar scripts\configure-mcp-clients.ps1 después)
en.NoMcpConfigured=none selected (you can run scripts\configure-mcp-clients.ps1 later)
es.McpPageTitle=Configurar clientes de IA (MCP)
en.McpPageTitle=Configure AI Clients (MCP)
es.McpPageSubtitle=Selecciona los clientes de IA donde Sutra registrará o actualizará el servidor MCP.
en.McpPageSubtitle=Select the AI clients where Sutra should register or update the MCP server.
es.McpPageDesc=Solo se agrega o actualiza la entrada "scio-revit" (tus otros servidores MCP se conservan intactos y se crea una copia .bak). Los clientes detectados en este equipo aparecen preseleccionados:
en.McpPageDesc=Only the "scio-revit" entry is added or updated (your other MCP servers remain untouched and a .bak backup is created). Clients detected on this machine are pre-selected:

[Files]
; Self-contained MCP server (win-x64)
Source: "{#StageDir}\server\*"; DestDir: "{app}\server"; Flags: ignoreversion recursesubdirs createallsubdirs

; Helper scripts (MCP client auto-configurator)
Source: "{#StageDir}\scripts\configure-mcp-clients.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion skipifsourcedoesntexist

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

; Per-year AutoCAD / Civil 3D add-in payloads (Scio.Cad.dll) staged in {tmp} and deployed as SutraCad.bundle
Source: "{#StageDir}\cad-addins\2023\*"; DestDir: "{tmp}\SutraCadAddins\2023"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "{#StageDir}\cad-addins\2024\*"; DestDir: "{tmp}\SutraCadAddins\2024"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "{#StageDir}\cad-addins\2025\*"; DestDir: "{tmp}\SutraCadAddins\2025"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall
Source: "{#StageDir}\cad-addins\2026\*"; DestDir: "{tmp}\SutraCadAddins\2026"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist deleteafterinstall

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
  InstalledCadYearsList: string;
  ConfiguredMcpList: string;
  PreviousVersion: string;
  McpClientsPage: TInputOptionWizardPage;
  McpClientKeys: array[0..7] of string;

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
  while IsProcessRunning('acad.exe') do
  begin
    if MsgBox(CustomMessage('CadRunning'), mbError, MB_RETRYCANCEL) = IDCANCEL then
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

procedure DeployCadBundle;
var
  CadYears: array[0..3] of string;
  SeriesIds: array[0..3] of string;
  I: Integer;
  YearStr, CadApiPath, SrcDll, BundleDir, DestDir, DestDll, ComponentsXml, PkgXml: string;
begin
  InstalledCadYearsList := '';
  CadYears[0] := '2023'; SeriesIds[0] := 'R24.2';
  CadYears[1] := '2024'; SeriesIds[1] := 'R24.3';
  CadYears[2] := '2025'; SeriesIds[2] := 'R25.0';
  CadYears[3] := '2026'; SeriesIds[3] := 'R25.1';

  BundleDir := ExpandConstant('{userappdata}\Autodesk\ApplicationPlugins\SutraCad.bundle');
  ComponentsXml := '';

  for I := 0 to 3 do
  begin
    YearStr := CadYears[I];
    CadApiPath := 'C:\Program Files\Autodesk\AutoCAD ' + YearStr + '\acdbmgd.dll';
    SrcDll := ExpandConstant('{tmp}\SutraCadAddins\') + YearStr + '\Scio.Cad.dll';
    if FileExists(CadApiPath) and FileExists(SrcDll) then
    begin
      DestDir := AddBackslash(BundleDir) + 'Contents\' + YearStr;
      if DirExists(DestDir) then DelTree(DestDir, True, True, True);
      ForceDirectories(DestDir);
      DestDll := AddBackslash(DestDir) + 'Scio.Cad.dll';
      FileCopy(SrcDll, DestDll, False);

      ComponentsXml := ComponentsXml +
        '  <Components Description="Sutra CAD ' + YearStr + ' (AutoCAD &amp; Civil 3D)">' + #13#10 +
        '    <RuntimeRequirements OS="Win64" Platform="AutoCAD|AutoCAD*|Civil3D" SeriesMin="' + SeriesIds[I] + '" SeriesMax="' + SeriesIds[I] + '" />' + #13#10 +
        '    <ComponentEntry AppName="SutraCad" Version="{#AppVersion}" ModuleName="./Contents/' + YearStr + '/Scio.Cad.dll" LoadOnAutoCADStartup="True" />' + #13#10 +
        '  </Components>' + #13#10;

      if InstalledCadYearsList <> '' then
        InstalledCadYearsList := InstalledCadYearsList + ', ';
      InstalledCadYearsList := InstalledCadYearsList + YearStr;
    end;
  end;

  if ComponentsXml <> '' then
  begin
    PkgXml :=
      '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
      '<ApplicationPackage SchemaVersion="1.0"' + #13#10 +
      '                    AutodeskProduct="AutoCAD"' + #13#10 +
      '                    Name="Sutra — AutoCAD &amp; Civil 3D MCP"' + #13#10 +
      '                    Description="Transactional, local-first MCP bridge for AutoCAD and Civil 3D integrated with Sutra."' + #13#10 +
      '                    AppVersion="{#AppVersion}"' + #13#10 +
      '                    Author="Marco Poma - SCIO DC"' + #13#10 +
      '                    ProductCode="{9F4C2B71-3E48-4C7A-A1F2-5D9E8A31C001}"' + #13#10 +
      '                    UpgradeCode="{9F4C2B71-3E48-4C7A-A1F2-5D9E8A31C002}">' + #13#10 +
      '  <CompanyDetails Name="SCIO DC (Grupo POVA)" Url="https://sciodc.com" />' + #13#10 +
      ComponentsXml +
      '</ApplicationPackage>' + #13#10;

    SaveStringToFile(AddBackslash(BundleDir) + 'PackageContents.xml', PkgXml, False);
  end;
end;

function HasClaudeMsixFolder(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{localappdata}\Packages\Claude_*'), FindRec) then
  begin
    try
      Result := True;
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure InitializeWizard;
begin
  McpClientsPage := CreateInputOptionPage(
    wpWelcome,
    CustomMessage('McpPageTitle'),
    CustomMessage('McpPageSubtitle'),
    CustomMessage('McpPageDesc'),
    False,
    False);

  McpClientKeys[0] := 'claude-desktop';
  McpClientKeys[1] := 'claude-code';
  McpClientKeys[2] := 'antigravity';
  McpClientKeys[3] := 'gemini-cli';
  McpClientKeys[4] := 'cursor';
  McpClientKeys[5] := 'windsurf';
  McpClientKeys[6] := 'vscode';
  McpClientKeys[7] := 'codex';

  McpClientsPage.Add('Claude Desktop (AppData / Microsoft Store)');
  McpClientsPage.Add('Claude Code (~/.claude.json)');
  McpClientsPage.Add('Antigravity (~/.gemini/antigravity/mcp_config.json)');
  McpClientsPage.Add('Gemini CLI (~/.gemini/settings.json)');
  McpClientsPage.Add('Cursor (~/.cursor/mcp.json)');
  McpClientsPage.Add('Windsurf (~/.codeium/windsurf/mcp_config.json)');
  McpClientsPage.Add('VS Code (AppData/Code/User/mcp.json)');
  McpClientsPage.Add('Codex CLI (~/.codex/config.toml)');

  McpClientsPage.Values[0] := DirExists(ExpandConstant('{userappdata}\Claude')) or HasClaudeMsixFolder();
  McpClientsPage.Values[1] := FileExists(ExpandConstant('{%USERPROFILE}\.claude.json')) or DirExists(ExpandConstant('{%USERPROFILE}\.claude'));
  McpClientsPage.Values[2] := DirExists(ExpandConstant('{%USERPROFILE}\.gemini\antigravity'));
  McpClientsPage.Values[3] := DirExists(ExpandConstant('{%USERPROFILE}\.gemini'));
  McpClientsPage.Values[4] := DirExists(ExpandConstant('{%USERPROFILE}\.cursor'));
  McpClientsPage.Values[5] := DirExists(ExpandConstant('{%USERPROFILE}\.codeium\windsurf'));
  McpClientsPage.Values[6] := DirExists(ExpandConstant('{userappdata}\Code\User'));
  McpClientsPage.Values[7] := DirExists(ExpandConstant('{%USERPROFILE}\.codex'));
end;

procedure ConfigureSelectedMcpClients;
var
  I, ResultCode: Integer;
  SelectedCsv, ScriptPath, ServerExe, SummaryPath, Params: string;
  SummaryBytes: AnsiString;
begin
  ConfiguredMcpList := '';
  ScriptPath := ExpandConstant('{app}\scripts\configure-mcp-clients.ps1');
  if not FileExists(ScriptPath) then
    Exit;

  ServerExe := ExpandConstant('{app}\server\scio-revit-mcp.exe');
  SummaryPath := ExpandConstant('{tmp}\sutra-mcp-configured.txt');
  if FileExists(SummaryPath) then DeleteFile(SummaryPath);

  SelectedCsv := '';
  for I := 0 to 7 do
  begin
    if McpClientsPage.Values[I] then
    begin
      if SelectedCsv <> '' then SelectedCsv := SelectedCsv + ',';
      SelectedCsv := SelectedCsv + McpClientKeys[I];
    end;
  end;

  if SelectedCsv = '' then
  begin
    if WizardSilent then
      Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -ServerExe "' + ServerExe + '" -AutoDetect -SummaryFile "' + SummaryPath + '"'
    else
      Exit;
  end
  else
  begin
    Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" -ServerExe "' + ServerExe + '" -Clients "' + SelectedCsv + '" -SummaryFile "' + SummaryPath + '"';
  end;

  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;

  if FileExists(SummaryPath) and LoadStringFromFile(SummaryPath, SummaryBytes) then
    ConfiguredMcpList := Trim(string(SummaryBytes));

  if (ConfiguredMcpList = '') and (ResultCode <> 0) then
    ConfiguredMcpList := 'error (code ' + IntToStr(ResultCode) + ') — run scripts\configure-mcp-clients.ps1';
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

    DeployCadBundle;
    ConfigureSelectedMcpClients;

    SaveStringToFile(ExpandConstant('{app}\version.txt'), '{#AppVersion}' + #13#10, False);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  ServerExe, YearsDisplay, CadYearsDisplay, McpDisplay: string;
begin
  if CurPageID = wpFinished then
  begin
    ServerExe := ExpandConstant('{app}\server\scio-revit-mcp.exe');
    if InstalledYearsList = '' then
      YearsDisplay := CustomMessage('NoRevitFound')
    else
      YearsDisplay := InstalledYearsList;

    if InstalledCadYearsList = '' then
      CadYearsDisplay := CustomMessage('NoCadFound')
    else
      CadYearsDisplay := InstalledCadYearsList;

    if ConfiguredMcpList = '' then
      McpDisplay := CustomMessage('NoMcpConfigured')
    else
      McpDisplay := ConfiguredMcpList;

    if PreviousVersion <> '' then
      WizardForm.FinishedLabel.Caption :=
        FmtMessage(CustomMessage('FinishedUpgrade'), [PreviousVersion, '{#AppVersion}', ServerExe, YearsDisplay, CadYearsDisplay, McpDisplay])
    else
      WizardForm.FinishedLabel.Caption :=
        FmtMessage(CustomMessage('FinishedInstall'), ['{#AppVersion}', ServerExe, YearsDisplay, CadYearsDisplay, McpDisplay]);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Years: array[0..4] of string;
  I: Integer;
  AddinRoot, PayloadDir, ManifestPath, CadBundleDir: string;
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
    CadBundleDir := ExpandConstant('{userappdata}\Autodesk\ApplicationPlugins\SutraCad.bundle');
    if DirExists(CadBundleDir) then DelTree(CadBundleDir, True, True, True);
  end;
end;

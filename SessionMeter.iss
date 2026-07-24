; SessionMeter installer — self-contained single-file console CLI, installed per-user and added to PATH.
; Built by build.ps1 (which syncs MyAppVersion from src\Session\Session.csproj).
; Self-contained: no .NET runtime detection needed. Per-user: no admin prompt.

#define MyAppName        "SessionMeter"
#define MyAppVersion     "0.6.0"
#define MyAppExeName     "Session.exe"
#define MyAppPublisher   "Pieter Sadie"
#define MyAppURL         "https://github.com/dbDez/SessionMeter"

[Setup]
AppId={{18997EFA-DB86-4CCB-BEAF-9FA26703B343}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppSupportURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\SessionMeter
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=SessionMeter-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ChangesEnvironment=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName} {#MyAppVersion}
; Icon shown in the Setup.exe itself, Add/Remove Programs, and the wizard.
SetupIconFile=assets\session.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "bin\Release\publish-sc\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "assets\session.ico"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Add the install dir to the per-user PATH (idempotent via NeedsAddPath).
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
{ True only when the install dir is not already on the per-user PATH (case-insensitive, delimiter-safe). }
function NeedsAddPath(Param: String): Boolean;
var
  OrigPath: String;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(OrigPath) + ';') = 0;
end;

{ On uninstall, strip the install dir from the per-user PATH. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  OrigPath, AppDir, NewPath: String;
  P: Integer;
begin
  if CurUninstallStep <> usUninstall then exit;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', OrigPath) then exit;
  AppDir := ExpandConstant('{app}');
  NewPath := ';' + OrigPath + ';';
  { remove ";AppDir;" (case-insensitive) }
  P := Pos(';' + Uppercase(AppDir) + ';', Uppercase(NewPath));
  if P > 0 then
  begin
    Delete(NewPath, P, Length(AppDir) + 1);
    { trim the leading/trailing sentinel semicolons }
    if (Length(NewPath) > 0) and (NewPath[1] = ';') then Delete(NewPath, 1, 1);
    if (Length(NewPath) > 0) and (NewPath[Length(NewPath)] = ';') then Delete(NewPath, Length(NewPath), 1);
    RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewPath);
  end;
end;

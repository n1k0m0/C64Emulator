#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

#ifndef AppVersionInfo
#define AppVersionInfo "0.1.0.0"
#endif

#ifndef SourceDir
#define SourceDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
#define OutputDir "..\artifacts\installer"
#endif

#define AppGuid "E1A5D0A3-295D-40B2-8EAC-D75D8AB1B7B5"
#define AppUninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{E1A5D0A3-295D-40B2-8EAC-D75D8AB1B7B5}_is1"

[Setup]
AppId={{{#AppGuid}}}
AppName=C64 Emulator
AppVersion={#AppVersion}
AppVerName=C64 Emulator {#AppVersion}
AppPublisher=Nils Kopal
AppPublisherURL=https://github.com/n1k0m0/C64Emulator
AppSupportURL=https://github.com/n1k0m0/C64Emulator/issues
AppUpdatesURL=https://github.com/n1k0m0/C64Emulator/releases
DefaultDirName={autopf}\C64Emulator
DefaultGroupName=C64 Emulator
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=C64Emulator-{#AppVersion}-win-x64-setup
SetupIconFile=..\C64Emulator\commodore_logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UsedUserAreasWarning=no
UninstallDisplayIcon={app}\C64Emulator.exe
VersionInfoVersion={#AppVersionInfo}
VersionInfoCompany=Nils Kopal
VersionInfoDescription=C64 Emulator Setup
VersionInfoProductName=C64 Emulator
VersionInfoProductVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.bin,*.prg,*.d64,*.D64,*.PRG"
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\C64 Emulator"; Filename: "{app}\C64Emulator.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\C64 Emulator"; Filename: "{app}\C64Emulator.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\C64Emulator.exe"; Description: "{cm:LaunchProgram,C64 Emulator}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\C64Emulator"

[Code]
var
  PreviousInstallDir: string;

function QueryPreviousInstallDir(RootKey: Integer; var InstallDir: string): Boolean;
begin
  Result := RegQueryStringValue(RootKey, '{#AppUninstallKey}', 'InstallLocation', InstallDir);
end;

function FindPreviousInstallDir(var InstallDir: string): Boolean;
begin
  Result := QueryPreviousInstallDir(HKLM, InstallDir);
  if not Result then
  begin
    Result := QueryPreviousInstallDir(HKCU, InstallDir);
  end;
end;

function InitializeSetup(): Boolean;
var
  InstallDir: string;
  MessageText: string;
begin
  Result := True;
  PreviousInstallDir := '';

  if FindPreviousInstallDir(InstallDir) and DirExists(InstallDir) then
  begin
    MessageText :=
      'A previous C64 Emulator installation was found:' + #13#10 + #13#10 +
      InstallDir + #13#10 + #13#10 +
      'Do you want Setup to remove the old application files before installing this version?' + #13#10 + #13#10 +
      'This update cleanup does not remove your ROMs, saves, or settings in %APPDATA%\C64Emulator. ' +
      'The standalone uninstaller removes those user data files.';

    if MsgBox(MessageText, mbConfirmation, MB_YESNO) = IDYES then
    begin
      PreviousInstallDir := InstallDir;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and (PreviousInstallDir <> '') then
  begin
    DelTree(PreviousInstallDir, True, True, True);
    ForceDirectories(PreviousInstallDir);
  end;
end;

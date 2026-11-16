; The LiteView Enhanced installer (Inno Setup 6; tools/make-installer.ps1 builds it from the folder build.ps1 makes).
; For the user only, without administrator rights: LiteView goes to %LOCALAPPDATA%\Programs\LiteView Enhanced (it keeps
; its settings, login and caches next to LiteView.exe, which must be writable), with a Start menu shortcut, an optional
; desktop shortcut and an entry in Settings > Apps for uninstalling. A first install can copy the settings, login, chat
; history and watched videos from a LiteView folder used until now (the portable zip). Installing again over it
; updates the program and keeps the settings. Uninstalling removes the program and asks whether to remove the
; settings, login and caches too.
#define AppName "LiteView Enhanced"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #error Give the built folder: ISCC /DSourceDir=dist\LiteView-Enhanced-<version> LiteView.iss
#endif

[Setup]
AppId={{3D58953F-4A0E-4A04-BA05-A2B6EA695D4D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=LiteView Enhanced (a fork of LiteView for Twitch)
AppPublisherURL=https://github.com/Slurms602/LiteView-for-Twitch
DefaultDirName={localappdata}\Programs\LiteView Enhanced
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=LiteView-Enhanced-Setup-{#AppVersion}
SetupIconFile=..\src\app\liteview.ico
UninstallDisplayIcon={app}\LiteView.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "gamefirst.ini"; Flags: recursesubdirs createallsubdirs ignoreversion
; gamefirst.ini is the user's to edit: a reinstall does not overwrite it
Source: "{#SourceDir}\gamefirst.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\LiteView.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\LiteView.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\LiteView.exe"; Description: "Start LiteView now"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunName = 'LiteView Enhanced';
var
  OldPage: TInputDirWizardPage;

// a first install: the folder of the LiteView used until now, to copy its settings from (may stay empty)
procedure InitializeWizard;
begin
  OldPage := CreateInputDirPage(wpSelectTasks, 'Your settings', 'Copy your settings from the LiteView you used until now?',
    'If you used LiteView from a folder (the zip), choose that folder: its settings, login, chat preferences and ' +
    'watched videos are copied. Leave the box empty to start fresh.', False, '');
  OldPage.Add('LiteView folder (the one with LiteView.exe and liteview.ini):');
  OldPage.Values[0] := '';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // an update (LiteView is already installed here, with its settings): nothing to copy
  Result := False;
  if PageID = OldPage.ID then Result := FileExists(ExpandConstant('{app}\liteview.ini'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;
  if CurPageID = OldPage.ID then
  begin
    Dir := Trim(OldPage.Values[0]);
    if (Dir <> '') and not FileExists(AddBackslash(Dir) + 'liteview.ini') then
    begin
      MsgBox('That folder has no liteview.ini (LiteView''s settings). Choose the folder with LiteView.exe in it, or leave the box empty.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CopyIfThere(const FromDir, Name: String);
begin
  if FileExists(FromDir + Name) then
    if not FileCopy(FromDir + Name, ExpandConstant('{app}\') + Name, False) then
      Log('Could not copy ' + Name);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir, Old: String;
begin
  if CurStep <> ssPostInstall then exit;
  Dir := '';
  if Assigned(OldPage) then Dir := Trim(OldPage.Values[0]);
  if (Dir <> '') and (CompareText(AddBackslash(Dir), ExpandConstant('{app}\')) <> 0) and FileExists(AddBackslash(Dir) + 'liteview.ini') then
  begin
    Dir := AddBackslash(Dir);
    CopyIfThere(Dir, 'liteview.ini');
    CopyIfThere(Dir, 'liteview.token');   // encrypted for this Windows user (DPAPI): it works for the same user only
    CopyIfThere(Dir, 'liteview.emotes');
    CopyIfThere(Dir, 'liteview.videos');
    CopyIfThere(Dir, 'gamefirst.ini');
  end;
  // "Start with Windows" (LiteView's own Run entry) pointed at the old folder: now at the installed LiteView
  if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, RunName, Old) then
    RegWriteStringValue(HKEY_CURRENT_USER, RunKey, RunName, '"' + ExpandConstant('{app}\LiteView.exe') + '" --autostart');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, RunName);   // no start with Windows of a LiteView that is gone
  if CurUninstallStep <> usPostUninstall then exit;
  if DirExists(ExpandConstant('{app}')) and not UninstallSilent then
    if MsgBox('Also remove your LiteView settings, login, watched videos and caches (in ' + ExpandConstant('{app}') + ')?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{app}'), True, True, True);
end;

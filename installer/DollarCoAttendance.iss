; DOLLAR & Co Attendance -- the counter PC installer (Inno Setup 6).
;
; Built by `build.cmd /installer`, which compiles the app first and passes the
; build's folder, exe name and config file in:
;
;   ISCC.exe /DBinDir=<bin folder> /DExeName=<exe> /DConfigFile=<config.json> DollarCoAttendance.iss
;
; Run by hand with no /D values it packs ..\bin\ with the shipped config
; (..\desktopApp.config.json: the live POS server).
;
;   * installs to Program Files\DollarCo Attendance, Start-menu shortcut, desktop
;     shortcut (ticked) and "start with Windows" (ticked: it is the counter's app);
;   * the settings file is installed once and kept on upgrade, and the PC's
;     users may change it (the app's Settings window writes it beside the exe);
;   * says plainly when the DigitalPersona reader software is missing (it is
;     not bundled -- it is the reader maker's to distribute);
;   * uninstall keeps this PC's sign-in, offline copies and unsent punches
;     unless whoever uninstalls says otherwise.

#ifndef BinDir
  #define BinDir "..\bin"
#endif
#ifndef ExeName
  #define ExeName "DollarCoAttendance.exe"
#endif
#ifndef ConfigFile
  #define ConfigFile "..\desktopApp.config.json"
#endif

#define AppName "DOLLAR & Co Attendance"
#define AppVersion GetVersionNumbersString(BinDir + "\" + ExeName)

[Setup]
; Never change the AppId: it is how an upgrade finds the copy already installed.
AppId={{8D3C6F2E-4B1A-4E7D-9C5B-71A2F0D4E6B9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DOLLAR & Co
; No "&" in the folder: batch files and some tools trip over it.
DefaultDirName={autopf}\DollarCo Attendance
DefaultGroupName=DOLLAR & Co
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=DollarCo-Attendance-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; DOLLAR & Co logo (assets\, made by tools\make_brand.py from the website logo files).
WizardImageFile=..\assets\installer-side.bmp
WizardSmallImageFile=..\assets\installer-small.bmp
; The app is built for 64-bit Windows only (build.cmd: /platform:x64).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
; Close a running copy before replacing it (it holds the reader).
CloseApplications=yes
AppMutex=Local\DollarCoAttendance.SingleInstance

[Tasks]
Name: "desktopicon"; Description: "Put a shortcut on the desktop"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start the app when Windows starts (recommended for the counter PC)"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#BinDir}\{#ExeName}"; DestDir: "{app}"; Flags: ignoreversion
; The DigitalPersona SDK's .NET wrappers and matching engine, loaded from
; beside the exe. The reader's driver itself is the One Touch runtime (below).
Source: "{#BinDir}\DPFPDevNET.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\DPFPShrNET.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\DPFPEngNET.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\DPFPVerNET.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\dpHFtrEx.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\dpHMatch.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\sounds\*"; DestDir: "{app}\sounds"; Flags: ignoreversion recursesubdirs
; Installed only when there is none yet (an upgrade keeps the PC's settings),
; never removed on its own, and writable by the PC's users.
Source: "{#ConfigFile}"; DestDir: "{app}"; DestName: "desktopApp.config.json"; Flags: onlyifdoesntexist uninsneveruninstall; Permissions: users-modify

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: startup

[Run]
; As the person who started setup, not as the elevated installer: the saved
; sign-in and offline copies are encrypted for the Windows account the app runs as.
Filename: "{app}\{#ExeName}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  ReaderPage: TOutputMsgWizardPage;

// The One Touch for Windows runtime puts DPFPApi.dll in System32; without it
// the app shows "Fingerprint reader software not installed".
function ReaderSoftwareInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\DPFPApi.dll'));
end;

procedure InitializeWizard;
begin
  ReaderPage := CreateOutputMsgPage(wpSelectTasks,
    'Fingerprint reader software not found',
    'Attendance cannot be marked until it is installed.',
    'This PC does not have the DigitalPersona fingerprint reader software (its driver).' + #13#10 + #13#10 +
    'Staff clock in ONLY with their fingerprint, so the reader must work before the counter can be used. ' +
    'You can finish this setup now and install the reader software afterwards.' + #13#10 + #13#10 +
    'What to install: "DigitalPersona One Touch for Windows" runtime (RTE), 64-bit. It is not the browser "Lite Client".' + #13#10 + #13#10 +
    'Where to get it: the CD or USB stick that came with the reader, the shop that sold the reader, ' +
    'or HID Global''s driver downloads (www.hidglobal.com/drivers - search for "DigitalPersona One Touch").' + #13#10 + #13#10 +
    'Install it, restart the PC if it asks, then plug the reader into a USB port on the PC itself (not a hub).');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = ReaderPage.ID) and ReaderSoftwareInstalled;
end;

// Punches saved while offline and not sent yet: one queue.dat per server.
function UnsentPunchesExist: Boolean;
var
  FindRec: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{localappdata}\DollarCoAttendance\');
  if FindFirst(Base + 'offline-*', FindRec) then
  begin
    try
      repeat
        if FileExists(Base + FindRec.Name + '\queue.dat') then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Question: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if UninstallSilent then Exit;

  Question := 'Also delete this PC''s attendance data?' + #13#10 + #13#10 +
    'That is the settings file, the saved sign-in, the copy of the fingerprint and staff lists kept for working offline, and the log.' + #13#10 + #13#10 +
    'Choose No to keep them (recommended if you are reinstalling or upgrading).';
  if UnsentPunchesExist then
    Question := Question + #13#10 + #13#10 +
      'WARNING: there are punches saved while offline that have not been sent yet. Deleting loses them. ' +
      'Choose No, start the app once with internet, and they will be sent.';

  if MsgBox(Question, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(ExpandConstant('{localappdata}\DollarCoAttendance'), True, True, True);
    DeleteFile(ExpandConstant('{app}\desktopApp.config.json'));
    RemoveDir(ExpandConstant('{app}'));
  end;
end;

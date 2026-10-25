; EpikStreamer per-user installer (Inno Setup 6). Build with build-installer.ps1, or:
;   ISCC.exe /DAppVersion=2.2.1 /DSourceDir=..\dist\full EpikStreamer.iss
; Per-user (no admin): installs to %LOCALAPPDATA%\Programs\EpikStreamer. Profiles/keys in %APPDATA%\EpikStreamer are kept on uninstall.

#ifndef AppVersion
  #define AppVersion "2.2.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\full"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
; Define FrameworkDependent when packaging the framework-dependent build (then the .NET 8 Desktop Runtime is checked)
#define AppName "EpikStreamer"
#define AppExe "EpikStreamer.exe"

[Setup]
AppId={{6C1B7E8A-3F2D-4E59-9A7C-EB57A1C0D2F1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=EpikStreamer
AppPublisherURL=https://github.com/nano11b/EpikStreamer
AppUpdatesURL=https://github.com/nano11b/EpikStreamer/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
CloseApplications=yes
CloseApplicationsFilter={#AppExe},ffmpeg.exe,ffprobe.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; interactive install: offer to launch
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; silent install (= in-app auto-update): relaunch the app
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}\session"

[Code]
const
  WebView2Guid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  DotNetUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';

function WebView2Installed(): Boolean;
var v: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Guid, 'pv', v) and (v <> '') and (v <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Guid, 'pv', v) and (v <> '') and (v <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Guid, 'pv', v) and (v <> '') and (v <> '0.0.0.0'));
end;

#ifdef FrameworkDependent
function DesktopRuntime8Installed(): Boolean;
var FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;
#endif

function InitializeSetup(): Boolean;
var ErrorCode: Integer;
begin
  Result := True;
#ifdef FrameworkDependent
  if not DesktopRuntime8Installed() then
  begin
    if WizardSilent() then begin Result := False; exit; end;
    if MsgBox('EpikStreamer needs the .NET 8 Desktop Runtime (x64), which was not found.' #13#10#13#10 +
              'Open the download page now? Setup will exit; run it again after installing the runtime.', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNetUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
    exit;
  end;
#endif
  if (not WebView2Installed()) and (not WizardSilent()) then
  begin
    if MsgBox('Microsoft Edge WebView2 Runtime was not found. It is only needed for "Sign in to EpikChat" and the chat panel ' +
              '(streaming works without it). It is preinstalled on Windows 11 and most Windows 10 PCs.' #13#10#13#10 +
              'Open the WebView2 download page?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', WebView2Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

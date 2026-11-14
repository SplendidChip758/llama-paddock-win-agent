; The Windows setup: llama-paddock-agent-setup.exe. Built by .github/scripts/build-setup.ps1, which
; publishes the agent and the tray app into build\out first:
;   ISCC /DAppVersion=1.2.3 setup\llama-paddock-agent.iss
;
; The wizard only asks; paddock-agent.exe does the work (install, uninstall, detect, stop), so the
; same steps run in the silent mode the CI test and later updates use:
;   llama-paddock-agent-setup.exe /VERYSILENT /SUPPRESSMSGBOXES [/OLLAMAUSER=<name or SID>] [/PORT=11435]
;                                 [/ALLOWFROM=192.168.30.0/24] [/NOFIREWALL=1]
;   unins000.exe /VERYSILENT [/KEEPDATA=1]
; A silent install that couldn't start the agent exits with code 1; the reason is in the setup log (/LOG=<file>).

; ExecAndLogOutput passes the installer's progress to the wizard
#if Ver < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is needed
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Windows' file version takes numbers only: 0.11.0-dev.2 → 0.11.0
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)
#define Out "..\build\out"
#define Agent "paddock-agent.exe"
#define Tray "paddock-agent-tray.exe"

[Setup]
; never change: Windows finds the installed agent by it, for upgrades and uninstall
AppId={{6F1B7C1E-3D52-4B8A-9C0E-7A2D5E4B9F31}
AppName=llama-paddock agent
AppVersion={#AppVersion}
AppVerName=llama-paddock agent {#AppVersion}
AppPublisher=llama-paddock
AppPublisherURL=https://llama-paddock.com
AppSupportURL=https://github.com/SplendidChip758/llama-paddock/issues
AppUpdatesURL=https://github.com/SplendidChip758/llama-paddock-win-agent/releases
DefaultDirName={autopf}\llama-paddock-agent
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=llama-paddock-agent-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\logo\paddock-agent.ico
CloseApplications=no
UninstallDisplayName=llama-paddock agent
UninstallDisplayIcon={app}\{#Agent}
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=llama-paddock agent

[Files]
; both programs and the .NET runtime they share
Source: "{#Out}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

; to start the tray app again after closing it from its menu
[Icons]
Name: "{autoprograms}\llama-paddock agent"; Filename: "{app}\{#Tray}"

[Code]
var
  UserPage: TInputOptionWizardPage;
  NetworkPage: TInputQueryWizardPage;
  FirewallBox: TNewCheckBox;
  ResultPage: TOutputMsgMemoWizardPage;
  Installed, InstallOk: Boolean;
  Legacy, DetectedUser: String;
  UserSids: TStringList;

function Agent(Args: String; ResultFile: String; Dir: String): Boolean;
var
  Code: Integer;
begin
  DeleteFile(ResultFile);
  Result := Exec(Dir + '\{#Agent}', Args + ' --result ' + AddQuotes(ResultFile), '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

// `step: …` lines from the installer become the wizard's status; everything goes to the setup log
procedure OnInstallerOutput(const S: String; const Error, FirstLine: Boolean);
begin
  Log('installer: ' + S);
  if Copy(S, 1, 6) = 'step: ' then WizardForm.StatusLabel.Caption := Copy(S, 7, Length(S));
end;

function Res(Key: String; FileName: String): String;
begin
  Result := GetIniString('result', Key, '', FileName);
end;

function InitializeSetup(): Boolean;
var
  F, Users: String;
begin
  // the agent needs the runtime next to it, so detect runs from a copy of the program folder. The
  // pattern is matched against the entries' DestDir as written, and the copy keeps that path:
  // {tmp}\{app}\, with "{app}" a folder name
  ExtractTemporaryFiles('{app}\*');
  F := ExpandConstant('{tmp}\detect.ini');
  if not Agent('detect', F, ExpandConstant('{tmp}') + '\{app}') then Log('detect failed: ' + Res('error', F));
  Installed := Res('installed', F) = '1';
  Legacy := Res('legacy', F);
  DetectedUser := Res('user', F);
  UserSids := TStringList.Create;
  // "SID:DOMAIN\name | SID:DOMAIN\name"
  Users := Res('users', F);
  StringChangeEx(Users, ' | ', #13#10, True);
  UserSids.Text := Users;
  Result := True;
end;

procedure InitializeWizard();
var
  Intro: String;
  I, Colon: Integer;
  Users: TStringList;
begin
  if Installed then
    Intro := 'The agent is already installed: this updates it. Its token, HTTPS key and settings are kept.'
  else if Legacy <> '' then
    Intro := 'The Node.js agent was found (' + Legacy + '). It will be replaced by the Windows service. ' +
      'Its token, HTTPS key and settings are kept, so the dashboard needs no change.'
  else
    Intro := 'This installs the llama-paddock agent, which reports this PC and its Ollama to your llama-paddock dashboard.';

  UserPage := CreateInputOptionPage(wpWelcome, 'Ollama', 'Who runs Ollama on this PC?',
    Intro + #13#10#13#10 +
    'The agent runs as a Windows service and reads this user''s Ollama log, models and settings. ' +
    'Ollama keeps running as it does now; a small tray app in that user''s session restarts it for the dashboard.',
    True, False);
  Users := TStringList.Create;
  Users.Text := UserSids.Text;
  UserSids.Clear;
  for I := 0 to Users.Count - 1 do begin
    Colon := Pos(':', Users[I]);
    if Colon = 0 then Continue;
    UserSids.Add(Copy(Users[I], 1, Colon - 1));
    UserPage.Add(Copy(Users[I], Colon + 1, Length(Users[I])));
    if Copy(Users[I], 1, Colon - 1) = DetectedUser then UserPage.SelectedValueIndex := UserSids.Count - 1;
  end;
  if (UserSids.Count > 0) and (UserPage.SelectedValueIndex < 0) then UserPage.SelectedValueIndex := 0;

  NetworkPage := CreateInputQueryPage(UserPage.ID, 'Network', 'How does the dashboard reach this PC?',
    'The dashboard connects to the agent on this port. The firewall rule allows it from these addresses ' +
    '(a subnet such as 192.168.30.0/24, or LocalSubnet), on Private and Domain networks.');
  NetworkPage.Add('Agent port:', False);
  NetworkPage.Add('Allow connections from:', False);
  NetworkPage.Values[0] := ExpandConstant('{param:PORT|11435}');
  NetworkPage.Values[1] := ExpandConstant('{param:ALLOWFROM|LocalSubnet}');
  FirewallBox := TNewCheckBox.Create(NetworkPage);
  FirewallBox.Parent := NetworkPage.Surface;
  FirewallBox.Top := NetworkPage.Edits[1].Top + NetworkPage.Edits[1].Height + ScaleY(16);
  FirewallBox.Width := NetworkPage.SurfaceWidth;
  FirewallBox.Caption := 'Add a Windows Firewall rule for the agent port';
  FirewallBox.Checked := ExpandConstant('{param:NOFIREWALL|0}') = '0';

  ResultPage := CreateOutputMsgMemoPage(wpInstalling, 'Add this PC to the dashboard', '', '', '');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // an update keeps its user; an update or a move keeps the port and the firewall rule
  Result := ((PageID = UserPage.ID) and Installed) or ((PageID = NetworkPage.ID) and (Installed or (Legacy <> '')));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  // the installer also finds the user from the Node.js agent's folder, or takes /OLLAMAUSER
  if (CurPageID = UserPage.ID) and (UserSids.Count = 0) and (Legacy = '') and (ExpandConstant('{param:OLLAMAUSER}') = '') then begin
    MsgBox('Ollama isn''t installed for any user on this PC. Install it from https://ollama.com and start it once, then run this setup again.', mbError, MB_OK);
    Result := False;
  end else if CurPageID = NetworkPage.ID then begin
    Port := StrToIntDef(NetworkPage.Values[0], 0);
    if (Port < 1) or (Port > 65535) or (Port = 11434) then begin
      MsgBox('The port must be a number from 1 to 65535, and not Ollama''s 11434.', mbError, MB_OK);
      Result := False;
    end else if Trim(NetworkPage.Values[1]) = '' then begin
      MsgBox('Enter the addresses to allow, such as LocalSubnet or 192.168.30.0/24.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // the running service and the tray apps hold their programs open
  if Installed and not Agent('stop', ExpandConstant('{tmp}\stop.ini'), ExpandConstant('{app}')) then
    Result := 'Couldn''t stop the running agent: ' + Res('error', ExpandConstant('{tmp}\stop.ini'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Args, F, Url, Others, User, Error: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Setting up the agent...';
  F := ExpandConstant('{tmp}\install.ini');
  Args := 'install';
  User := ExpandConstant('{param:OLLAMAUSER}');
  if (User = '') and not WizardSilent and not Installed and (UserPage.SelectedValueIndex >= 0) then
    User := UserSids[UserPage.SelectedValueIndex];
  if User <> '' then Args := Args + ' --ollama-user ' + AddQuotes(User);
  if not Installed and (Legacy = '') then begin
    Args := Args + ' --port ' + Trim(NetworkPage.Values[0]) + ' --allow-from ' + AddQuotes(Trim(NetworkPage.Values[1]));
    if not FirewallBox.Checked then Args := Args + ' --no-firewall';
  end;
  DeleteFile(F);
  InstallOk := ExecAndLogOutput(ExpandConstant('{app}\{#Agent}'), Args + ' --result ' + AddQuotes(F), '', SW_HIDE,
    ewWaitUntilTerminated, Code, @OnInstallerOutput) and (Code = 0);
  if InstallOk then begin
    // the tray app belongs in the user's session, not this elevated one; it also starts at every sign-in
    ExecAsOriginalUser(ExpandConstant('{app}\{#Tray}'), '', '', SW_SHOWNORMAL, ewNoWait, Code);
    ResultPage.Description := 'The agent is running.';
    Url := Res('url', F);
    Others := Res('addresses', F);
    // the first address is in the URL; the others are worth knowing on a PC with several networks
    if Pos(' | ', Others) > 0 then
      Others := #13#10 + '(This PC''s other addresses: ' + Copy(Others, Pos(' | ', Others) + 3, Length(Others)) + ')'
    else
      Others := '';
    if Res('token', F) <> '' then
      ResultPage.RichEditViewer.Text :=
        'In the dashboard, choose Add host and enter:' + #13#10#13#10 +
        'Agent URL:  ' + Url + Others + #13#10 +
        'Agent token:  ' + Res('token', F) + #13#10 +
        'Key fingerprint:  ' + Res('fingerprint', F) + #13#10#13#10 +
        'Copy them from here: the token isn''t shown again. It is stored in ' + ExpandConstant('{commonappdata}') + '\llama-paddock-agent\agent.json, which only administrators can read.'
    else
      ResultPage.RichEditViewer.Text :=
        'The token and the HTTPS key were kept, so there is nothing to change in the dashboard.' + #13#10#13#10 +
        'Agent URL:  ' + Url + Others + #13#10 +
        'Key fingerprint:  ' + Res('fingerprint', F);
    ResultPage.RichEditViewer.Text := ResultPage.RichEditViewer.Text + #13#10#13#10 +
      'Ollama user: ' + Res('user', F) + '. The tray app restarts Ollama for the dashboard while they''re signed in.';
  end else begin
    Error := Res('error', F);
    if Error = '' then Error := 'the installer step failed without a message';
    ResultPage.Description := 'The agent could not be started.';
    ResultPage.RichEditViewer.Text := Error;
    Log('install failed: ' + Error);
  end;
  if Res('warnings', F) <> '' then
    ResultPage.RichEditViewer.Text := ResultPage.RichEditViewer.Text + #13#10#13#10 + 'Note: ' + Res('warnings', F);
end;

// a silent run reports a failed install through its exit code
function GetCustomSetupExitCode(): Integer;
begin
  if InstallOk then Result := 0 else Result := 1;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Args: String;
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Args := 'uninstall';
  if UninstallSilent then begin
    if ExpandConstant('{param:KEEPDATA|0}') <> '0' then Args := Args + ' --keep-data';
  end else if MsgBox('Keep the agent''s token and HTTPS key, so a later install is the same host to the dashboard?', mbConfirmation, MB_YESNO) = IDYES then
    Args := Args + ' --keep-data';
  Exec(ExpandConstant('{app}\{#Agent}'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

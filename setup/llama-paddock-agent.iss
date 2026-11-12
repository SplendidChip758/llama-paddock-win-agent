; The Windows setup: llama-paddock-agent-setup.exe. Built by .github/scripts/build-setup.ps1, which
; puts the service program and Node.js into build\out first:
;   ISCC /DAppVersion=1.2.3 setup\llama-paddock-agent.iss
;
; The wizard only asks; paddock-agent-service.exe does the work (install, uninstall, detect, stop),
; so the same steps run in the silent mode the CI test and later updates use:
;   llama-paddock-agent-setup.exe /VERYSILENT /SUPPRESSMSGBOXES [/PORT=11435] [/ALLOWFROM=192.168.30.0/24]
;                                 [/OLLAMAEXE=<path to ollama.exe>] [/NOFIREWALL=1]
;   unins000.exe /VERYSILENT [/KEEPDATA=1]
; A silent install that couldn't start the agent exits with code 1; the reason is in the setup log (/LOG=<file>).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Windows' file version takes numbers only: 0.11.0-dev.2 → 0.11.0
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)
#define Out "..\build\out"
#define Svc "paddock-agent-service.exe"

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
CloseApplications=no
UninstallDisplayName=llama-paddock agent
UninstallDisplayIcon={app}\{#Svc}
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=llama-paddock agent

[Files]
Source: "{#Out}\service\{#Svc}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Out}\service\{#Svc}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Out}\node\node.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Out}\node\LICENSE"; DestDir: "{app}"; DestName: "node-LICENSE.txt"; Flags: ignoreversion
Source: "..\paddock-agent.mjs"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Code]
var
  OllamaPage: TInputFileWizardPage;
  NetworkPage: TInputQueryWizardPage;
  FirewallBox: TNewCheckBox;
  ResultPage: TOutputMsgMemoWizardPage;
  Installed, InstallOk: Boolean;
  Legacy, DetectedOllama, InstallError: String;

function Svc(Args: String; ResultFile: String; Dir: String): Boolean;
var
  Code: Integer;
begin
  DeleteFile(ResultFile);
  Result := Exec(Dir + '\{#Svc}', Args + ' --result ' + AddQuotes(ResultFile), '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function Res(Key: String; FileName: String): String;
begin
  Result := GetIniString('result', Key, '', FileName);
end;

function InitializeSetup(): Boolean;
var
  F: String;
begin
  ExtractTemporaryFile('{#Svc}');
  ExtractTemporaryFile('{#Svc}.config');
  F := ExpandConstant('{tmp}\detect.ini');
  Svc('detect', F, ExpandConstant('{tmp}'));
  Installed := Res('installed', F) = '1';
  Legacy := Res('legacy', F);
  DetectedOllama := Res('ollama', F);
  Result := True;
end;

procedure InitializeWizard();
var
  Intro: String;
begin
  if Installed then
    Intro := 'The agent is already installed: this updates it. Its token, HTTPS key and settings are kept.'
  else if Legacy <> '' then
    Intro := 'An agent that runs as a scheduled task was found (' + Legacy + '). It will be moved to the Windows service. ' +
      'Its token, HTTPS key and Ollama settings are kept, so the dashboard needs no change.'
  else
    Intro := 'This installs the llama-paddock agent as a Windows service. It runs Ollama itself and reports this PC to your llama-paddock dashboard, whether or not anyone is signed in.';

  OllamaPage := CreateInputFilePage(wpWelcome, 'Ollama', 'Which Ollama should the agent run?',
    Intro + #13#10#13#10 +
    'The service starts ollama serve from this install. The Ollama tray app no longer starts when you sign in, ' +
    'so the two don''t compete for port 11434; uninstalling the agent turns it back on.');
  OllamaPage.Add('ollama.exe:', 'ollama.exe|ollama.exe', '.exe');
  OllamaPage.Values[0] := ExpandConstant('{param:OLLAMAEXE}');
  if OllamaPage.Values[0] = '' then OllamaPage.Values[0] := DetectedOllama;

  NetworkPage := CreateInputQueryPage(OllamaPage.ID, 'Network', 'How does the dashboard reach this PC?',
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
  // an update or a move keeps the port and the firewall rule
  Result := (PageID = NetworkPage.ID) and (Installed or (Legacy <> ''));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = OllamaPage.ID then begin
    if not FileExists(OllamaPage.Values[0]) or (CompareText(ExtractFileName(OllamaPage.Values[0]), 'ollama.exe') <> 0) then begin
      MsgBox('Choose ollama.exe. If Ollama isn''t installed yet, install it from https://ollama.com first, then run this setup again.', mbError, MB_OK);
      Result := False;
    end;
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
  // the running service holds its exe and node.exe open
  if Installed and not Svc('stop', ExpandConstant('{tmp}\stop.ini'), ExpandConstant('{tmp}')) then
    Result := 'Couldn''t stop the running agent: ' + Res('error', ExpandConstant('{tmp}\stop.ini'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Args, F: String;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Starting the agent...';
  F := ExpandConstant('{tmp}\install.ini');
  Args := 'install --ollama-exe ' + AddQuotes(OllamaPage.Values[0]);
  if not Installed and (Legacy = '') then begin
    Args := Args + ' --port ' + Trim(NetworkPage.Values[0]) + ' --allow-from ' + AddQuotes(Trim(NetworkPage.Values[1]));
    if not FirewallBox.Checked then Args := Args + ' --no-firewall';
  end;
  InstallOk := Svc(Args, F, ExpandConstant('{app}'));
  if InstallOk then begin
    ResultPage.Description := 'The agent is running.';
    if Res('token', F) <> '' then
      ResultPage.RichEditViewer.Text :=
        'In the dashboard, choose Add host and enter:' + #13#10#13#10 +
        'Agent URL:  https://<this PC''s address>:' + Res('port', F) + #13#10 +
        'Agent token:  ' + Res('token', F) + #13#10 +
        'Key fingerprint:  ' + Res('fingerprint', F) + #13#10#13#10 +
        'Copy them from here: the token isn''t shown again. It is stored in ' + ExpandConstant('{commonappdata}') + '\llama-paddock-agent\agent.json, which only administrators can read.'
    else
      ResultPage.RichEditViewer.Text :=
        'The token and the HTTPS key were kept, so there is nothing to change in the dashboard.' + #13#10#13#10 +
        'Key fingerprint:  ' + Res('fingerprint', F);
  end else begin
    InstallError := Res('error', F);
    if InstallError = '' then InstallError := 'the installer step failed without a message';
    ResultPage.Description := 'The agent could not be started.';
    ResultPage.RichEditViewer.Text := InstallError + #13#10#13#10 +
      'Logs: ' + ExpandConstant('{commonappdata}') + '\llama-paddock-agent\logs';
  end;
  if Res('warnings', F) <> '' then
    ResultPage.RichEditViewer.Text := ResultPage.RichEditViewer.Text + #13#10#13#10 + 'Note: ' + Res('warnings', F);
  if not InstallOk then Log('install failed: ' + InstallError);
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
  Exec(ExpandConstant('{app}\{#Svc}'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

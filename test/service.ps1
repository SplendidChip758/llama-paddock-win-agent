# End-to-end test of the setup and the service, as an administrator in PowerShell 7 (CI's windows-latest):
#   pwsh test\service.ps1 -Setup dist\llama-paddock-agent-setup.exe -FakeOllama <fake ollama.exe>
# Fakes a running install of the Node agent (a scheduled task), installs over it silently and checks
# that the token and the HTTPS key came along. Then the service: this user's files and variables
# read as SYSTEM, the tray app's pipe between SYSTEM and the user, nothing changed without the tray
# app, and Ollama started as the user by it. Then an upgrade, and that uninstalling leaves nothing.
# Changes the machine (a service, %ProgramData%, this user's variables, every ollama.exe stopped):
# run it on a throwaway one.
param(
  [Parameter(Mandatory)][string]$Setup,
  [Parameter(Mandatory)][string]$FakeOllama
)
$ErrorActionPreference = 'Stop'
$Setup = (Resolve-Path $Setup).Path
$FakeOllama = (Resolve-Path $FakeOllama).Path
$repo = Split-Path $PSScriptRoot
$name = 'llama-paddock-agent'
$prog = Join-Path $env:ProgramFiles 'llama-paddock-agent'
$data = Join-Path $env:ProgramData 'llama-paddock-agent'
$legacy = Join-Path $env:LOCALAPPDATA 'llama-paddock-agent'
$port = 11435
$ollamaHost = '127.0.0.1:11545'
$token = 'legacy-token-' + [Guid]::NewGuid().ToString('N')
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$failures = [Collections.Generic.List[string]]::new()

function Check([string]$what, [bool]$ok, [string]$detail = '') {
  if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what $detail"; $failures.Add($what) }
}
function WaitFor([int]$seconds, [scriptblock]$condition) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) { try { if (& $condition) { return $true } } catch {}; Start-Sleep -Milliseconds 500 }
  return $false
}
function Health { Invoke-RestMethod "https://127.0.0.1:$port/health" -SkipCertificateCheck -TimeoutSec 3 }
function Agent([string]$method, [string]$path, $body = $null) {
  $params = @{ Method = $method; Uri = "https://127.0.0.1:$port$path"; Headers = @{ Authorization = "Bearer $token" }
    SkipCertificateCheck = $true; SkipHttpErrorCheck = $true; TimeoutSec = 90 }
  if ($null -ne $body) { $params.Body = ($body | ConvertTo-Json -Depth 5 -Compress); $params.ContentType = 'application/json' }
  $r = Invoke-WebRequest @params
  [pscustomobject]@{ Status = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json -AsHashtable) }
}
function OllamaProcesses {
  @(Get-CimInstance Win32_Process -Filter "Name='ollama.exe'" | ForEach-Object {
    [pscustomobject]@{ Id = $_.ProcessId; Parent = $_.ParentProcessId; CommandLine = $_.CommandLine; Owner = (Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid).Sid }
  })
}
function Trays { @(Get-Process paddock-agent-tray -ErrorAction SilentlyContinue) }
function RunSetup([string]$exe, [string[]]$more = @()) {
  $log = Join-Path $env:RUNNER_TEMP "setup-$([Guid]::NewGuid().ToString('N').Substring(0, 6)).log"
  # not -Wait: that waits for every process it starts too, and the setup starts the tray app
  $p = Start-Process $exe -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $more) -PassThru
  if (-not $p.WaitForExit(300000)) { Write-Host "the setup didn't finish in 5 minutes"; Get-Content $log -Tail 40 | Write-Host; return -1 }
  if ($p.ExitCode -ne 0) { Get-Content $log -Tail 40 | Write-Host }
  $p.ExitCode
}

try {
  # --- this user's Ollama: the fake one, and their variables -------------------
  [Environment]::SetEnvironmentVariable('OLLAMA_HOST', $ollamaHost, 'User')
  [Environment]::SetEnvironmentVariable('OLLAMA_PADDOCK_TEST_A', 'from-the-user', 'User')
  $env:OLLAMA_HOST = $ollamaHost
  Start-Process $FakeOllama -ArgumentList serve -WindowStyle Hidden
  Check 'the fake Ollama answers' (WaitFor 20 { (Invoke-WebRequest "http://$ollamaHost/api/version").StatusCode -eq 200 })

  # --- the Node agent, as its installer leaves it, running --------------------
  Write-Host "==> Node agent in $legacy"
  New-Item -ItemType Directory -Force $legacy | Out-Null
  Copy-Item -Force (Join-Path $repo 'test\legacy\paddock-agent.mjs') $legacy
  [IO.File]::WriteAllText((Join-Path $legacy 'agent.json'), (@{
    PADDOCK_AGENT_TOKEN = $token; PADDOCK_AGENT_BIND = '0.0.0.0'; PADDOCK_AGENT_PORT = "$port"; PADDOCK_AGENT_HTTP = 'off'
  } | ConvertTo-Json), (New-Object Text.UTF8Encoding $true))  # with a BOM, as old installers wrote it
  $action = New-ScheduledTaskAction -Execute 'node.exe' -Argument "`"$legacy\paddock-agent.mjs`""
  Register-ScheduledTask -TaskName $name -Action $action -Trigger (New-ScheduledTaskTrigger -AtLogOn) -Force | Out-Null
  Start-Process node.exe -ArgumentList "`"$legacy\paddock-agent.mjs`"" -WindowStyle Hidden
  Check 'the Node agent answers' (WaitFor 20 { (Health).ok })
  $fingerprint = (Health).fingerprint

  # --- install over it ----------------------------------------------------------
  Write-Host '==> setup'
  Check 'the setup succeeds' ((RunSetup $Setup) -eq 0)
  $h = Health
  Check 'the service answers' ($h.ok -and $h.version -ne '0.10.0-dev.1') ($h | ConvertTo-Json)
  Check 'the HTTPS key came along' ($h.fingerprint -eq $fingerprint) "$($h.fingerprint) vs $fingerprint"
  Check 'the token came along' ((Agent GET /env).Status -eq 200)
  $agentJson = Join-Path $data 'agent.json'
  Check 'the Ollama user is recorded' ((Test-Path $agentJson) -and (Get-Content -Raw $agentJson | ConvertFrom-Json).PADDOCK_OLLAMA_USER_SID -eq $sid)
  Check 'the scheduled task is gone' (-not (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue))
  Check 'the Node agent is gone' (-not (Test-Path $legacy) -and -not (Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object CommandLine -like '*paddock-agent.mjs*'))
  Check 'the service runs as LocalSystem' ((Get-CimInstance Win32_Service -Filter "Name='$name'").StartName -eq 'LocalSystem')
  Check 'the tray app starts at sign-in' ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').'llama-paddock agent' -like "*$prog*")
  Check 'the setup started the tray app' (WaitFor 10 { (Trays).Count -gt 0 })
  $log = Join-Path $data 'logs\agent.log'
  Check 'the service logs to agent.log' ((Test-Path $log) -and (Select-String -Path $log -Pattern 'listening on' -Quiet))
  $acl = (Get-Acl $data).Access | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique
  Check 'only SYSTEM and administrators can read the data folder' (-not (Compare-Object $acl @('S-1-5-18', 'S-1-5-32-544'))) ($acl -join ', ')

  # --- as SYSTEM: this user's files and variables -------------------------------
  $envState = Agent GET /env
  Check 'GET /env reads the user''s variables as SYSTEM' ($envState.Status -eq 200 -and $envState.Body.managed.OLLAMA_PADDOCK_TEST_A -eq 'from-the-user') ($envState | ConvertTo-Json -Depth 5)
  $stats = Agent GET /stats
  Check 'GET /stats finds the user''s Ollama' ($stats.Status -eq 200 -and $null -ne $stats.Body.system.ollama_process) ($stats.Body.system.ollama_process | ConvertTo-Json)
  Check 'GET /logs answers' ((Agent GET /logs).Status -eq 200)

  # --- without the tray app: nothing is stopped or written ----------------------
  Trays | Stop-Process -Force
  $before = OllamaProcesses
  $r = Agent POST /service/restart
  Check 'restart without the tray app answers 409' ($r.Status -eq 409) ($r | ConvertTo-Json -Depth 5)
  Check 'restart without the tray app stops nothing' (-not (Compare-Object @($before.Id) @((OllamaProcesses).Id)))
  $r = Agent PUT /env @{ vars = $envState.Body.managed + @{ OLLAMA_PADDOCK_TEST_B = '1' }; restart = $false }
  Check 'PUT /env without the tray app answers 409' ($r.Status -eq 409) ($r | ConvertTo-Json -Depth 5)
  Check 'PUT /env without the tray app writes nothing' ($null -eq [Environment]::GetEnvironmentVariable('OLLAMA_PADDOCK_TEST_B', 'User'))

  # --- through the tray app, which serves SYSTEM only ---------------------------
  $tray = Start-Process (Join-Path $prog 'paddock-agent-tray.exe') -ArgumentList --headless -PassThru
  Check 'the tray app serves the service' (WaitFor 20 { (Agent PUT /env @{ vars = $envState.Body.managed; restart = $false }).Status -eq 200 })
  $r = Agent PUT /env @{ vars = $envState.Body.managed + @{ OLLAMA_PADDOCK_TEST_B = '1' }; restart = $false }
  Check 'PUT /env through the tray app' ($r.Status -eq 200 -and [Environment]::GetEnvironmentVariable('OLLAMA_PADDOCK_TEST_B', 'User') -eq '1') ($r | ConvertTo-Json -Depth 5)

  # no desktop app at the default path: the agent runs `ollama.exe serve` from that folder
  $cfgPath = Join-Path $data 'agent.json'
  $cfg = Get-Content -Raw $cfgPath | ConvertFrom-Json -AsHashtable
  $cfg.PADDOCK_OLLAMA_APP = Join-Path (Split-Path $FakeOllama) 'ollama app.exe'
  [IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
  Restart-Service $name
  Check 'the service answers after a restart' (WaitFor 30 { (Health).ok })
  $before = OllamaProcesses
  $r = Agent POST /service/restart
  $after = OllamaProcesses
  $server = $after | Where-Object { $_.CommandLine -match ' serve$' } | Select-Object -First 1
  Check 'restart answers ok' ($r.Status -eq 200 -and $r.Body.ok) ($r | ConvertTo-Json -Depth 5)
  Check 'restart stops the old Ollama' (-not ($after.Id | Where-Object { $_ -in $before.Id }))
  Check 'Ollama runs as the user, not SYSTEM' ($server -and $server.Owner -eq $sid) ($server | ConvertTo-Json)
  Check 'the tray app started Ollama' ($server -and $server.Parent -eq $tray.Id) ($server | ConvertTo-Json)
  Check 'Ollama answers after the restart' (WaitFor 10 { (Invoke-WebRequest "http://$ollamaHost/api/version").StatusCode -eq 200 })

  # --- Ollama on the network ------------------------------------------------------
  # unset: set through the agent, Ollama restarted by the tray app, its port let in
  $cmd = Join-Path $prog 'paddock-agent.exe'
  [Environment]::SetEnvironmentVariable('OLLAMA_HOST', $null, 'User')
  $out = & $cmd ollama-network | ConvertFrom-Json
  Check 'ollama-network sets OLLAMA_HOST' ($out.ok -and [Environment]::GetEnvironmentVariable('OLLAMA_HOST', 'User') -eq '0.0.0.0:11434') ($out | ConvertTo-Json)
  Check 'Ollama listens on the network after its restart' (WaitFor 20 { (Invoke-WebRequest 'http://127.0.0.1:11434/api/version').StatusCode -eq 200 })
  Check 'the firewall lets Ollama''s port in' ((Get-NetFirewallRule -DisplayName 'llama-paddock Ollama' -ErrorAction SilentlyContinue | Get-NetFirewallPortFilter).LocalPort -eq '11434')
  Check 'the Ollama URL is plain HTTP on its port' ($out.ollama_url -match '^http://.+:11434$') $out.ollama_url
  # already set: the user's choice stays, with a note when only this PC can reach Ollama
  [Environment]::SetEnvironmentVariable('OLLAMA_HOST', $ollamaHost, 'User')
  $out = & $cmd ollama-network | ConvertFrom-Json
  Check 'ollama-network leaves an OLLAMA_HOST that is set' ($out.ok -and [Environment]::GetEnvironmentVariable('OLLAMA_HOST', 'User') -eq $ollamaHost -and ($out.warnings -join ' ') -match 'only answers on this PC') ($out | ConvertTo-Json)

  # --- an upgrade over the running service and tray app -------------------------
  Write-Host '==> upgrade'
  Check 'the upgrade succeeds' ((RunSetup $Setup) -eq 0)
  $h = Health
  Check 'the upgrade keeps the HTTPS key and the token' ($h.fingerprint -eq $fingerprint -and (Agent GET /env).Status -eq 200)

  # --- uninstall -----------------------------------------------------------------
  Write-Host '==> uninstall'
  Check 'the uninstall succeeds' ((RunSetup (Join-Path $prog 'unins000.exe')) -eq 0)
  Check 'the service is gone' (-not (Get-Service $name -ErrorAction SilentlyContinue))
  Check 'the tray apps are gone' ((Trays).Count -eq 0 -and $null -eq (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run').'llama-paddock agent')
  Check 'the data folder is gone' (-not (Test-Path $data))
  Check 'the Ollama firewall rule is gone' (-not (Get-NetFirewallRule -DisplayName 'llama-paddock Ollama' -ErrorAction SilentlyContinue))
  Check 'the program folder is gone' (WaitFor 10 { -not (Test-Path (Join-Path $prog 'paddock-agent.exe')) })
}
finally {
  if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -Force -ErrorAction SilentlyContinue; sc.exe delete $name | Out-Null }
  Trays | Stop-Process -Force -ErrorAction SilentlyContinue
  Get-Process ollama, node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
  foreach ($v in 'OLLAMA_HOST', 'OLLAMA_PADDOCK_TEST_A', 'OLLAMA_PADDOCK_TEST_B') { [Environment]::SetEnvironmentVariable($v, $null, 'User') }
}

if ($failures.Count) { Write-Host "$($failures.Count) failed"; exit 1 }
Write-Host 'all passed'

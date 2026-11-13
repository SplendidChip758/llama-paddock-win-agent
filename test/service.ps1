# Runs the C# agent as a real Windows service (LocalSystem) with the tray app in this user's
# session, and checks the parts the comparison test can't: reading this user's files and variables
# as SYSTEM, the tray app's pipe between SYSTEM and the user, and Ollama started as the user rather
# than as SYSTEM. CI only: it needs an administrator, installs a service, writes
# %ProgramData%\llama-paddock-agent and this user's variables, and stops every ollama.exe.
#
#   ./test/service.ps1 -Agent <paddock-agent.exe> -Tray <paddock-agent-tray.exe> -FakeOllama <ollama.exe>
param(
  [Parameter(Mandatory)][string]$Agent,
  [Parameter(Mandatory)][string]$Tray,
  [Parameter(Mandatory)][string]$FakeOllama
)
$ErrorActionPreference = 'Stop'
$Agent = (Resolve-Path $Agent).Path
$Tray = (Resolve-Path $Tray).Path
$FakeOllama = (Resolve-Path $FakeOllama).Path

$name = 'llama-paddock-agent'
$token = 'service-test-token'
$port = 11560
$ollamaHost = '127.0.0.1:11545'
$data = Join-Path $env:ProgramData 'llama-paddock-agent'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$failures = [Collections.Generic.List[string]]::new()

function Check([string]$what, [bool]$ok, [string]$detail = '') {
  if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what $detail"; $failures.Add($what) }
}

function Agent([string]$method, [string]$path, $body = $null) {
  $params = @{ Method = $method; Uri = "http://127.0.0.1:$port$path"; Headers = @{ Authorization = "Bearer $token" }; SkipHttpErrorCheck = $true; TimeoutSec = 90 }
  if ($null -ne $body) { $params.Body = ($body | ConvertTo-Json -Depth 5 -Compress); $params.ContentType = 'application/json' }
  $r = Invoke-WebRequest @params
  [pscustomobject]@{ Status = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json -AsHashtable) }
}

function OllamaProcesses {
  @(Get-CimInstance Win32_Process -Filter "Name='ollama.exe'" | ForEach-Object {
    [pscustomobject]@{ Id = $_.ProcessId; Parent = $_.ParentProcessId; CommandLine = $_.CommandLine; Owner = (Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid).Sid }
  })
}

function WaitFor([scriptblock]$condition, [int]$seconds) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 500 }
  return $false
}

$trayProcess = $null
try {
  # this user's variables, which the service reads from HKEY_USERS\<SID>
  [Environment]::SetEnvironmentVariable('OLLAMA_HOST', $ollamaHost, 'User')
  [Environment]::SetEnvironmentVariable('OLLAMA_PADDOCK_TEST_A', 'from-the-user', 'User')

  $env:OLLAMA_HOST = $ollamaHost
  Start-Process $FakeOllama -ArgumentList serve -WindowStyle Hidden
  Check 'the fake Ollama answers' (WaitFor { try { (Invoke-WebRequest "http://$ollamaHost/api/version").StatusCode -eq 200 } catch { $false } } 20)

  New-Item -ItemType Directory -Force $data | Out-Null
  @{
    PADDOCK_AGENT_TOKEN = $token
    PADDOCK_AGENT_PORT = "$port"
    PADDOCK_OLLAMA_USER_SID = $sid
    # no desktop app there: the agent runs `ollama.exe serve` from that folder
    PADDOCK_OLLAMA_APP = Join-Path (Split-Path $FakeOllama) 'ollama app.exe'
  } | ConvertTo-Json | Set-Content (Join-Path $data 'agent.json')
  sc.exe create $name binPath= "`"$Agent`"" start= demand | Out-Null
  sc.exe start $name | Out-Null
  Check 'the service answers' (WaitFor { try { (Invoke-WebRequest "http://127.0.0.1:$port/health").StatusCode -eq 200 } catch { $false } } 30)

  $envState = Agent GET /env
  Check 'GET /env reads the user''s variables as SYSTEM' ($envState.Status -eq 200 -and $envState.Body.managed.OLLAMA_PADDOCK_TEST_A -eq 'from-the-user') ($envState | ConvertTo-Json -Depth 5)
  $stats = Agent GET /stats
  Check 'GET /stats finds the user''s Ollama' ($stats.Status -eq 200 -and $null -ne $stats.Body.system.ollama_process) ($stats.Body.system.ollama_process | ConvertTo-Json)
  Check 'GET /logs answers' ((Agent GET /logs).Status -eq 200)

  # without the tray app: nothing is stopped or written
  $before = OllamaProcesses
  $r = Agent POST /service/restart
  Check 'restart without the tray app answers 409' ($r.Status -eq 409) ($r | ConvertTo-Json -Depth 5)
  Check 'restart without the tray app stops nothing' (-not (Compare-Object @($before.Id) @((OllamaProcesses).Id)))
  $r = Agent PUT /env @{ vars = $envState.Body.managed + @{ OLLAMA_PADDOCK_TEST_B = '1' }; restart = $false }
  Check 'PUT /env without the tray app answers 409' ($r.Status -eq 409) ($r | ConvertTo-Json -Depth 5)
  Check 'PUT /env without the tray app writes nothing' ($null -eq [Environment]::GetEnvironmentVariable('OLLAMA_PADDOCK_TEST_B', 'User'))

  # the tray app serves SYSTEM only: no --allow-same-user here
  $trayProcess = Start-Process $Tray -ArgumentList --headless -PassThru
  Check 'the tray app serves the service' (WaitFor { (Agent PUT /env @{ vars = $envState.Body.managed; restart = $false }).Status -eq 200 } 20)

  $r = Agent PUT /env @{ vars = $envState.Body.managed + @{ OLLAMA_PADDOCK_TEST_B = '1' }; restart = $false }
  Check 'PUT /env through the tray app' ($r.Status -eq 200 -and [Environment]::GetEnvironmentVariable('OLLAMA_PADDOCK_TEST_B', 'User') -eq '1') ($r | ConvertTo-Json -Depth 5)

  $before = OllamaProcesses
  $r = Agent POST /service/restart
  $after = OllamaProcesses
  $server = $after | Where-Object { $_.CommandLine -match ' serve$' } | Select-Object -First 1
  Check 'restart answers ok' ($r.Status -eq 200 -and $r.Body.ok) ($r | ConvertTo-Json -Depth 5)
  Check 'restart stops the old Ollama' (-not ($after.Id | Where-Object { $_ -in $before.Id }))
  Check 'Ollama runs as the user, not SYSTEM' ($server -and $server.Owner -eq $sid) ($server | ConvertTo-Json)
  Check 'the tray app started Ollama' ($server -and $server.Parent -eq $trayProcess.Id) ($server | ConvertTo-Json)
  Check 'Ollama answers after the restart' (WaitFor { try { (Invoke-WebRequest "http://$ollamaHost/api/version").StatusCode -eq 200 } catch { $false } } 10)
}
finally {
  sc.exe stop $name | Out-Null
  Start-Sleep -Seconds 2
  sc.exe delete $name | Out-Null
  if ($trayProcess) { Stop-Process -Id $trayProcess.Id -Force -ErrorAction SilentlyContinue }
  Get-Process ollama -ErrorAction SilentlyContinue | Stop-Process -Force
  foreach ($v in 'OLLAMA_HOST', 'OLLAMA_PADDOCK_TEST_A', 'OLLAMA_PADDOCK_TEST_B') { [Environment]::SetEnvironmentVariable($v, $null, 'User') }
  Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}

if ($failures.Count) { Write-Host "$($failures.Count) failed"; exit 1 }
Write-Host 'all passed'

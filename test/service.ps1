# End-to-end test of the setup, as an administrator in PowerShell 7 (CI's windows-latest):
#   pwsh test\service.ps1 -Setup dist\llama-paddock-agent-setup.exe -FakeOllama <stub ollama.exe>
# Fakes a running scheduled-task install (the legacy setup), installs over it silently and checks
# that the token, the HTTPS key and the Ollama variables came along; then the service itself, an
# upgrade, and that uninstalling leaves nothing behind. Changes the machine: run it on a throwaway one.
param(
  [Parameter(Mandatory = $true)][string]$Setup,
  [Parameter(Mandatory = $true)][string]$FakeOllama
)
$ErrorActionPreference = "Stop"
$Setup = (Resolve-Path $Setup).Path
$FakeOllama = (Resolve-Path $FakeOllama).Path
$repo = Split-Path $PSScriptRoot
$svc = "llama-paddock-agent"
$prog = Join-Path $env:ProgramFiles "llama-paddock-agent"
$data = Join-Path $env:ProgramData "llama-paddock-agent"
$legacy = Join-Path $env:LOCALAPPDATA "llama-paddock-agent"
$port = 11435
$token = "legacy-token-" + [Guid]::NewGuid().ToString("N")
$headers = @{ Authorization = "Bearer $token" }
$failed = 0

function Check($name, $ok, $detail = "") {
  if ($ok) { Write-Host "ok    $name" } else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failed++ }
}
function Until($seconds, [scriptblock]$cond) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) { try { if (& $cond) { return $true } } catch {}; Start-Sleep -Milliseconds 500 }
  return $false
}
function Health { Invoke-RestMethod "https://127.0.0.1:$port/health" -SkipCertificateCheck -TimeoutSec 3 }
function Api($path, $method = "Get", $body = $null) {
  $p = @{ Uri = "https://127.0.0.1:$port$path"; Method = $method; Headers = $headers; SkipCertificateCheck = $true; TimeoutSec = 60 }
  if ($body) { $p.Body = ($body | ConvertTo-Json -Depth 5); $p.ContentType = "application/json" }
  Invoke-RestMethod @p
}
function Fakes { @(Get-CimInstance Win32_Process -Filter "Name = 'ollama.exe'" | Where-Object { $_.ExecutablePath -eq $FakeOllama }) }
function RunSetup {
  $log = Join-Path $env:RUNNER_TEMP "setup-$([Guid]::NewGuid().ToString('N').Substring(0, 6)).log"
  $p = Start-Process $Setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/OLLAMAEXE=`"$FakeOllama`"", "/LOG=`"$log`"" -Wait -PassThru
  if ($p.ExitCode -ne 0) { Get-Content $log -Tail 40 | Write-Host }
  $p.ExitCode
}

# --- a scheduled-task install, running ------------------------------------------
Write-Host "==> legacy install in $legacy"
New-Item -ItemType Directory -Force $legacy | Out-Null
Copy-Item -Force (Join-Path $repo "paddock-agent.mjs") $legacy
[IO.File]::WriteAllText((Join-Path $legacy "agent.json"), (@{
  PADDOCK_AGENT_TOKEN = $token; PADDOCK_AGENT_BIND = "0.0.0.0"; PADDOCK_AGENT_PORT = "$port"; PADDOCK_AGENT_HTTP = "off"
} | ConvertTo-Json), (New-Object Text.UTF8Encoding $true))  # with a BOM, as old installers wrote it
[Environment]::SetEnvironmentVariable("OLLAMA_KEEP_ALIVE", "7m", "User")
$action = New-ScheduledTaskAction -Execute "node.exe" -Argument "`"$legacy\paddock-agent.mjs`""
Register-ScheduledTask -TaskName $svc -Action $action -Trigger (New-ScheduledTaskTrigger -AtLogOn) -Force | Out-Null
$legacyNode = Start-Process node.exe -ArgumentList "`"$legacy\paddock-agent.mjs`"" -PassThru -WindowStyle Hidden
Check "legacy agent answers" (Until 30 { (Health).ok })
$fingerprint = (Health).fingerprint
Check "legacy agent has a key" ($fingerprint -like "SHA256:*") $fingerprint

# --- install over it --------------------------------------------------------------
Write-Host "==> setup (migration)"
Check "setup exits with 0" ((RunSetup) -eq 0)
$s = Get-CimInstance Win32_Service -Filter "Name = '$svc'"
Check "service is running" ($s.State -eq "Running") $s.State
Check "service runs as the virtual account" ($s.StartName -eq "NT SERVICE\$svc") $s.StartName
Check "service starts automatically" ($s.StartMode -eq "Auto") $s.StartMode
Check "agent answers" (Until 30 { (Health).mode -eq "service" })
Check "same HTTPS key" ((Health).fingerprint -eq $fingerprint) "$((Health).fingerprint) vs $fingerprint"
Check "same token" ((Api "/stats").mode -eq "service")
Check "legacy agent stopped" ($legacyNode.HasExited)
Check "scheduled task removed" (-not (Get-ScheduledTask -TaskName $svc -ErrorAction SilentlyContinue))
Check "legacy folder removed" (-not (Test-Path $legacy))
$envState = Api "/env"
Check "Ollama variables moved over" ($envState.managed.OLLAMA_KEEP_ALIVE -eq "7m") ($envState.managed | ConvertTo-Json -Compress)
Check "models folder kept" ($envState.managed.OLLAMA_MODELS -like "*\.ollama\models") $envState.managed.OLLAMA_MODELS
Check "Ollama runs under the service" (Until 30 { (Api "/stats").service.active -eq "active" })
Check "server.log readable through the agent" (Until 15 { (Api "/logs").lines -match "inference compute" })
$acl = Get-Acl $data
Check "data folder doesn't inherit" ($acl.AreAccessRulesProtected)
Check "users can't read the data folder" (-not ($acl.Access | Where-Object { $_.IdentityReference -match "\\Users$|Everyone|Authenticated Users" })) ($acl.Access.IdentityReference -join ", ")
Check "firewall rule added" ([bool](Get-NetFirewallRule -DisplayName "llama-paddock agent" -ErrorAction SilentlyContinue))

# --- the service ------------------------------------------------------------------
Write-Host "==> service"
$pid1 = (Api "/stats").service.main_pid
$put = Api "/env" "Put" @{ vars = @{ OLLAMA_KEEP_ALIVE = "9m"; OLLAMA_MODELS = $envState.managed.OLLAMA_MODELS } }
Check "PUT /env restarts Ollama" ($put.restart.ok) ($put | ConvertTo-Json -Compress)
Check "with a new pid" ((Api "/stats").service.main_pid -ne $pid1)
Check "Ollama gets the new value" (Until 15 { (Api "/logs").lines -match "OLLAMA_KEEP_ALIVE=9m" })

Stop-Service $svc
Check "stopping leaves no Ollama" (Until 10 { (Fakes).Count -eq 0 }) ("left: " + (Fakes).Count)
Check "stopping leaves no Node" (-not (Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $prog "node.exe") }))
Start-Service $svc
Check "starts again" (Until 30 { (Health).ok })

Get-Process node | Where-Object { $_.Path -eq (Join-Path $prog "node.exe") } | Stop-Process -Force
Check "recovers after the agent dies" (Until 60 { (Get-Service $svc).Status -eq "Running" -and (Health).ok })
Check "with one Ollama" (Until 30 { (Fakes).Count -eq 2 }) ("ollama.exe: " + (Fakes).Count)

# --- upgrade ----------------------------------------------------------------------
Write-Host "==> setup again (upgrade)"
Check "setup exits with 0" ((RunSetup) -eq 0)
Check "agent answers" (Until 30 { (Health).ok })
Check "same HTTPS key after the upgrade" ((Health).fingerprint -eq $fingerprint)
Check "same token after the upgrade" ((Api "/env").managed.OLLAMA_KEEP_ALIVE -eq "9m")

# --- uninstall --------------------------------------------------------------------
Write-Host "==> uninstall"
Start-Process (Join-Path $prog "unins000.exe") -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES" -Wait
# the uninstaller hands over to a copy of itself and returns early
Check "service removed" (Until 60 { -not (Get-Service $svc -ErrorAction SilentlyContinue) })
Check "program folder removed" (Until 60 { -not (Test-Path $prog) })
Check "data folder removed" (-not (Test-Path $data))
Check "firewall rule removed" (-not (Get-NetFirewallRule -DisplayName "llama-paddock agent" -ErrorAction SilentlyContinue))
Check "no Ollama left" ((Fakes).Count -eq 0)
Check "Ollama's folder no longer grants the service" (-not ((Get-Acl (Split-Path $FakeOllama)).Access | Where-Object { $_.IdentityReference -like "NT SERVICE\$svc" }))

[Environment]::SetEnvironmentVariable("OLLAMA_KEEP_ALIVE", $null, "User")
if ($failed) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed"

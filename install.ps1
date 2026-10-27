<#
.SYNOPSIS
  Install the llama-paddock agent on a Windows machine running Ollama.

.DESCRIPTION
  - Copies the agent to %LOCALAPPDATA%\llama-paddock-agent
  - Creates agent.json with a generated token (readable only by you)
  - Registers a Scheduled Task that starts the agent hidden when you log on,
    in your own session, so it can restart the Ollama tray app
  - Adds an inbound firewall rule for the agent port (needs an elevated shell)

  Run it from the user account that runs Ollama:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
  Optional: -Token <existing token>  -Port 11435  -AllowFrom 192.168.30.0/24
#>
param(
  [string]$Token = "",
  [int]$Port = 11435,
  [string]$AllowFrom = "LocalSubnet"
)
$ErrorActionPreference = "Stop"
$TaskName = "llama-paddock-agent"
$Dir = Join-Path $env:LOCALAPPDATA "llama-paddock-agent"

# --- Node.js ---------------------------------------------------------------
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) {
  Write-Host "Node.js not found. Install it first, e.g.:  winget install OpenJS.NodeJS.LTS" -ForegroundColor Red
  exit 1
}
$ok = & $node -e "const [a,b]=process.versions.node.split('.').map(Number);process.exit(a>18||(a===18&&b>=15)?0:1)"
if ($LASTEXITCODE -ne 0) {
  Write-Host "Node.js >= 18.15 required (found $(& $node --version)). Upgrade: winget upgrade OpenJS.NodeJS.LTS" -ForegroundColor Red
  exit 1
}
Write-Host "==> node $(& $node --version) at $node"

# --- files -----------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
Copy-Item -Force (Join-Path $PSScriptRoot "paddock-agent.mjs") (Join-Path $Dir "paddock-agent.mjs")

$cfgPath = Join-Path $Dir "agent.json"
if (-not (Test-Path $cfgPath)) {
  if (-not $Token) {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $Token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
  }
  @{
    PADDOCK_AGENT_TOKEN = $Token
    PADDOCK_AGENT_BIND  = "0.0.0.0"
    PADDOCK_AGENT_PORT  = "$Port"
    PADDOCK_DISK_PATHS  = ""
  } | ConvertTo-Json | Set-Content -Encoding UTF8 $cfgPath
  # only the current user can read the token
  icacls $cfgPath /inheritance:r /grant:r "$($env:USERNAME):(F)" | Out-Null
  Write-Host "==> generated token (paste it as the agent token under Add host in the dashboard):" -ForegroundColor Green
  Write-Host "    $Token"
} else {
  Write-Host "==> keeping existing $cfgPath"
}

# --- scheduled task (runs hidden at logon, restarts on failure) -------------
# Node runs in the foreground of a hidden PowerShell, so the task stays "running"
# while the agent runs and a crash (non-zero exit) triggers the task's restart policy.
# The command is passed encoded so paths with spaces/quotes can't break it.
$launch = "& '$($node.Replace("'", "''"))' '$($Dir.Replace("'", "''"))\paddock-agent.mjs'; exit `$LASTEXITCODE"
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launch))
$action = New-ScheduledTaskAction -Execute "powershell.exe" -WorkingDirectory $Dir `
  -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand $encoded"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
# stop a running copy first so updates take effect
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" |
  Where-Object { $_.CommandLine -like "*llama-paddock-agent*paddock-agent.mjs*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-ScheduledTask -TaskName $TaskName
Write-Host "==> scheduled task '$TaskName' registered and started"

# --- firewall ---------------------------------------------------------------
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
  [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($admin) {
  Get-NetFirewallRule -DisplayName "llama-paddock agent" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  New-NetFirewallRule -DisplayName "llama-paddock agent" -Direction Inbound -Protocol TCP -LocalPort $Port `
    -RemoteAddress $AllowFrom -Action Allow -Profile Private,Domain | Out-Null
  Write-Host "==> firewall: TCP $Port allowed from $AllowFrom (Private/Domain networks)"
} else {
  Write-Host "==> not elevated: skipped firewall rule. Re-run from an admin PowerShell, or allow TCP $Port yourself." -ForegroundColor Yellow
}

Start-Sleep -Seconds 2
try {
  $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 5
  Write-Host "==> health: ok (agent $($h.version) on $($h.hostname))" -ForegroundColor Green
} catch {
  Write-Host "==> agent not answering yet; check Task Scheduler > $TaskName" -ForegroundColor Yellow
}

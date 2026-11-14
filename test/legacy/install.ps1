<#
.SYNOPSIS
  Install the llama-paddock agent on a Windows machine running Ollama.

.DESCRIPTION
  - Copies the agent to %LOCALAPPDATA%\llama-paddock-agent
  - Creates agent.json with a generated token (readable only by you)
  - Registers a Scheduled Task that starts the agent hidden when you log on,
    in your own session, so it can restart the Ollama tray app
  - Adds an inbound firewall rule for the agent port (needs an elevated shell)
  - Prints the fingerprint of the agent's HTTPS key (agent.key, made on its first
    start) for the dashboard. New installs serve HTTPS only; updated ones keep
    plain HTTP on until you set PADDOCK_AGENT_HTTP to "off" in agent.json

  Run it from the user account that runs Ollama:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
  Optional: -Token <existing token>  -Port 11435  -AllowFrom 192.168.30.0/24
            -NewKey  replace the agent's HTTPS key (accept the new one in the dashboard afterwards)
#>
param(
  [string]$Token = "",
  [int]$Port = 11435,
  [string]$AllowFrom = "LocalSubnet",
  [switch]$NewKey
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
  $json = @{
    PADDOCK_AGENT_TOKEN = $Token
    PADDOCK_AGENT_BIND  = "0.0.0.0"
    PADDOCK_AGENT_PORT  = "$Port"
    PADDOCK_DISK_PATHS  = ""
    # HTTPS only; "on" also serves plain HTTP, for dashboards before 0.7.0
    PADDOCK_AGENT_HTTP  = "off"
  } | ConvertTo-Json
  # UTF-8 without a BOM: Windows PowerShell's Set-Content -Encoding UTF8 adds one
  [IO.File]::WriteAllText($cfgPath, $json, (New-Object Text.UTF8Encoding $false))
  # only the current user can read the token
  icacls $cfgPath /inheritance:r /grant:r "$($env:USERNAME):(F)" | Out-Null
  Write-Host "==> generated token (paste it as the agent token under Add host in the dashboard):" -ForegroundColor Green
  Write-Host "    $Token"
} else {
  Write-Host "==> keeping existing $cfgPath"
}

if ($NewKey) {
  $cfg = Get-Content -Raw $cfgPath | ConvertFrom-Json
  # only the agent's own key: one you put in place yourself is yours to replace
  if ($cfg.PADDOCK_AGENT_TLS_KEY -or $cfg.PADDOCK_AGENT_TLS_CERT) {
    Write-Host "PADDOCK_AGENT_TLS_KEY/CERT in agent.json point to your own files; replace those yourself" -ForegroundColor Red
    exit 1
  }
  Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $Dir "agent.key"), (Join-Path $Dir "agent.crt")
  Write-Host "==> removed the agent's key; it makes a new one when it starts"
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
# Ask through Node: Windows PowerShell can't skip the check of the agent's self-signed certificate
# (it's the agent's own, on the loopback interface). HTTPS first, plain HTTP if it can't serve that.
$probe = "const get=(m,o)=>new Promise((res,rej)=>require(m).get({host:'127.0.0.1',port:$Port,path:'/health',timeout:5000,...o},(r)=>{let s='';r.on('data',(d)=>s+=d).on('end',()=>res(s))}).on('error',rej));get('https',{rejectUnauthorized:false}).catch(()=>get('http',{})).then((s)=>console.log(s),()=>process.exit(1))"
$out = & $node -e $probe 2>$null
if ($LASTEXITCODE -eq 0 -and $out) {
  $h = $out | ConvertFrom-Json
  Write-Host "==> health: ok (agent $($h.version) on $($h.hostname))" -ForegroundColor Green
  if ($h.fingerprint) {
    Write-Host "==> in the dashboard (Add host), use agent URL https://<this PC's address>:$Port and this key fingerprint:" -ForegroundColor Green
    Write-Host "    $($h.fingerprint)"
    if ($NewKey) {
      Write-Host "==> the key is new: the dashboard shows that it changed. Compare it with the fingerprint above, then accept it."
    }
  } else {
    Write-Host "==> the agent serves plain HTTP only: it couldn't set up its HTTPS key (see its output in Task Scheduler)" -ForegroundColor Yellow
  }
} else {
  Write-Host "==> agent not answering yet; check Task Scheduler > $TaskName" -ForegroundColor Yellow
}

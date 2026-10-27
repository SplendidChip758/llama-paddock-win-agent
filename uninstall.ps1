# Remove the llama-paddock agent (run from the same user account; elevated to also remove the firewall rule).
$ErrorActionPreference = "Continue"
$TaskName = "llama-paddock-agent"
$Dir = Join-Path $env:LOCALAPPDATA "llama-paddock-agent"

Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" |
  Where-Object { $_.CommandLine -like "*llama-paddock-agent*paddock-agent.mjs*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-NetFirewallRule -DisplayName "llama-paddock agent" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item -Recurse -Force $Dir -ErrorAction SilentlyContinue
Write-Host "llama-paddock agent removed. Your Ollama environment variables were left as they are."

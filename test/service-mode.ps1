# The agent's service mode without the service: runs paddock-agent.mjs as the service would
# (PADDOCK_AGENT_MODE=service, its own data folder, stdin as the stop signal) against the stub
# ollama.exe from test/fake-ollama, and checks the API, Ollama's supervision and the shutdown.
# Needs no admin rights. Windows PowerShell 5.1 or PowerShell 7:
#   powershell -ExecutionPolicy Bypass -File test\service-mode.ps1 -FakeOllama <path to the stub ollama.exe>
param(
  [Parameter(Mandatory = $true)][string]$FakeOllama,
  [string]$Node = "node.exe",
  [int]$Port = 11595,
  [int]$OllamaPort = 11594
)
$ErrorActionPreference = "Stop"
$agent = Join-Path (Split-Path $PSScriptRoot) "paddock-agent.mjs"
$data = Join-Path ([IO.Path]::GetTempPath()) ("paddock-service-mode-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$token = "service-mode-test"
$base = "http://127.0.0.1:$Port"
$headers = @{ Authorization = "Bearer $token" }
$failed = 0

function Check($name, $ok, $detail = "") {
  if ($ok) { Write-Host "ok    $name" } else { Write-Host "FAIL  $name  $detail" -ForegroundColor Red; $script:failed++ }
}
function Api($path, $method = "Get", $body = $null) {
  $p = @{ Uri = "$base$path"; Method = $method; Headers = $headers; TimeoutSec = 60 }
  if ($body) { $p.Body = ($body | ConvertTo-Json -Depth 5); $p.ContentType = "application/json" }
  Invoke-RestMethod @p
}
function Until($seconds, [scriptblock]$cond) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $deadline) { try { if (& $cond) { return $true } } catch {}; Start-Sleep -Milliseconds 300 }
  return $false
}
# the stub's server and its runner: ollama.exe, and llama-server.exe when a copy has that name
function FakeProcesses {
  $dir = Split-Path (Resolve-Path $FakeOllama).Path
  @(Get-CimInstance Win32_Process -Filter "Name = 'ollama.exe' OR Name = 'llama-server.exe'" | Where-Object { $_.ExecutablePath -and (Split-Path $_.ExecutablePath) -eq $dir })
}
function LlamaServers { @(FakeProcesses | Where-Object { $_.Name -eq "llama-server.exe" }) }
$expectLlamaServer = Test-Path (Join-Path (Split-Path (Resolve-Path $FakeOllama).Path) "llama-server.exe")

New-Item -ItemType Directory $data | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $data "agent.json"), (@{
  PADDOCK_AGENT_TOKEN = $token; PADDOCK_AGENT_PORT = "$Port"; PADDOCK_AGENT_BIND = "127.0.0.1"
  PADDOCK_OLLAMA_EXE = (Resolve-Path $FakeOllama).Path
} | ConvertTo-Json), $utf8)
[IO.File]::WriteAllText((Join-Path $data "ollama-env.json"), (@{
  OLLAMA_HOST = "127.0.0.1:$OllamaPort"; OLLAMA_KEEP_ALIVE = "5m"
} | ConvertTo-Json), $utf8)

$psi = New-Object Diagnostics.ProcessStartInfo $Node, "`"$agent`""
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.EnvironmentVariables["PADDOCK_AGENT_MODE"] = "service"
$psi.EnvironmentVariables["PADDOCK_AGENT_DATA"] = $data
$proc = [Diagnostics.Process]::Start($psi)

try {
  Check "agent answers" (Until 30 { (Invoke-RestMethod "$base/health" -TimeoutSec 2).ok })
  $h = Invoke-RestMethod "$base/health"
  Check "/health says service mode" ($h.mode -eq "service") $h.mode
  Check "TLS key made in the data folder" (Test-Path (Join-Path $data "agent.key"))

  Check "Ollama started" (Until 30 { (Invoke-RestMethod "http://127.0.0.1:$OllamaPort/api/version" -TimeoutSec 2).version })
  $s = Api "/stats"
  $pid1 = $s.service.main_pid
  Check "/stats reports Ollama running" ($s.service.active -eq "active" -and $pid1 -and $s.service.since) ($s.service | ConvertTo-Json -Compress)
  Check "/stats ollama_process is the supervised server" ($s.system.ollama_process.pid -eq $pid1) "$($s.system.ollama_process.pid) vs $pid1"
  Check "/stats counts the model runner" (Until 10 { (Api "/stats").system.ollama_process.runners -eq 1 }) ((Api "/stats").system.ollama_process | ConvertTo-Json -Compress)
  if ($expectLlamaServer) { Check "the runner is llama-server.exe" (@(LlamaServers).Count -eq 1) ("llama-server.exe: " + @(LlamaServers).Count) }

  $env1 = Api "/env"
  Check "/env reads ollama-env.json" ($env1.mode -eq "service" -and $env1.managed.OLLAMA_KEEP_ALIVE -eq "5m" -and $env1.override_path -like "*ollama-env.json") ($env1 | ConvertTo-Json -Compress)
  Check "server.log has Ollama's environment" (Until 10 { (Api "/logs").lines -match "OLLAMA_KEEP_ALIVE=5m" })
  Check "/gpu-check reads server.log" ((Api "/gpu-check").log.lines -match "inference compute")
  Check "/runner-starts reads server.log" ((Api "/runner-starts?since=$([int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) - 600)").starts.Count -ge 1)
  Invoke-RestMethod "http://127.0.0.1:$OllamaPort/api/version" | Out-Null
  $since = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 120
  Check "/requests reads server.log" (Until 5 { (Api "/requests?since=$since").requests | Where-Object { $_.path -eq "/api/version" } })

  $put = Api "/env" "Put" @{ vars = @{ OLLAMA_HOST = "127.0.0.1:$OllamaPort"; OLLAMA_KEEP_ALIVE = "10m" } }
  Check "PUT /env restarts Ollama" ($put.restart.ok) ($put | ConvertTo-Json -Compress)
  $pid2 = (Api "/stats").service.main_pid
  Check "Ollama has a new pid" ($pid2 -and $pid2 -ne $pid1) "$pid1 -> $pid2"
  $saved = Get-Content -Raw (Join-Path $data "ollama-env.json") | ConvertFrom-Json
  Check "ollama-env.json saved" ($saved.OLLAMA_KEEP_ALIVE -eq "10m")
  Check "Ollama runs with the new value" (Until 10 { (Api "/logs").lines -match "OLLAMA_KEEP_ALIVE=10m" })
  Check "the previous run's log is kept" (Test-Path (Join-Path $data "logs\server-1.log"))
  Check "the old runner is gone" (Until 10 { @(FakeProcesses).Count -eq 2 }) ("processes: " + @(FakeProcesses).Count)

  $bad = try { Api "/env" "Put" @{ vars = @{ CUDA_INJECTION64_PATH = "x" } }; $false } catch { $_.Exception.Response.StatusCode.value__ -eq 400 }
  Check "PUT /env still refuses variables off the allowlist" $bad

  Stop-Process -Id $pid2 -Force
  Check "a dead Ollama is started again" (Until 15 { $p = (Api "/stats").service.main_pid; $p -and $p -ne $pid2 })
  # the dead server's runner is an orphan: the agent stops it before starting Ollama again
  Check "the dead Ollama's runner is gone" (Until 15 { @(FakeProcesses).Count -eq 2 }) ("processes: " + (@(FakeProcesses).Name -join ", "))

  $proc.StandardInput.Close()
  Check "closing stdin stops the agent" ($proc.WaitForExit(20000)) ""
  if ($proc.HasExited) { Check "it exits with 0" ($proc.ExitCode -eq 0) "exit code $($proc.ExitCode)" }
  Check "no Ollama or runner is left" (Until 10 { @(FakeProcesses).Count -eq 0 }) ("left: " + (@(FakeProcesses).Name -join ", "))
} finally {
  if (-not $proc.HasExited) { $proc.Kill() }
  FakeProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
if ($failed) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "all checks passed"

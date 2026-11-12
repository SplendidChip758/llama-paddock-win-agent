# Builds dist\llama-paddock-agent-setup.exe and dist\SHA256SUMS, on Windows (CI's windows-latest,
# or a PC with the .NET SDK and Inno Setup 6):
#   pwsh .github/scripts/build-setup.ps1 -Version 1.2.3
# The service program is built from service\, Node.js is the version pinned in build\node.json,
# downloaded from nodejs.org and refused unless its SHA-256 matches.
param(
  [string]$Version = "0.0.0",
  [string]$Iscc = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$out = Join-Path $root "build\out"
$dist = Join-Path $root "dist"

function Fetch($url, $file, $sha256) {
  if (-not (Test-Path $file) -or (Get-FileHash -Algorithm SHA256 $file).Hash -ne $sha256) {
    Write-Host "==> $url"
    Invoke-WebRequest -UseBasicParsing $url -OutFile $file
  }
  $got = (Get-FileHash -Algorithm SHA256 $file).Hash
  if ($got -ne $sha256) {
    Remove-Item -Force $file
    throw "$url has SHA-256 $got, not the $sha256 pinned in build\node.json"
  }
}

# --- the service program ------------------------------------------------------
Write-Host "==> service"
& dotnet build (Join-Path $root "service") -c Release -o (Join-Path $out "service") -nologo -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

# --- Node.js ------------------------------------------------------------------
$pin = Get-Content -Raw (Join-Path $root "build\node.json") | ConvertFrom-Json
$nodeDir = Join-Path $out "node"
New-Item -ItemType Directory -Force $nodeDir | Out-Null
Fetch "https://nodejs.org/dist/v$($pin.version)/win-x64/node.exe" (Join-Path $nodeDir "node.exe") $pin.node_exe_sha256
Fetch "https://raw.githubusercontent.com/nodejs/node/v$($pin.version)/LICENSE" (Join-Path $nodeDir "LICENSE") $pin.license_sha256

# --- the setup ----------------------------------------------------------------
if (-not $Iscc) {
  $Iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
  ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw "Inno Setup 6 not found: winget install JRSoftware.InnoSetup, or pass -Iscc" }
Write-Host "==> setup ($Iscc)"
& $Iscc /Q "/DAppVersion=$Version" (Join-Path $root "setup\llama-paddock-agent.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

# --- checksums: the setup, and what it installs -------------------------------
$lines = @()
$setup = Join-Path $dist "llama-paddock-agent-setup.exe"
$lines += "{0}  llama-paddock-agent-setup.exe" -f (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLower()
foreach ($f in @(
    @{ path = Join-Path $out "service\paddock-agent-service.exe"; name = "llama-paddock-agent/paddock-agent-service.exe" },
    @{ path = Join-Path $nodeDir "node.exe"; name = "llama-paddock-agent/node.exe" },
    @{ path = Join-Path $root "paddock-agent.mjs"; name = "llama-paddock-agent/paddock-agent.mjs" })) {
  $lines += "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $f.path).Hash.ToLower(), $f.name
}
[IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS"), ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "==> $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Get-Content (Join-Path $dist "SHA256SUMS")

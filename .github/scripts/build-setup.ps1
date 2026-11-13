# Builds dist\llama-paddock-agent-setup.exe and dist\SHA256SUMS, on Windows (CI's windows-latest,
# or a PC with the .NET 10 SDK and Inno Setup 6):
#   pwsh .github/scripts/build-setup.ps1 -Version 1.2.3
# The agent and the tray app are published self-contained into one folder, so the PC needs nothing
# installed first and the two share one copy of the .NET runtime (single-file exes would carry one
# each, and their compression leaves the setup's nothing to do: twice the download).
param(
  [string]$Version = "0.0.0-dev",
  [string]$Iscc = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$out = Join-Path $root "build\out"
$dist = Join-Path $root "dist"
Remove-Item -Recurse -Force $out, $dist -ErrorAction SilentlyContinue

foreach ($project in "agent", "tray") {
  Write-Host "==> $project"
  & dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -o $out -nologo `
    -p:DebugType=none -p:Version=$Version
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

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

# checksums: the setup, and what it installs
$lines = @()
$setup = Join-Path $dist "llama-paddock-agent-setup.exe"
$lines += "{0}  llama-paddock-agent-setup.exe" -f (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLower()
foreach ($f in "paddock-agent.exe", "paddock-agent-tray.exe") {
  $lines += "{0}  llama-paddock-agent/{1}" -f (Get-FileHash -Algorithm SHA256 (Join-Path $out $f)).Hash.ToLower(), $f
}
[IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS"), ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "==> $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Get-Content (Join-Path $dist "SHA256SUMS")

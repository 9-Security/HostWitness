# Publish HostWitness.Agent (console, no UI) for remote deployment.
# Usage: .\publish-agent.ps1 [output directory]
# Default output: Release\Agent
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "Release\Agent")
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$agentProj = Join-Path $root "WinDFIR.Agent\WinDFIR.Agent.csproj"

if (-not (Test-Path $agentProj)) {
    Write-Error "Project not found: $agentProj"
    exit 1
}

$absOut = [System.IO.Path]::GetFullPath($OutDir)
if (Test-Path $absOut) {
    Remove-Item -Recurse -Force $absOut
}
New-Item -ItemType Directory -Path $absOut -Force | Out-Null

Write-Host "Publishing HostWitness.Agent to $absOut ..."
dotnet publish $agentProj -c Release -o $absOut --no-self-contained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Done. Run: $absOut\HostWitness.Agent.exe [output_dir] [seconds] [--etw] [--providers=...]"
Write-Host "See docs\遠端採集Agent說明.md for usage."

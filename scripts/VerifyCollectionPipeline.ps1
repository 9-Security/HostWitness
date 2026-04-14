<#
.SYNOPSIS
    One-shot verification pipeline for collection return artifacts.
.DESCRIPTION
    Verifies:
    1) collect-status-latest.json HMAC signature (optional if -HmacKey provided)
    2) snapshot integrity via hashes.txt
    Outputs one machine-readable JSON report.
.PARAMETER StatusPath
    Path to collect-status-latest.json.
.PARAMETER SnapshotPath
    Path to snapshot_* folder.
.PARAMETER HmacKey
    Shared secret for status signature verification. If omitted, signature check is skipped.
.PARAMETER ReportPath
    Output path for final JSON report. Default: <SnapshotPath>\pipeline-verify-report.json
.EXAMPLE
    .\VerifyCollectionPipeline.ps1 -StatusPath "C:\Collect\collect-status-latest.json" -SnapshotPath "C:\Collect\snapshot_20260305_010203" -HmacKey "shared-secret"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StatusPath,
    [Parameter(Mandatory = $true)]
    [string] $SnapshotPath,
    [string] $HmacKey = "",
    [string] $ReportPath = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $StatusPath)) {
    Write-Error "Status file not found: $StatusPath"
    exit 1
}
if (-not (Test-Path -LiteralPath $SnapshotPath)) {
    Write-Error "Snapshot folder not found: $SnapshotPath"
    exit 1
}

function Resolve-ToolPath {
    param([Parameter(Mandatory = $true)][string] $ToolFileName)
    $base = Split-Path -Parent $PSCommandPath
    return Join-Path $base $ToolFileName
}

function Read-JsonSafe {
    param([string] $Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        return (Get-Content -Path $Path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 10)
    }
    catch {
        return $null
    }
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $SnapshotPath "pipeline-verify-report.json"
}

$report = [ordered]@{
    timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    statusPath = $StatusPath
    snapshotPath = $SnapshotPath
    success = $false
    checks = [ordered]@{
        statusSignature = [ordered]@{
            executed = $false
            success = $false
            skipped = $false
            exitCode = $null
            error = $null
        }
        snapshotIntegrity = [ordered]@{
            executed = $false
            success = $false
            exitCode = $null
            error = $null
        }
    }
}

try {
    $statusVerifier = Resolve-ToolPath "VerifyCollectStatusSignature.ps1"
    $snapshotVerifier = Resolve-ToolPath "VerifySnapshotIntegrity.ps1"

    if (-not (Test-Path -LiteralPath $snapshotVerifier)) {
        throw "Missing verifier script: $snapshotVerifier"
    }

    if ([string]::IsNullOrWhiteSpace($HmacKey)) {
        $report.checks.statusSignature.executed = $false
        $report.checks.statusSignature.skipped = $true
        $report.checks.statusSignature.success = $true
    }
    else {
        if (-not (Test-Path -LiteralPath $statusVerifier)) {
            throw "Missing verifier script: $statusVerifier"
        }
        $report.checks.statusSignature.executed = $true
        & $statusVerifier -StatusPath $StatusPath -HmacKey $HmacKey
        $sigExit = $LASTEXITCODE
        if ($null -eq $sigExit) { $sigExit = 1 }
        $report.checks.statusSignature.exitCode = $sigExit
        $report.checks.statusSignature.success = ($sigExit -eq 0)
        if ($sigExit -ne 0) {
            $report.checks.statusSignature.error = "Signature verification failed."
        }
    }

    $report.checks.snapshotIntegrity.executed = $true
    & $snapshotVerifier -SnapshotPath $SnapshotPath
    $intExit = $LASTEXITCODE
    if ($null -eq $intExit) { $intExit = 1 }
    $report.checks.snapshotIntegrity.exitCode = $intExit
    $report.checks.snapshotIntegrity.success = ($intExit -eq 0)
    if ($intExit -ne 0) {
        $report.checks.snapshotIntegrity.error = "Snapshot integrity verification failed."
    }

    $report.success = ($report.checks.statusSignature.success -and $report.checks.snapshotIntegrity.success)
}
catch {
    $report.success = $false
    if (-not $report.error) {
        $report.error = $_.Exception.Message
    }
}
finally {
    $report.timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    $json = $report | ConvertTo-Json -Depth 10
    $reportDir = Split-Path -Parent $ReportPath
    if ($reportDir -and -not (Test-Path -LiteralPath $reportDir)) {
        New-Item -Path $reportDir -ItemType Directory -Force | Out-Null
    }
    Set-Content -Path $ReportPath -Value $json -Encoding UTF8
    Write-Host "Pipeline report: $ReportPath"
}

if ($report.success) { exit 0 }
exit 1


<#
.SYNOPSIS
    Runs HostWitness Agent once, then copies the latest snapshot to a destination (e.g. network share) for auto-return.
.PARAMETER AgentPath
    Full path to HostWitness.Agent.exe.
.PARAMETER OutputDir
    Output directory for snapshots (Agent writes snapshot_* here).
.PARAMETER CollectSeconds
    Collection duration in seconds (default 60).
.PARAMETER EnableEtw
    Add --etw to Agent.
.PARAMETER CopyToPath
    After collection, copy the latest snapshot_* folder to this path (e.g. \\server\share\Snapshots\HostA).
.PARAMETER RetryCount
    Retry count for copy operation (default 3).
.PARAMETER RetryDelaySeconds
    Delay between retries in seconds (default 3).
.PARAMETER IncludeEvtx
    Copy Application/System/Security EVTX into snapshot raw\evtx before return.
.PARAMETER VerifyCopy
    Verify copy integrity with manifest.json SHA256 comparison.
.PARAMETER StatusHmacKey
    Optional shared secret to HMAC-sign collect-status-latest.json payload.
.EXAMPLE
    .\RunCollectAndCopy.ps1 -AgentPath "C:\Deploy\Agent\HostWitness.Agent.exe" -OutputDir "C:\Collect" -CollectSeconds 90 -EnableEtw -CopyToPath "\\analysis\Snapshots\$(hostname)"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AgentPath,
    [Parameter(Mandatory = $true)]
    [string] $OutputDir,
    [int] $CollectSeconds = 60,
    [switch] $EnableEtw,
    [Parameter(Mandatory = $true)]
    [string] $CopyToPath,
    [int] $RetryCount = 3,
    [int] $RetryDelaySeconds = 3,
    [switch] $IncludeEvtx,
    [switch] $VerifyCopy = $true,
    [string] $StatusHmacKey = ""
)
$ErrorActionPreference = "Stop"

function Copy-WithRetry {
    param(
        [Parameter(Mandatory = $true)][string] $SourcePath,
        [Parameter(Mandatory = $true)][string] $DestinationPath,
        [int] $RetryCount = 3,
        [int] $RetryDelaySeconds = 3
    )
    $attempt = 0
    while ($true) {
        try {
            Copy-Item -Path $SourcePath -Destination $DestinationPath -Recurse -Force
            return
        }
        catch {
            $attempt++
            if ($attempt -gt $RetryCount) { throw }
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
}

function Add-EvtxToSnapshot {
    param([Parameter(Mandatory = $true)][string] $SnapshotDir)
    $logsRoot = Join-Path $env:SystemRoot "System32\winevt\Logs"
    if (-not (Test-Path -LiteralPath $logsRoot)) { return }
    $evtxOut = Join-Path $SnapshotDir "raw\evtx"
    New-Item -ItemType Directory -Path $evtxOut -Force | Out-Null
    foreach ($name in @("Application.evtx", "System.evtx", "Security.evtx")) {
        $src = Join-Path $logsRoot $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -Path $src -Destination (Join-Path $evtxOut $name) -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-FileHashSafe {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return "" }
    try { return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant() } catch { return "" }
}

function Write-StatusReport {
    param(
        [Parameter(Mandatory = $true)][string] $StatusPath,
        [Parameter(Mandatory = $true)][hashtable] $Status
    )
    if (-not [string]::IsNullOrWhiteSpace($StatusHmacKey)) {
        $payload = Get-StatusPayloadJson -Status $Status
        $Status.signatureAlgorithm = "HMAC-SHA256"
        $Status.signature = Compute-HmacSignature -Text $payload -Key $StatusHmacKey
    }
    $json = $Status | ConvertTo-Json -Depth 6
    Set-Content -Path $StatusPath -Value $json -Encoding UTF8
}

function Get-StatusPayloadJson {
    param([hashtable] $Status)
    $clone = @{}
    foreach ($k in $Status.Keys) {
        if ($k -in @("signature", "signatureAlgorithm")) { continue }
        $clone[$k] = $Status[$k]
    }
    return ($clone | ConvertTo-Json -Depth 6)
}

function Compute-HmacSignature {
    param(
        [Parameter(Mandatory = $true)][string] $Text,
        [Parameter(Mandatory = $true)][string] $Key
    )
    $encoding = [System.Text.Encoding]::UTF8
    $keyBytes = $encoding.GetBytes($Key)
    $textBytes = $encoding.GetBytes($Text)
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($keyBytes)
    try {
        $hash = $hmac.ComputeHash($textBytes)
        return ([System.BitConverter]::ToString($hash) -replace "-", "").ToLowerInvariant()
    }
    finally {
        $hmac.Dispose()
    }
}

$status = @{
    timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    host = $env:COMPUTERNAME
    success = $false
    stage = "init"
    error = $null
    agentExitCode = $null
    exportSkipped = $false
    outputDir = $OutputDir
    copyToPath = $CopyToPath
    snapshotName = $null
    snapshotPath = $null
    destinationPath = $null
    includeEvtx = [bool]$IncludeEvtx
    verifyCopy = [bool]$VerifyCopy
    manifestHashSource = $null
    manifestHashDestination = $null
    manifestVerified = $false
    signatureAlgorithm = $null
    signature = $null
}
$statusPath = Join-Path $OutputDir "collect-status-latest.json"
$script:RunCollectFinalExitCode = 1

try {
    if (-not (Test-Path -LiteralPath $AgentPath)) {
        $status.stage = "agent_not_found"
        $status.error = "Agent executable not found: $AgentPath"
        throw "Agent not found: $AgentPath"
    }
    $status.stage = "collect"
    $agentArgs = @($OutputDir, $CollectSeconds)
    if ($EnableEtw) { $agentArgs += "--etw" }
    & $AgentPath @agentArgs
    $agentExit = $LASTEXITCODE
    $status.agentExitCode = $agentExit
    if ($agentExit -ne 0) {
        if ($agentExit -eq 2) {
            $status.exportSkipped = $true
            $status.stage = "agent_stop_failed"
            $status.error = "Agent exited with code 2 (provider stop failure; snapshot export was not performed)."
            $script:RunCollectFinalExitCode = 2
        }
        elseif ($agentExit -eq 1) {
            $status.stage = "agent_failed"
            $status.error = "Agent exited with code 1 (provider start rollback, export failure, or other error). See Agent console output."
            $script:RunCollectFinalExitCode = 1
        }
        else {
            $status.stage = "agent_failed"
            $status.error = "Agent exited with code $agentExit."
            $script:RunCollectFinalExitCode = 1
        }
        throw $status.error
    }

    $status.stage = "locate_snapshot"
    $latest = Get-ChildItem -Path $OutputDir -Filter "snapshot_*" -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) {
        throw "No snapshot_* folder found in $OutputDir"
    }
    $status.snapshotName = $latest.Name
    $status.snapshotPath = $latest.FullName

    if ($IncludeEvtx) {
        $status.stage = "add_evtx"
        Add-EvtxToSnapshot -SnapshotDir $latest.FullName
    }

    $status.stage = "copy"
    if (-not (Test-Path $CopyToPath)) { New-Item -ItemType Directory -Path $CopyToPath -Force | Out-Null }
    $destDir = Join-Path $CopyToPath $latest.Name
    $status.destinationPath = $destDir
    Copy-WithRetry -SourcePath $latest.FullName -DestinationPath $destDir -RetryCount $RetryCount -RetryDelaySeconds $RetryDelaySeconds

    if ($VerifyCopy) {
        $status.stage = "verify"
        $srcManifest = Join-Path $latest.FullName "manifest.json"
        $dstManifest = Join-Path $destDir "manifest.json"
        $srcHash = Get-FileHashSafe -Path $srcManifest
        $dstHash = Get-FileHashSafe -Path $dstManifest
        $status.manifestHashSource = $srcHash
        $status.manifestHashDestination = $dstHash
        $status.manifestVerified = ($srcHash -and $dstHash -and $srcHash -eq $dstHash)
        if (-not $status.manifestVerified) {
            throw "Manifest hash verification failed."
        }
    }

    $status.success = $true
    $status.stage = "done"
    $script:RunCollectFinalExitCode = 0
    Write-Host "Copied $($latest.Name) to $CopyToPath"
}
catch {
    $status.success = $false
    if ([string]::IsNullOrWhiteSpace([string]$status.error)) {
        $status.error = $_.Exception.Message
    }
    Write-Error $_.Exception.Message
}
finally {
    try {
        if (-not (Test-Path -LiteralPath $OutputDir)) {
            New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
        }
        $status.timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        Write-StatusReport -StatusPath $statusPath -Status $status
        Write-Host "Status report: $statusPath"
    }
    catch {
        Write-Warning "Failed to write status report: $($_.Exception.Message)"
    }
}

exit $script:RunCollectFinalExitCode

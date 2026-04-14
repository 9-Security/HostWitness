<#
.SYNOPSIS
    Creates a Windows scheduled task for HostWitness Agent and optionally copies output to a share (auto-return).
.DESCRIPTION
    Use this script to automate periodic collection and, optionally, copy snapshot results to a network share or local path.
    Requires running as Administrator to create the scheduled task.
.PARAMETER AgentPath
    Full path to HostWitness.Agent.exe (e.g. C:\Deploy\Agent\HostWitness.Agent.exe).
.PARAMETER OutputDir
    Output directory for snapshots (e.g. C:\Collect or \\server\share\Collect).
.PARAMETER CollectSeconds
    Collection duration in seconds (default 60).
.PARAMETER EnableEtw
    Include ETW monitoring (adds --etw).
.PARAMETER TaskName
    Name of the scheduled task (default "HostWitness Agent").
.PARAMETER Schedule
    daily | hourly | minute. For daily/hourly, use StartTime (HH:mm). For minute, use IntervalMinutes.
.PARAMETER StartTime
    For daily/hourly: start time (e.g. "02:00").
.PARAMETER IntervalMinutes
    For minute schedule: interval in minutes (e.g. 30).
.PARAMETER CopyToPath
    After each run, copy the latest snapshot_* folder to this path (e.g. \\analysis\Snapshots\HostA). Optional.
.PARAMETER IncludeEvtx
    Include Application/System/Security EVTX in snapshot raw\evtx before return.
.PARAMETER RetryCount
    Retry count for auto-return copy (default 3).
.PARAMETER RetryDelaySeconds
    Delay between retries in seconds (default 3).
.PARAMETER VerifyCopy
    Verify auto-return copy using manifest.json SHA256.
.PARAMETER StatusHmacKey
    Optional shared secret to HMAC-sign collect-status-latest.json payload.
.PARAMETER RunAsUser
    Account to run the task (default SYSTEM). Use "DOMAIN\user" for domain account.
.PARAMETER RunAsPassword
    Password for RunAsUser (required if not SYSTEM).
.EXAMPLE
    .\ScheduledCollect.ps1 -AgentPath "C:\Deploy\Agent\HostWitness.Agent.exe" -OutputDir "C:\Collect" -CollectSeconds 120 -EnableEtw -Schedule daily -StartTime "02:00"
.EXAMPLE
    .\ScheduledCollect.ps1 -AgentPath "C:\Deploy\Agent\HostWitness.Agent.exe" -OutputDir "C:\Collect" -Schedule hourly -CopyToPath "\\analysis\Snapshots\$(hostname)"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AgentPath,
    [Parameter(Mandatory = $true)]
    [string] $OutputDir,
    [int] $CollectSeconds = 60,
    [switch] $EnableEtw,
    [string] $TaskName = "HostWitness Agent",
    [ValidateSet("daily", "hourly", "minute")]
    [string] $Schedule = "daily",
    [string] $StartTime = "02:00",
    [int] $IntervalMinutes = 0,
    [string] $CopyToPath = "",
    [switch] $IncludeEvtx,
    [int] $RetryCount = 3,
    [int] $RetryDelaySeconds = 3,
    [switch] $VerifyCopy = $true,
    [string] $StatusHmacKey = "",
    [string] $RunAsUser = "SYSTEM",
    [string] $RunAsPassword = ""
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $AgentPath)) {
    Write-Error "Agent not found: $AgentPath"
    exit 1
}
$args = "`"$OutputDir`" $CollectSeconds"
if ($EnableEtw) { $args += " --etw" }
$tr = "`"$AgentPath`" $args"

$taskExists = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($taskExists) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute $AgentPath -Argument $args -WorkingDirectory ([System.IO.Path]::GetDirectoryName($AgentPath))
$trigger = switch ($Schedule) {
    "daily"  { New-ScheduledTaskTrigger -Daily -At $StartTime }
    "hourly" { New-ScheduledTaskTrigger -Once -At $StartTime -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 365) }
    "minute" { $m = if ($IntervalMinutes -gt 0) { $IntervalMinutes } else { 60 }; New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes $m) -RepetitionDuration (New-TimeSpan -Days 365) }
}
$principal = if ($RunAsUser -eq "SYSTEM") {
    New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount
} else {
    if (-not $RunAsPassword) { Write-Error "RunAsPassword required when RunAsUser is not SYSTEM" }
    New-ScheduledTaskPrincipal -UserId $RunAsUser -LogonType Password
}
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$params = @{
    TaskName   = $TaskName
    Action     = $action
    Trigger    = $trigger
    Settings   = $settings
    Principal  = $principal
}
if ($RunAsUser -ne "SYSTEM" -and $RunAsPassword) {
    $params["Password"] = $RunAsPassword
}
Register-ScheduledTask @params | Out-Null
Write-Host "Scheduled task '$TaskName' created. Schedule: $Schedule."

if ($CopyToPath) {
    # Single-quoted embedded literals: double each ' per PowerShell rules (e.g. O'Reilly -> O''Reilly)
    $embOut = $OutputDir.Replace("'", "''")
    $embDest = $CopyToPath.Replace("'", "''")
    $embKey = $StatusHmacKey.Replace("'", "''")
    $scriptBlock = @"
    `$out = '$embOut'
    `$dest = '$embDest'
    `$retryCount = $RetryCount
    `$retryDelaySeconds = $RetryDelaySeconds
    `$includeEvtx = $([bool]$IncludeEvtx)
    `$verifyCopy = $([bool]$VerifyCopy)
    `$statusHmacKey = '$embKey'

    function Copy-WithRetry {
        param([string]`$SourcePath, [string]`$DestinationPath, [int]`$Count, [int]`$Delay)
        `$attempt = 0
        while (`$true) {
            try {
                Copy-Item -Path `$SourcePath -Destination `$DestinationPath -Recurse -Force
                return
            } catch {
                `$attempt++
                if (`$attempt -gt `$Count) { throw }
                Start-Sleep -Seconds `$Delay
            }
        }
    }

    function Add-EvtxToSnapshot {
        param([string]`$SnapshotDir)
        `$logsRoot = Join-Path `$env:SystemRoot "System32\winevt\Logs"
        if (-not (Test-Path -LiteralPath `$logsRoot)) { return }
        `$evtxOut = Join-Path `$SnapshotDir "raw\evtx"
        New-Item -ItemType Directory -Path `$evtxOut -Force | Out-Null
        foreach (`$name in @("Application.evtx","System.evtx","Security.evtx")) {
            `$src = Join-Path `$logsRoot `$name
            if (Test-Path -LiteralPath `$src) {
                Copy-Item -Path `$src -Destination (Join-Path `$evtxOut `$name) -Force -ErrorAction SilentlyContinue
            }
        }
    }

    function Get-FileHashSafe {
        param([string]`$Path)
        if (-not (Test-Path -LiteralPath `$Path)) { return "" }
        try { return (Get-FileHash -Path `$Path -Algorithm SHA256).Hash.ToLowerInvariant() } catch { return "" }
    }

    function Get-StatusPayloadJson {
        param([hashtable]`$Status)
        `$clone = @{}
        foreach (`$k in `$Status.Keys) {
            if (`$k -in @("signature","signatureAlgorithm")) { continue }
            `$clone[`$k] = `$Status[`$k]
        }
        return (`$clone | ConvertTo-Json -Depth 6)
    }

    function Compute-HmacSignature {
        param([string]`$Text, [string]`$Key)
        `$encoding = [System.Text.Encoding]::UTF8
        `$keyBytes = `$encoding.GetBytes(`$Key)
        `$textBytes = `$encoding.GetBytes(`$Text)
        `$hmac = [System.Security.Cryptography.HMACSHA256]::new(`$keyBytes)
        try {
            `$hash = `$hmac.ComputeHash(`$textBytes)
            return ([System.BitConverter]::ToString(`$hash) -replace "-", "").ToLowerInvariant()
        }
        finally {
            `$hmac.Dispose()
        }
    }

    function Write-EmbeddedCollectStatus {
        param([hashtable]`$s)
        `$s.timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        if (-not [string]::IsNullOrWhiteSpace(`$statusHmacKey)) {
            `$s.signatureAlgorithm = "HMAC-SHA256"
            `$s.signature = Compute-HmacSignature -Text (Get-StatusPayloadJson -Status `$s) -Key `$statusHmacKey
        }
        if (-not (Test-Path -LiteralPath `$out)) {
            try { New-Item -ItemType Directory -Path `$out -Force | Out-Null } catch { return }
        }
        `$sp = Join-Path `$out "collect-status-latest.json"
        try { (`$s | ConvertTo-Json -Depth 6) | Set-Content -Path `$sp -Encoding UTF8 } catch { }
    }

    if (-not (Test-Path -LiteralPath `$out)) {
        Write-EmbeddedCollectStatus @{
            host = `$env:COMPUTERNAME; success = `$false; stage = "output_dir_missing"
            error = "Output directory not found; post-collection copy skipped."
            outputDir = `$out; copyToPath = `$dest; snapshotName = `$null; snapshotPath = `$null
            destinationPath = `$null; agentExitCode = `$null; exportSkipped = `$false
            includeEvtx = `$includeEvtx; verifyCopy = `$verifyCopy
            manifestHashSource = `$null; manifestHashDestination = `$null; manifestVerified = `$false
            signatureAlgorithm = `$null; signature = `$null
        }
        exit 1
    }
    `$latest = Get-ChildItem -Path `$out -Filter 'snapshot_*' -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not `$latest) {
        Write-EmbeddedCollectStatus @{
            host = `$env:COMPUTERNAME; success = `$false; stage = "no_snapshot_for_copy"
            error = "No snapshot_* folder in output directory; agent may have failed or export was skipped."
            outputDir = `$out; copyToPath = `$dest; snapshotName = `$null; snapshotPath = `$null
            destinationPath = `$null; agentExitCode = `$null; exportSkipped = `$true
            includeEvtx = `$includeEvtx; verifyCopy = `$verifyCopy
            manifestHashSource = `$null; manifestHashDestination = `$null; manifestVerified = `$false
            signatureAlgorithm = `$null; signature = `$null
        }
        exit 1
    }
    `$status = @{
        timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        host = `$env:COMPUTERNAME
        success = `$false
        stage = "init"
        error = `$null
        outputDir = `$out
        copyToPath = `$dest
        snapshotName = `$latest.Name
        snapshotPath = `$latest.FullName
        destinationPath = `$null
        agentExitCode = `$null
        exportSkipped = `$false
        includeEvtx = `$includeEvtx
        verifyCopy = `$verifyCopy
        manifestHashSource = `$null
        manifestHashDestination = `$null
        manifestVerified = `$false
        signatureAlgorithm = `$null
        signature = `$null
    }

    try {
        if (`$includeEvtx) {
            `$status.stage = "add_evtx"
            Add-EvtxToSnapshot -SnapshotDir `$latest.FullName
        }
        `$status.stage = "copy"
        if (-not (Test-Path -LiteralPath `$dest)) { New-Item -ItemType Directory -Path `$dest -Force | Out-Null }
        `$destDir = Join-Path `$dest `$latest.Name
        `$status.destinationPath = `$destDir
        Copy-WithRetry -SourcePath `$latest.FullName -DestinationPath `$destDir -Count `$retryCount -Delay `$retryDelaySeconds
        if (`$verifyCopy) {
            `$status.stage = "verify"
            `$srcManifest = Join-Path `$latest.FullName "manifest.json"
            `$dstManifest = Join-Path `$destDir "manifest.json"
            `$srcHash = Get-FileHashSafe -Path `$srcManifest
            `$dstHash = Get-FileHashSafe -Path `$dstManifest
            `$status.manifestHashSource = `$srcHash
            `$status.manifestHashDestination = `$dstHash
            `$status.manifestVerified = (`$srcHash -and `$dstHash -and `$srcHash -eq `$dstHash)
            if (-not `$status.manifestVerified) {
                throw "manifest hash verification failed"
            }
        }
        `$status.success = `$true
        `$status.stage = "done"
    }
    catch {
        `$status.success = `$false
        `$status.error = `$_.Exception.Message
    }
    finally {
        Write-EmbeddedCollectStatus `$status
    }
"@
    $postScript = [System.IO.Path]::GetTempFileName() + ".ps1"
    Set-Content -Path $postScript -Value $scriptBlock
    Write-Host "Auto-return script written to $postScript. Run it after each collection (e.g. via Task Scheduler with trigger after task '$TaskName') or use a wrapper task that runs Agent then this script."
    Write-Host "Example: run Agent, then: & '$postScript'"
}

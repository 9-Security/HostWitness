<#
.SYNOPSIS
    Runs the stable release gate for the UI/Core local release workflow.
.DESCRIPTION
    Executes automated checks and writes one JSON report under Release_Verification.
    This script does not replace the manual verification matrix. Use the matrix and
    checklist documents for manual sign-off before marking a build as stable.
.PARAMETER ReportDirectory
    Optional output directory for the JSON report. Default: <repo>\Release_Verification
.PARAMETER SkipBuild
    Skip the build step.
.PARAMETER SkipTests
    Skip the full solution test step.
.EXAMPLE
    .\scripts\InvokeStableReleaseGate.ps1
#>
[CmdletBinding()]
param(
    [string] $ReportDirectory = "",
    [switch] $SkipBuild,
    [switch] $SkipTests
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot ".." )).Path

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repoRoot "Release_Verification"
}

function New-StepResult {
    param([Parameter(Mandatory = $true)][string] $Name)

    return @{
        name = $Name
        executed = $false
        success = $false
        skipped = $false
        exitCode = $null
        command = $null
        error = $null
        details = @()
    }
}

function Complete-SkippedStep {
    param(
        [Parameter(Mandatory = $true)][hashtable] $Step,
        [Parameter(Mandatory = $true)][string] $Reason
    )

    $Step.executed = $false
    $Step.skipped = $true
    $Step.success = $true
    $Step.error = $Reason
}

function Test-RequiredDocs {
    param(
        [Parameter(Mandatory = $true)][hashtable] $Step,
        [Parameter(Mandatory = $true)][object[]] $RequiredDocs,
        [Parameter(Mandatory = $true)][string] $RepoRoot
    )

    $Step.executed = $true
    $details = @()
    $allGood = $true

    foreach ($doc in $RequiredDocs) {
        $relativePath = [string] $doc.Path
        $fullPath = Join-Path $RepoRoot $relativePath
        $exists = Test-Path -LiteralPath $fullPath
        $markers = @()
        $docSuccess = $exists

        if ($exists) {
            $content = Get-Content -Path $fullPath -Raw -Encoding UTF8
            foreach ($marker in $doc.Markers) {
                $present = $content.Contains([string] $marker)
                $markers += [ordered]@{
                    value = [string] $marker
                    present = $present
                }

                if (-not $present) {
                    $docSuccess = $false
                }
            }
        }
        else {
            foreach ($marker in $doc.Markers) {
                $markers += [ordered]@{
                    value = [string] $marker
                    present = $false
                }
            }
        }

        $details += [ordered]@{
            path = $relativePath
            exists = $exists
            success = $docSuccess
            markers = $markers
        }

        if (-not $docSuccess) {
            $allGood = $false
        }
    }

    $Step.details = $details
    $Step.success = $allGood

    if (-not $allGood) {
        $Step.error = "One or more required docs are missing or out of date."
    }
}

$requiredDocs = @(
    @{
        Path = "README.md"
        Markers = @("publish.cmd", "Stable gate", "InvokeStableReleaseGate.ps1")
    },
    @{
        Path = "docs\\README.md"
        Markers = @("StableReleaseVerificationMatrix.md", "StableReleaseChecklist.md", "StableReleaseVerificationRecordTemplate.md", "StableReleaseManualVerificationRunbook.md", "StableOperatorProfileAndSOP.md")
    },
    @{
        Path = "docs\\LIMITATIONS.md"
        Markers = @("collectionSummary", "artifactCopyWarningCount")
    },
    @{
        Path = "docs\\StableReleaseVerificationMatrix.md"
        Markers = @("UI/Core local stable scope", "Windows 10", "Windows 11", "Admin", "VSS unavailable", "Release Gate")
    },
    @{
        Path = "docs\\StableReleaseChecklist.md"
        Markers = @("Automated Gate", "Manual Verification Matrix", "Known Limitations", "Verification Record")
    },
    @{
        Path = "docs\\StableReleaseVerificationRecordTemplate.md"
        Markers = @("Stable Release Verification Record", "Automated Gate", "Manual Verification Matrix", "Approval")
    },
    @{
        Path = "docs\\StableReleaseManualVerificationRunbook.md"
        Markers = @("release operator", "Blocked", "Manual matrix execution workflow")
    },
    @{
        Path = "docs\\StableOperatorProfileAndSOP.md"
        Markers = @("Forensic Strict", "Triage Fast", "Create Dump", "High-risk isolation")
    }
)

$report = [ordered]@{
    timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    releaseScope = "ui_core_local"
    dotnetVersion = (& dotnet --version).Trim()
    reportPath = $null
    success = $false
    checks = [ordered]@{
        build = New-StepResult -Name "build"
        tests = New-StepResult -Name "tests"
        docs = New-StepResult -Name "docs"
    }
    manualFollowUp = @(
        "Run the manual matrix in docs/StableReleaseVerificationMatrix.md.",
        "Complete docs/StableReleaseChecklist.md.",
        "Attach this JSON report to the release verification record."
    )
}

Push-Location $repoRoot
try {
    # Build+test run via publish.cmd (cmd.exe) to avoid PowerShell-hosted dotnet SDK resolver noise (e.g. MSB4276).
    $publishCmd = Join-Path $repoRoot "publish.cmd"
    if (-not (Test-Path -LiteralPath $publishCmd)) {
        throw "publish.cmd not found at $publishCmd"
    }

    if ($SkipBuild) {
        Complete-SkippedStep -Step $report.checks.build -Reason "Skipped by caller."
        if ($SkipTests) {
            Complete-SkippedStep -Step $report.checks.tests -Reason "Skipped by caller."
        }
        else {
            $nugetAppData = Join-Path $repoRoot ".nuget-appdata"
            $report.checks.tests.executed = $true
            $invokeTests = "cmd.exe /d /c set APPDATA=$nugetAppData & mkdir `"$nugetAppData\\NuGet`" 2>nul & cd /d `"$repoRoot`" && dotnet test .\\WinDFIR.sln -c Release --no-restore"
            $report.checks.tests.command = $invokeTests
            & cmd.exe /d /c "set APPDATA=$nugetAppData & mkdir `"$nugetAppData\NuGet`" 2>nul & cd /d `"$repoRoot`" && dotnet test .\WinDFIR.sln -c Release --no-restore"
            $exitCode = $LASTEXITCODE
            $report.checks.tests.exitCode = $exitCode
            $report.checks.tests.success = ($exitCode -eq 0)
            if (-not $report.checks.tests.success) {
                $report.checks.tests.error = "Command failed."
            }
        }
    }
    else {
        $skipTestsArg = if ($SkipTests) { " -SkipTests" } else { "" }
        $invokeDesc = "cmd.exe /d /c cd /d `"$repoRoot`" && call `"$publishCmd`" -SkipPublish$skipTestsArg"
        $report.checks.build.executed = $true
        $report.checks.build.command = $invokeDesc
        & cmd.exe /d /c "cd /d `"$repoRoot`" && call `"$publishCmd`" -SkipPublish$skipTestsArg"
        $exitCode = $LASTEXITCODE
        $report.checks.build.exitCode = $exitCode
        $report.checks.build.success = ($exitCode -eq 0)
        if (-not $report.checks.build.success) {
            $report.checks.build.error = "publish.cmd -SkipPublish failed."
        }

        if ($SkipTests) {
            Complete-SkippedStep -Step $report.checks.tests -Reason "Skipped by caller (-SkipTests); build+test step did not run tests."
        }
        else {
            $report.checks.tests.executed = $true
            $report.checks.tests.command = $invokeDesc
            $report.checks.tests.exitCode = $exitCode
            $report.checks.tests.success = ($exitCode -eq 0)
            if (-not $report.checks.tests.success) {
                $report.checks.tests.error = "publish.cmd -SkipPublish failed (includes tests)."
            }
        }
    }

    Test-RequiredDocs -Step $report.checks.docs -RequiredDocs $requiredDocs -RepoRoot $repoRoot

    $report.success = @(
        $report.checks.build.success,
        $report.checks.tests.success,
        $report.checks.docs.success
    ) -notcontains $false
}
catch {
    $report.success = $false
    if (-not $report.error) {
        $report.error = $_.Exception.Message
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $ReportDirectory)) {
    New-Item -Path $ReportDirectory -ItemType Directory -Force | Out-Null
}

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
$reportPath = Join-Path $ReportDirectory ("stable-release-gate_{0}.json" -f $timestamp)
$report.reportPath = $reportPath
$report.timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
($report | ConvertTo-Json -Depth 10) | Set-Content -Path $reportPath -Encoding UTF8

Write-Host ("Stable release gate report: {0}" -f $reportPath)
Write-Host ("Build: {0}" -f ($(if ($report.checks.build.success) { "PASS" } else { "FAIL" })))
Write-Host ("Tests: {0}" -f ($(if ($report.checks.tests.success) { "PASS" } else { "FAIL" })))
Write-Host ("Docs: {0}" -f ($(if ($report.checks.docs.success) { "PASS" } else { "FAIL" })))

if ($report.success) {
    Write-Host "Stable release gate PASSED."
    exit 0
}

Write-Host "Stable release gate FAILED."
exit 1



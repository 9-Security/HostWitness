<#
.SYNOPSIS
    Verifies HMAC signature of collect-status-latest.json.
.DESCRIPTION
    Validates `signature` field in status JSON created by RunCollectAndCopy.ps1 or
    ScheduledCollect.ps1 (when -StatusHmacKey is used). The signature is computed over
    JSON payload excluding `signature` and `signatureAlgorithm`.
.PARAMETER StatusPath
    Path to collect-status-latest.json.
.PARAMETER HmacKey
    Shared secret key used during status generation.
.EXAMPLE
    .\VerifyCollectStatusSignature.ps1 -StatusPath "C:\Collect\collect-status-latest.json" -HmacKey "my-shared-secret"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StatusPath,
    [Parameter(Mandatory = $true)]
    [string] $HmacKey
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $StatusPath)) {
    Write-Error "Status file not found: $StatusPath"
    exit 2
}

if ([string]::IsNullOrWhiteSpace($HmacKey)) {
    Write-Error "HmacKey cannot be empty."
    exit 3
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

try {
    $raw = Get-Content -Path $StatusPath -Raw -Encoding UTF8
    $obj = $raw | ConvertFrom-Json -Depth 10
}
catch {
    Write-Error "Invalid JSON in status file: $($_.Exception.Message)"
    exit 4
}

$algorithm = [string]($obj.signatureAlgorithm)
$providedSignature = [string]($obj.signature)
if ([string]::IsNullOrWhiteSpace($providedSignature)) {
    Write-Error "No signature field found."
    exit 5
}
if (-not [string]::Equals($algorithm, "HMAC-SHA256", [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Unsupported signatureAlgorithm: '$algorithm'"
    exit 6
}

$payload = @{}
foreach ($p in $obj.PSObject.Properties) {
    if ($p.Name -eq "signature" -or $p.Name -eq "signatureAlgorithm") { continue }
    $payload[$p.Name] = $p.Value
}
$payloadJson = $payload | ConvertTo-Json -Depth 10
$expected = Compute-HmacSignature -Text $payloadJson -Key $HmacKey

if ($expected -eq $providedSignature.ToLowerInvariant()) {
    Write-Host "Status signature OK"
    Write-Host ("Host: {0} | Success: {1} | Stage: {2}" -f $obj.host, $obj.success, $obj.stage)
    exit 0
}

Write-Host "Status signature FAILED"
Write-Host ("Expected: {0}" -f $expected)
Write-Host ("Provided: {0}" -f $providedSignature)
exit 1


# Signs Release\HostWitness.exe when certs\HostWitness.pfx exists. Non-fatal if missing cert or signtool.
param(
    [Parameter(Mandatory = $true)][string] $RepoRoot
)

$ErrorActionPreference = "Continue"
$pfx = Join-Path $RepoRoot "certs\HostWitness.pfx"
$releaseDir = Join-Path $RepoRoot "Release"
$exe = Join-Path $releaseDir "HostWitness.exe"

if (-not (Test-Path -LiteralPath $pfx)) {
    Write-Host "No cert at $pfx - exe not signed."
    exit 0
}

$st = Get-Command signtool.exe -ErrorAction SilentlyContinue
$signtoolExe = if ($st) { $st.Source } else { $null }
if (-not $signtoolExe) {
    $sdkPaths = Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object { $_.FullName } -Descending
    $signtoolExe = $sdkPaths | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtoolExe) {
    Write-Host "No signtool.exe in PATH or Windows SDK - exe not signed. Install Windows SDK or add signtool to PATH."
    exit 0
}
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host "Exe not found at $exe - skip signing."
    exit 0
}

$pass = if ($env:HOSTWITNESS_PFX_PASSWORD) { $env:HOSTWITNESS_PFX_PASSWORD } else { "HostWitness" }
$url = if ($env:HOSTWITNESS_SIGNING_URL) { $env:HOSTWITNESS_SIGNING_URL } else { "https://github.com/nine-security/hostwitness" }
$ts = if ($env:HOSTWITNESS_TIMESTAMP_URL) { $env:HOSTWITNESS_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
$signAttempts = @(
    @("sign", "/f", $pfx, "/p", $pass, "/fd", "SHA256", "/tr", $ts, "/td", "SHA256", "/d", "HostWitness", "/du", $url, $exe),
    @("sign", "/f", $pfx, "/p", $pass, "/fd", "SHA256", "/t", $ts, "/d", "HostWitness", "/du", $url, $exe),
    @("sign", "/f", $pfx, "/p", $pass, "/fd", "SHA256", "/d", "HostWitness", "/du", $url, $exe)
)

$signed = $false
foreach ($args in $signAttempts) {
    $signErr = & $signtoolExe @args 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Signed: $exe"
        $signed = $true
        break
    }
}

if (-not $signed) {
    Write-Host "Signing failed after timestamp/no-timestamp retries. Check signtool, PFX password, private key permissions, and timestamp server."
    if ($signErr) { Write-Host $signErr }
}

exit 0

[CmdletBinding()]
param(
    [ValidateSet('G2')][string]$Gate = 'G2',
    [ValidatePattern('^W2G-IS-0[0-7]$')][string]$Slice,
    [Parameter(Mandatory)][string]$ProtocolManifest,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProtocolManifest -PathType Leaf)) { throw "Protocol manifest not found: $ProtocolManifest" }
$expectedManifestSha256 = '92c19e74affe876902e1c64aa5cdbca845f5dbc93a8c82014a16627a26deb8d3'
$actualManifestSha256 = (Get-FileHash -LiteralPath $ProtocolManifest -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualManifestSha256 -ne $expectedManifestSha256) {
    throw "Protocol manifest hash mismatch: expected $expectedManifestSha256, actual $actualManifestSha256"
}
if (Test-Path -LiteralPath $Output) { throw "Output directory already exists: $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
if ($env:WIRE_TO_GATE_DOTNET_EXE -and -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "WIRE_TO_GATE_DOTNET_EXE not found: $dotnet"
}
$startedAt = [DateTimeOffset]::UtcNow
& $dotnet test (Join-Path $root 'tests\SQCD.Agv.UnitTests\SQCD.Agv.UnitTests.csproj') -c Release --filter "IntegrationSlice=$Slice" --logger "trx;LogFileName=onboard-$Slice.trx" --results-directory $Output
$testExitCode = $LASTEXITCODE
[ordered]@{
    schemaVersion = '1.0.0'
    gate = $Gate
    integrationSliceId = $Slice
    status = if ($testExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    startedAt = $startedAt.ToString('O')
    finishedAt = ([DateTimeOffset]::UtcNow).ToString('O')
    implementationRepository = '8005-agv-onboard-hmi'
    implementationCommit = (git -c safe.directory=$root -C $root rev-parse HEAD).Trim()
    protocolRepositoryCommit = '3ad309ffd5f9a48a6cf390b51a81da2f47c814dd'
    protocolManifestSha256 = $actualManifestSha256
    testExitCode = $testExitCode
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'gate-result.json') -Encoding utf8NoBOM
exit $testExitCode

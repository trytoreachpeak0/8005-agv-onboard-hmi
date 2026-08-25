[CmdletBinding()]
param(
    [ValidateSet('G2')][string]$Gate = 'G2',
    [ValidatePattern('^W2G-IS-0[0-7]$')][string]$Slice,
    [Parameter(Mandatory)][string]$ProtocolManifest,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProtocolManifest -PathType Leaf)) { throw "Protocol manifest not found: $ProtocolManifest" }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
if ($env:WIRE_TO_GATE_DOTNET_EXE -and -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "WIRE_TO_GATE_DOTNET_EXE not found: $dotnet"
}
& $dotnet test (Join-Path $root 'tests\SQCD.Agv.UnitTests\SQCD.Agv.UnitTests.csproj') -c Release --filter "IntegrationSlice=$Slice" --logger "trx;LogFileName=onboard-$Slice.trx" --results-directory $Output
exit $LASTEXITCODE

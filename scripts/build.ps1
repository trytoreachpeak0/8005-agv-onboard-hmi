[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
if ($env:WIRE_TO_GATE_DOTNET_EXE -and -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "WIRE_TO_GATE_DOTNET_EXE not found: $dotnet"
}
& $dotnet restore (Join-Path $root 'SQCD_8005AGV.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build (Join-Path $root 'SQCD_8005AGV.sln') -c $Configuration --no-restore
exit $LASTEXITCODE

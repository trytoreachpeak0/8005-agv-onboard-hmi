[CmdletBinding()]
param(
    [string]$ProtocolRoot = '',
    [string]$SimulatorRoot = ''
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot

Write-Host '=== protocol + HMI G2 evidence ==='
$g2Arguments = @()
if (-not [string]::IsNullOrWhiteSpace($ProtocolRoot)) {
    $g2Arguments += @('-ProtocolRoot', $ProtocolRoot)
}
& (Join-Path $scriptRoot 'run-w2g-g2.ps1') @g2Arguments

Write-Host ''
Write-Host '=== slots-simulator black-box ==='
$simArguments = @()
if (-not [string]::IsNullOrWhiteSpace($SimulatorRoot)) {
    $simArguments += @('-SimulatorRoot', $SimulatorRoot)
}
& (Join-Path $scriptRoot 'run-slots-simulator-tests.ps1') @simArguments

Write-Host ''
Write-Host '=== W2G / Legacy boundary ==='
& (Join-Path $scriptRoot 'audit-w2g-boundary.ps1')

Write-Host ''
Write-Host '=== UI layout ==='
& (Join-Path $scriptRoot 'check-ui-layout.ps1')

Write-Host ''
Write-Host '本机计划中的可执行验证全部通过；ControlServer、真实车辆信号、现场明文网络和硬件仍保留为外部门禁。'

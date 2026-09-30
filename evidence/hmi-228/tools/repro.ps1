#Requires -Version 7
<#
Runs a filtered slice of the G2 test project N times without rebuilding and keeps each run's full output.
Used to look for the two tests of onboard-hmi#228 that are only known by their symptom.

    pwsh -File repro.ps1 -OutDir <dir> -Label <name> -Filter <vstest filter> [-Runs 5]
#>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Label,
    [Parameter(Mandatory)][string]$Filter,
    [int]$Runs = 5
)

$ErrorActionPreference = 'Stop'
$worktree = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi228-8005-agv-onboard-hmi'
New-Item -ItemType Directory -Force $OutDir | Out-Null
Set-Location $worktree
foreach ($run in 1..$Runs) {
    $log = Join-Path $OutDir "$Label.run$run.log"
    & dotnet test tests/SQCD.Agv.WireToGateG2Tests -c Release --no-build --filter $Filter *> $log
    $summary = Select-String -LiteralPath $log -Pattern '^(Passed|Failed)!' | Select-Object -Last 1
    $failed = @(Select-String -LiteralPath $log -Pattern '^\s+Failed (\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    Write-Host "run$run exit=$LASTEXITCODE $($summary?.Line) $($failed -join ' | ')"
}

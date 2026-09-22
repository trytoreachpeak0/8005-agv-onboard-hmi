#Requires -Version 7
# Runs one G2 test N times back to back (--no-build) and records each outcome.
param(
    [Parameter(Mandatory)][string]$Worktree,
    [Parameter(Mandatory)][int]$Count,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Filter = 'FullyQualifiedName~WritingTheResultObservationNeverDropsAPendingResultRecordedBeforeIt'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
Set-Location $Worktree
[Environment]::CurrentDirectory = $Worktree
$project = 'tests/SQCD.Agv.WireToGateG2Tests'
$summary = [System.Collections.Generic.List[string]]::new()
$failed = 0
$dll = Join-Path $Worktree 'tests/SQCD.Agv.WireToGateG2Tests/bin/Debug/net8.0-windows/SQCD.Agv.WireToGateG2Tests.dll'
$dll = (Test-Path $dll) ? $dll : (Get-ChildItem (Join-Path $Worktree 'tests/SQCD.Agv.WireToGateG2Tests/bin') -Recurse -Filter SQCD.Agv.WireToGateG2Tests.dll | Select-Object -First 1).FullName
$summary.Add("test dll=$dll lastWrite=$((Get-Item $dll).LastWriteTime.ToString('o')) sha256=$((Get-FileHash $dll).Hash)")
$summary.Add("worktree=$Worktree head=$(git rev-parse HEAD) dotnet=$(dotnet --version) count=$Count filter=$Filter")
for ($i = 1; $i -le $Count; $i++) {
    $log = Join-Path $OutDir ('run-{0:D2}.txt' -f $i)
    $output = dotnet test $project --no-build --filter $Filter 2>&1 | Out-String
    $code = $LASTEXITCODE
    $passed = if ($output -match 'Passed:\s+(\d+)') { [int]$Matches[1] } else { -1 }
    $fail = if ($output -match 'Failed:\s+(\d+)') { [int]$Matches[1] } else { -1 }
    $line = "run {0:D2}: exit={1} passed={2} failed={3}" -f $i, $code, $passed, $fail
    if ($code -ne 0 -or $passed -ne 1) {
        $failed++
        $output | Set-Content -Path $log -Encoding utf8NoBOM
        $line += " -> $log"
    }
    $summary.Add($line)
    Write-Host $line
}
$summary.Add("TOTAL: $Count runs, $failed not passed")
$summary | Set-Content -Path (Join-Path $OutDir 'summary.txt') -Encoding utf8NoBOM
Write-Host "TOTAL: $Count runs, $failed not passed"
exit ($failed -eq 0 ? 0 : 1)

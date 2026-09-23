#Requires -Version 7
# Rebuilds run10.ps1's per-run summary lines from the raw per-run outputs (<tag>-<state>-<n>.txt), with the same parsing.
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string[]] $States
)

$ErrorActionPreference = 'Stop'
$States = @($States | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$sp = 'C:/Users/szy/AppData/Local/Temp/claude/C--Users-szy-Desktop-8005-workspace-v2/bf6b5571-299c-4809-abb2-bb7c48bda32f/scratchpad'
foreach ($state in $States) {
    "================ $state"
    for ($i = 1; $i -le 10; $i++) {
        $out = "$sp/$Tag-$state-$i.txt"
        if (-not (Test-Path $out)) { "  run ${i}: MISSING"; continue }
        $text = Get-Content $out -Raw
        $summary = if ($text -match 'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+\d+, Total:\s+(\d+)') {
            "fail=$($Matches[1]) pass=$($Matches[2]) total=$($Matches[3])"
        } else { 'NO SUMMARY' }
        $red = [regex]::Matches($text, 'MultiDemandJourneyG2Tests\.(\S+?)(\((?:next|trigger): \w+\))? \[FAIL\]') |
            ForEach-Object { ($_.Groups[1].Value -replace 'ASafetyReportStillInFlightStaysOutOfTheNextHandshake', 'inflight') + $_.Groups[2].Value } |
            Sort-Object -Unique
        $lines = [regex]::Matches($text, 'HandshakeWindowIntrusion\.cs:line (\d+)') | ForEach-Object { $_.Groups[1].Value } |
            Group-Object | Sort-Object Count -Descending | Select-Object -First 3 | ForEach-Object { "$($_.Name)x$($_.Count)" }
        "  run ${i}: $summary | red: $(if ($red) { $red -join ' ' } else { 'none' }) | at: $($lines -join ' ')"
    }
}

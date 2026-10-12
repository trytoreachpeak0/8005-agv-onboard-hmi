#Requires -Version 7
<#
.SYNOPSIS
Compares the safety messages the G2 fake control server received across runs (onboard-hmi#314).

.DESCRIPTION
Run the G2 tests with HMI314_PROBE_DIR set and FakeControlServer writes, per test, every SafetyStateSnapshot,
SafetyStateChanged and PreDepartureSafetyCheckResult line it received, in order. This script normalises the fields that
differ between runs by construction and compares the rest byte for byte, order and count included:

- sentAt, payload.observedAt, payload.validUntil: the real clock (SystemClock) the G2 clients run on;
- messageId: StableUuid of a deduplication key that contains observedAt;
- correlationId, payload.preDepartureSafetyCheckId: random ids the tests generate for a pre-departure check.

A test is in the comparison only when every baseline run agrees on it: some scenarios send a different number of
safety messages from run to run, decided by IO and reconnect timing, and are reported as unstable instead.

Exit code 0 when every candidate run matches the baseline on every stable test, 1 otherwise.

.PARAMETER Baseline
Probe directories of runs on the integration branch: the expectation.

.PARAMETER Candidate
Probe directories of runs on the branch under test. Omit to only report which baseline tests are stable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Baseline,

    [string[]]$Candidate = @()
)

$ErrorActionPreference = 'Stop'
$volatile = [System.Collections.Generic.HashSet[string]]::new(
    [string[]]@('sentAt', 'messageId', 'observedAt', 'validUntil', 'correlationId', 'preDepartureSafetyCheckId'),
    [System.StringComparer]::Ordinal)

function ConvertTo-Normalised([object]$Node) {
    if ($Node -is [System.Collections.IDictionary]) {
        $sorted = [ordered]@{}
        foreach ($key in ($Node.Keys | Sort-Object -CaseSensitive)) {
            $sorted[$key] = $volatile.Contains($key) ? '<V>' : (ConvertTo-Normalised $Node[$key])
        }
        return $sorted
    }

    if ($Node -is [System.Collections.IList]) {
        return , @($Node | ForEach-Object { ConvertTo-Normalised $_ })
    }

    return $Node
}

# file name -> normalised lines ("connection|json")
function Read-Run([string]$Directory) {
    $run = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Filter '*.txt') {
        $run[$file.Name] = @(
            Get-Content -LiteralPath $file.FullName -Encoding utf8 | Where-Object { $_ } | ForEach-Object {
                $connection, $wire = $_ -split '\|', 2
                $json = ConvertTo-Normalised ($wire | ConvertFrom-Json -AsHashtable)
                "$connection|$(ConvertTo-Json $json -Depth 32 -Compress)"
            })
    }
    return $run
}

function Test-Same([string[]]$Left, [string[]]$Right) {
    $Left.Count -eq $Right.Count -and -not (Compare-Object $Left $Right -SyncWindow 0 -CaseSensitive)
}

$baselineRuns = @($Baseline | ForEach-Object { Read-Run $_ })
$names = $baselineRuns[0].Keys | Sort-Object
$stable = [System.Collections.Generic.List[string]]::new()
$unstable = [System.Collections.Generic.List[string]]::new()
foreach ($name in $names) {
    $agrees = $true
    foreach ($run in $baselineRuns | Select-Object -Skip 1) {
        if (-not $run.ContainsKey($name) -or -not (Test-Same $baselineRuns[0][$name] $run[$name])) {
            $agrees = $false
        }
    }

    ($agrees ? $stable : $unstable).Add($name)
}

$messageCount = ($stable | ForEach-Object { $baselineRuns[0][$_].Count } | Measure-Object -Sum).Sum
Write-Output "baseline: $($baselineRuns.Count) runs, $($names.Count) test files, $($stable.Count) stable ($messageCount messages), $($unstable.Count) unstable"
$unstable | ForEach-Object { Write-Output "  unstable: $_" }

$differences = 0
foreach ($directory in $Candidate) {
    $run = Read-Run $directory
    foreach ($name in $stable) {
        if (-not $run.ContainsKey($name)) {
            $differences++
            Write-Output "MISSING in ${directory}: $name"
            continue
        }

        $expected = $baselineRuns[0][$name]
        $actual = $run[$name]
        if (-not (Test-Same $expected $actual)) {
            $differences++
            Write-Output "DIFF in ${directory}: $name ($($expected.Count) expected, $($actual.Count) actual)"
            for ($index = 0; $index -lt [Math]::Max($expected.Count, $actual.Count); $index++) {
                if ($expected[$index] -cne $actual[$index]) {
                    Write-Output "  expected: $($expected[$index])"
                    Write-Output "  actual:   $($actual[$index])"
                    break
                }
            }
        }
    }

    Write-Output "candidate ${directory}: $($stable.Count) stable tests compared"
}

if ($Candidate.Count -gt 0) {
    Write-Output ($differences -eq 0 ? 'RESULT: identical' : "RESULT: $differences differences")
}

exit ($differences -eq 0 ? 0 : 1)

#Requires -Version 7
<#
The acceptance harness for onboard-hmi#295: one multi-slice call of scripts/run-w2g-g2.ps1 against
the same slices run one call each, on the same commit.

    pwsh -File evidence/hmi295/compare-g2-slice-modes.ps1 -ProtocolRoot <plain clone> -OutputRoot <new dir> -Slices FP-IS-04,FP-IS-01,FP-IS-10
    pwsh -File evidence/hmi295/compare-g2-slice-modes.ps1 -ProtocolRoot <plain clone> -OutputRoot <new dir> -Slices FP-IS-04,FP-IS-01,FP-IS-10 -ExpectFailSlice FP-IS-01 -HmiRoot <mutated copy>

Compare mode (no -ExpectFailSlice) passes when:
  - the multi-slice call exits 0 and writes one multi-slice directory whose transcript records the
    build, dotnet format and protocol G1 exactly once each;
  - every slice has its own evidence directory with summary.json and gate-result.json;
  - per slice, the multi-slice evidence and the one-call evidence agree on status, selected and
    recorded test counts, the trx outcome counts, and schemaConformance.

Red mode (-ExpectFailSlice) runs only the multi-slice call and passes when it exits non-zero, the
named slice's gate-result.json says FAIL and every other slice's says PASS.

The multi-slice call runs first so a script that does not take several slices fails in seconds.
Exit code 0 = the expectation held, 1 = it did not. comparison.json in -OutputRoot records why.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProtocolRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$Slices,
    [string]$HmiRoot = '',
    [string]$ExpectFailSlice = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($HmiRoot)) {
    $HmiRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$OutputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw "OutputRoot must be a new directory: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

$sliceIds = @($Slices -split ',' | ForEach-Object Trim | Where-Object { $_ })
$script = Join-Path $HmiRoot 'scripts/run-w2g-g2.ps1'
$problems = [System.Collections.Generic.List[string]]::new()
$report = [ordered]@{
    mode = $ExpectFailSlice ? 'red' : 'compare'
    hmiRoot = $HmiRoot
    hmiCommit = (& git -C $HmiRoot rev-parse HEAD).Trim()
    hmiWorkingTreeStatus = @(& git -C $HmiRoot status --porcelain)
    slices = $sliceIds
    expectFailSlice = $ExpectFailSlice
}

function Invoke-G2 {
    param([string[]]$Arguments, [string]$EvidenceRoot, [string]$LogName)
    $log = Join-Path $OutputRoot $LogName
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & pwsh -NoProfile -File $script -ProtocolRoot $ProtocolRoot -EvidenceRoot $EvidenceRoot @Arguments *>&1 |
        Out-File -LiteralPath $log -Encoding utf8
    $exit = $LASTEXITCODE
    $watch.Stop()
    [ordered]@{ exitCode = $exit; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1); log = (Split-Path -Leaf $log) }
}

# One slice's evidence, reduced to the facts the two modes must agree on.
function Read-SliceFacts {
    param([string]$EvidenceRoot, [string]$Slice)
    $dirs = @(Get-ChildItem -Path (Join-Path $EvidenceRoot "protocol-v*/$Slice/*") -Directory -ErrorAction SilentlyContinue)
    if ($dirs.Count -ne 1) {
        return [ordered]@{ problem = "$Slice：期望 1 个证据目录，找到 $($dirs.Count) 个（$EvidenceRoot）" }
    }
    $dir = $dirs[0].FullName
    $gatePath = Join-Path $dir 'gate-result.json'
    $summaryPath = Join-Path $dir 'summary.json'
    if (-not (Test-Path -LiteralPath $gatePath) -or -not (Test-Path -LiteralPath $summaryPath)) {
        return [ordered]@{ problem = "$Slice：$dir 缺 gate-result.json 或 summary.json" }
    }
    $gate = Get-Content -LiteralPath $gatePath -Raw | ConvertFrom-Json
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    $outcomes = [ordered]@{ Passed = 0; Failed = 0; NotExecuted = 0; Other = 0 }
    foreach ($trx in @(Get-ChildItem -LiteralPath (Join-Path $dir 'test-results') -Filter '*.trx' -File)) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $trx.FullName -Raw), '<UnitTestResult\b[^>]*\boutcome="([^"]+)"')) {
            $key = $m.Groups[1].Value
            if ($outcomes.Contains($key)) { $outcomes[$key]++ } else { $outcomes['Other']++ }
        }
    }
    $schema = $gate.schemaConformance
    [ordered]@{
        directory = $dir
        gateStatus = $gate.status
        summaryStatus = $summary.status
        selectedTestCount = $gate.selectedTestCount
        recordedTestCount = $gate.recordedTestCount
        trxOutcomes = $outcomes
        schemaConformance = if ($null -eq $schema) { $null } else {
            [ordered]@{
                linesChecked = $schema.linesChecked
                linesInViolation = $schema.linesInViolation
                linesInKnownViolation = $schema.linesInKnownViolation
            }
        }
        buildExitCode = $gate.buildExitCode
        testExitCode = $gate.testExitCode
        formatExitCode = $gate.formatExitCode
        g1Status = $summary.protocol.g1Status
    }
}

# --- the multi-slice call ---------------------------------------------------------------------
$newRoot = Join-Path $OutputRoot 'multi-slice'
$report['multiSliceRun'] = Invoke-G2 -Arguments @('-Slice', ($sliceIds -join ',')) -EvidenceRoot $newRoot -LogName 'multi-slice.console.log'

$multiDirs = @(Get-ChildItem -Path (Join-Path $newRoot 'protocol-v*/multi-slice/*') -Directory -ErrorAction SilentlyContinue)
if ($multiDirs.Count -ne 1) {
    $problems.Add("多片调用应写出 1 个 multi-slice 目录，实际 $($multiDirs.Count) 个")
} else {
    $transcriptPath = Join-Path $multiDirs[0].FullName 'transcript.ndjson'
    $events = @(Get-Content -LiteralPath $transcriptPath -ErrorAction SilentlyContinue | ForEach-Object { $_ | ConvertFrom-Json })
    $counts = [ordered]@{
        build = @($events | Where-Object { $_.kind -eq 'command.completed' -and $_.data.name -eq 'dotnet-build-release' }).Count
        format = @($events | Where-Object { $_.kind -eq 'command.completed' -and $_.data.name -eq 'dotnet-format-verify' }).Count
        g1 = @($events | Where-Object { $_.kind -eq 'protocol.g1.completed' }).Count
    }
    $report['multiSliceSharedStepCounts'] = $counts
    foreach ($k in $counts.Keys) {
        if ($counts[$k] -ne 1) { $problems.Add("多片调用的 $k 应恰好 1 次，transcript 记了 $($counts[$k]) 次") }
    }
}

$newFacts = [ordered]@{}
foreach ($s in $sliceIds) { $newFacts[$s] = Read-SliceFacts $newRoot $s }
$report['multiSliceFacts'] = $newFacts

if ($ExpectFailSlice) {
    if ($report.multiSliceRun.exitCode -eq 0) { $problems.Add('注入失败后多片调用的退出码仍是 0') }
    foreach ($s in $sliceIds) {
        $f = $newFacts[$s]
        if ($f.Contains('problem')) { $problems.Add($f.problem); continue }
        $want = $s -eq $ExpectFailSlice ? 'FAIL' : 'PASS'
        if ($f.gateStatus -ne $want) { $problems.Add("$s 的 gate status 是 $($f.gateStatus)，期望 $want") }
    }
} else {
    if ($report.multiSliceRun.exitCode -ne 0) { $problems.Add("多片调用退出码 $($report.multiSliceRun.exitCode)") }
    if ($problems.Count -eq 0) {
        # --- the same slices, one call each ------------------------------------------------------
        $oldRoot = Join-Path $OutputRoot 'one-call-per-slice'
        $oldRuns = [ordered]@{}
        $oldFacts = [ordered]@{}
        foreach ($s in $sliceIds) {
            $oldRuns[$s] = Invoke-G2 -Arguments @('-Slice', $s) -EvidenceRoot $oldRoot -LogName "one-call-$s.console.log"
            if ($oldRuns[$s].exitCode -ne 0) { $problems.Add("逐片调用 $s 退出码 $($oldRuns[$s].exitCode)") }
            $oldFacts[$s] = Read-SliceFacts $oldRoot $s
        }
        $report['oneCallPerSliceRuns'] = $oldRuns
        $report['oneCallPerSliceFacts'] = $oldFacts
        $report['seconds'] = [ordered]@{
            multiSlice = $report.multiSliceRun.seconds
            oneCallPerSlice = [math]::Round(($oldRuns.Values | ForEach-Object { $_.seconds } | Measure-Object -Sum).Sum, 1)
        }

        $compared = 'gateStatus', 'summaryStatus', 'selectedTestCount', 'recordedTestCount', 'trxOutcomes', 'schemaConformance', 'buildExitCode', 'testExitCode', 'formatExitCode', 'g1Status'
        foreach ($s in $sliceIds) {
            foreach ($side in $newFacts[$s], $oldFacts[$s]) {
                if ($side.Contains('problem')) { $problems.Add($side.problem) }
            }
            if ($newFacts[$s].Contains('problem') -or $oldFacts[$s].Contains('problem')) { continue }
            foreach ($field in $compared) {
                $a = $newFacts[$s][$field] | ConvertTo-Json -Compress -Depth 5
                $b = $oldFacts[$s][$field] | ConvertTo-Json -Compress -Depth 5
                if ($a -ne $b) { $problems.Add("$s.$field 不一致：多片 $a，逐片 $b") }
            }
            if ($newFacts[$s].gateStatus -ne 'PASS') { $problems.Add("$s 状态 $($newFacts[$s].gateStatus)，对照要求两边都 PASS") }
        }
    }
}

$report['problems'] = @($problems)
$report['verdict'] = $problems.Count -eq 0 ? 'PASS' : 'FAIL'
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputRoot 'comparison.json') -Encoding utf8
Write-Host "verdict: $($report.verdict)"
$problems | ForEach-Object { Write-Host "  - $_" }
exit ($problems.Count -eq 0 ? 0 : 1)

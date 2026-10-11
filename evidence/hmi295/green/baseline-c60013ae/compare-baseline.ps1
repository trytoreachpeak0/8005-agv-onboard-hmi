#Requires -Version 7
param([string]$Old, [string]$New, [string]$Out)
function Facts($root, $s) {
  $d = @(Get-ChildItem -Path (Join-Path $root "protocol-v*/$s/*") -Directory)
  if ($d.Count -ne 1) { throw "$s dirs $($d.Count) in $root" }
  $dir = $d[0].FullName
  $g = Get-Content (Join-Path $dir gate-result.json) -Raw | ConvertFrom-Json
  $sm = Get-Content (Join-Path $dir summary.json) -Raw | ConvertFrom-Json
  $o = [ordered]@{ Passed = 0; Failed = 0; Other = 0 }
  foreach ($t in Get-ChildItem (Join-Path $dir test-results) -Filter *.trx) { foreach ($m in [regex]::Matches((Get-Content $t.FullName -Raw), '<UnitTestResult\b[^>]*\boutcome="([^"]+)"')) { $k = $m.Groups[1].Value; if ($o.Contains($k)) { $o[$k]++ } else { $o.Other++ } } }
  $names = @(foreach ($t in Get-ChildItem (Join-Path $dir test-results) -Filter *.trx) { [regex]::Matches((Get-Content $t.FullName -Raw), '<UnitTestResult\b[^>]*\btestName="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } }) | Sort-Object
  [ordered]@{ gateStatus = $g.status; summaryStatus = $sm.status; selected = $g.selectedTestCount; recorded = $g.recordedTestCount; trx = $o; testNames = $names
    schema = $g.schemaConformance ? [ordered]@{ linesChecked = $g.schemaConformance.linesChecked; linesInViolation = $g.schemaConformance.linesInViolation; linesInKnownViolation = $g.schemaConformance.linesInKnownViolation } : $null
    exits = "$($g.buildExitCode)/$($g.testExitCode)/$($g.formatExitCode)"; g1 = $sm.protocol.g1Status
    gateFields = @($g.PSObject.Properties.Name); summaryFields = @($sm.PSObject.Properties.Name)
    files = @(Get-ChildItem $dir -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($dir, $_.FullName).Replace('\','/') } | Where-Object { $_ -notmatch '\.trx$' } | Sort-Object) }
}
$problems = @(); $rows = [ordered]@{}
foreach ($s in 'FP-IS-04','FP-IS-01','FP-IS-10') {
  $a = Facts $Old $s; $b = Facts $New $s
  $rows[$s] = [ordered]@{ baseline = $a; multiSlice = $b }
  foreach ($k in 'gateStatus','summaryStatus','selected','recorded','trx','testNames','schema','exits','g1','files') {
    if (($a[$k] | ConvertTo-Json -Compress -Depth 5) -ne ($b[$k] | ConvertTo-Json -Compress -Depth 5)) { $problems += "$s.$k 不一致" }
  }
  $addedGate = @($b.gateFields | Where-Object { $_ -notin $a.gateFields }); $lostGate = @($a.gateFields | Where-Object { $_ -notin $b.gateFields })
  $addedSum = @($b.summaryFields | Where-Object { $_ -notin $a.summaryFields }); $lostSum = @($a.summaryFields | Where-Object { $_ -notin $b.summaryFields })
  $rows[$s]['fieldDelta'] = [ordered]@{ gateAdded = $addedGate; gateLost = $lostGate; summaryAdded = $addedSum; summaryLost = $lostSum }
  if ($lostGate.Count -or $lostSum.Count) { $problems += "$s 丢了字段" }
}
[ordered]@{ verdict = $problems.Count ? 'FAIL' : 'PASS'; problems = $problems; slices = $rows } | ConvertTo-Json -Depth 8 | Set-Content $Out
"verdict: $($problems.Count ? 'FAIL' : 'PASS')"; $problems
foreach ($s in $rows.Keys) { "$s fieldDelta: $($rows[$s].fieldDelta | ConvertTo-Json -Compress)  baseline sel=$($rows[$s].baseline.selected) schema=$($rows[$s].baseline.schema | ConvertTo-Json -Compress)" }

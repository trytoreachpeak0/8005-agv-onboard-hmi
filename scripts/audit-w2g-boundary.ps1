[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$hmiRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $hmiRoot 'src'
$files = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs'
$failures = [System.Collections.Generic.List[string]]::new()

function Require-Text {
    param([string]$Path, [string]$Pattern, [string]$Description)
    $text = Get-Content -LiteralPath $Path -Raw
    if ($text -notmatch $Pattern) {
        $null = $failures.Add($Description)
    }
}

$mesMatches = $files | Select-String -Pattern 'MesIngest' -SimpleMatch
if ($mesMatches) {
    $null = $failures.Add('HMI source must not read MesIngest.')
}

$appPath = Join-Path $hmiRoot 'src\SQCD.Agv.Wpf\App.xaml.cs'
Require-Text $appPath 'settings\.WireToGate\.Enabled\s*\?[\s\S]*DisabledRuleGateway' 'W2G mode must select DisabledRuleGateway for the legacy rule port.'

$clientPath = Join-Path $hmiRoot 'src\SQCD.Agv.Infrastructure\WireToGateSessionClient.cs'
Require-Text $clientPath 'CurrentStopWorklistSnapshot|UpcomingStopPlanSnapshot' 'W2G client must consume the two server-owned journey snapshots.'

$journeyPath = Join-Path $hmiRoot 'src\SQCD.Agv.Core\WireToGateJourney.cs'
Require-Text $journeyPath 'CanAcceptSublotAt' 'Journey projection must gate input from the server-owned worklist.'

$boundary = [ordered]@{
    audit = 'W2G_LEGACY_BOUNDARY'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    hmiCommit = (& git -C $hmiRoot rev-parse HEAD).Trim()
    checks = [ordered]@{
        noMesIngestReference = ($null -eq $mesMatches)
        w2gDisablesLegacyRuleGateway = ($failures -notcontains 'W2G mode must select DisabledRuleGateway for the legacy rule port.')
        serverOwnedJourneySnapshots = ($failures -notcontains 'W2G client must consume the two server-owned journey snapshots.')
        noLocalJourneySelection = ($failures -notcontains 'Journey projection must gate input from the server-owned worklist.')
    }
    failures = @($failures)
    status = if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
}

$auditDirectory = Join-Path $hmiRoot 'evidence\audits'
New-Item -ItemType Directory -Force -Path $auditDirectory | Out-Null
$auditPath = Join-Path $auditDirectory ('w2g-boundary-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.json')
$boundary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $auditPath -Encoding utf8
Write-Host "W2G boundary audit: $auditPath"
Write-Host "Status: $($boundary.status)"
if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

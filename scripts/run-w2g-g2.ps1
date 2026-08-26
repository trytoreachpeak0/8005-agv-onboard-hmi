[CmdletBinding()]
param(
    [string]$ProtocolRoot = '',
    [string]$EvidenceRoot = '',
    [switch]$SkipProtocolG1
)

$ErrorActionPreference = 'Stop'
$hmiRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ProtocolRoot)) {
    $ProtocolRoot = Join-Path (Split-Path -Parent $hmiRoot) '8005-agv-protocol'
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $hmiRoot 'evidence\g2'
}

$expected = [ordered]@{
    ProtocolVersion = 1
    ProfileId = 'WIRE_TO_GATE_MVP'
    ReleaseVersion = '0.1.1'
    Repository = '8005-agv-protocol'
    Tag = 'protocol-v0.1.1'
    Commit = '1531489e42e328f28bfe0c51ed3f8c56e5ce0279'
    ManifestSha256 = 'a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f'
    SchemaBundleSha256 = 'e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c'
    VectorsSha256 = 'fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e'
}

$failures = [System.Collections.Generic.List[string]]::new()
$runUtc = [DateTime]::UtcNow
$runId = $runUtc.ToString('yyyyMMddTHHmmssfffZ')
$hmiCommit = (& git -C $hmiRoot rev-parse HEAD).Trim()
$shortHmiCommit = if ($hmiCommit.Length -ge 12) { $hmiCommit.Substring(0, 12) } else { $hmiCommit }
$runDirectory = Join-Path (Join-Path $EvidenceRoot 'protocol-v0.1.1') ($runId + '-' + $shortHmiCommit)
$logsDirectory = Join-Path $runDirectory 'logs'
$resultsDirectory = Join-Path $runDirectory 'test-results'
New-Item -ItemType Directory -Force -Path $logsDirectory, $resultsDirectory | Out-Null

$transcriptPath = Join-Path $runDirectory 'transcript.ndjson'
$journalPath = Join-Path $runDirectory 'journal.ndjson'
$transcript = [System.Collections.Generic.List[string]]::new()
$journal = [System.Collections.Generic.List[string]]::new()

function Add-Event {
    param(
        [System.Collections.Generic.List[string]]$Target,
        [string]$Kind,
        [hashtable]$Data = @{}
    )

    $event = [ordered]@{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        kind = $Kind
        data = $Data
    }
    $null = $Target.Add(($event | ConvertTo-Json -Compress -Depth 20))
}

function Add-Failure {
    param([string]$Message)
    $null = $failures.Add($Message)
    Write-Warning $Message
}

function Assert-Equal {
    param(
        [string]$Name,
        [object]$Actual,
        [object]$Expected
    )

    if ([string]$Actual -ne [string]$Expected) {
        Add-Failure "$Name mismatch: actual='$Actual', expected='$Expected'"
    }
}

function Read-CSharpConstant {
    param(
        [string]$Source,
        [string]$Name,
        [string]$Type = 'string'
    )

    $escapedName = [regex]::Escape($Name)
    $pattern = if ($Type -eq 'int') {
        'public\s+const\s+int\s+' + $escapedName + '\s*=\s*(\d+)\s*;'
    } else {
        'public\s+const\s+string\s+' + $escapedName + '\s*=\s*"([^"]+)"\s*;'
    }
    $match = [regex]::Match($Source, $pattern)
    if (-not $match.Success) {
        Add-Failure "HMI contract constant not found: $Name"
        return ''
    }
    return $match.Groups[1].Value
}

function Invoke-LoggedCommand {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$LogPath
    )

    Add-Event $transcript 'command.started' @{ name = $Name; arguments = $Arguments }
    Push-Location $hmiRoot
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $output | Out-File -LiteralPath $LogPath -Encoding utf8
    } finally {
        Pop-Location
    }
    Add-Event $transcript 'command.completed' @{ name = $Name; exitCode = $exitCode; log = (Split-Path -Leaf $LogPath) }
    if ($exitCode -ne 0) {
        Add-Failure "$Name exited with code $exitCode"
    }
    return [pscustomobject]@{
        Name = $Name
        ExitCode = $exitCode
        Output = ($output -join [Environment]::NewLine)
        LogPath = $LogPath
    }
}

function Invoke-ProtocolG1 {
    param([string]$LogPath)

    $candidateDrives = @('X', 'Y', 'Z', 'W', 'V') |
        Where-Object { -not (Test-Path -LiteralPath ($_ + ':\')) }
    if ($candidateDrives.Count -eq 0) {
        Add-Failure '没有可用于规避 Windows # 路径编码问题的临时盘符。'
        return [pscustomobject]@{ ExitCode = 1; Output = ''; Status = 'NOT_RUN' }
    }

    $drive = $candidateDrives[0]
    $driveName = $drive + ':'
    Add-Event $transcript 'protocol.g1.started' @{ drive = $driveName }
    & subst $driveName $ProtocolRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "无法创建协议仓库临时盘符 $driveName"
        return [pscustomobject]@{ ExitCode = 1; Output = ''; Status = 'NOT_RUN' }
    }

    try {
        Push-Location ($driveName + '\')
        try {
            $output = & pnpm g1 2>&1
            $exitCode = $LASTEXITCODE
            $output | Out-File -LiteralPath $LogPath -Encoding utf8
        } finally {
            Pop-Location
        }
    } finally {
        & subst $driveName /D | Out-Null
    }

    $text = $output -join [Environment]::NewLine
    $status = if ($text -match '"status"\s*:\s*"PASS"') { 'PASS' } else { 'FAIL' }
    Add-Event $transcript 'protocol.g1.completed' @{
        exitCode = $exitCode
        status = $status
        log = (Split-Path -Leaf $LogPath)
    }
    if ($exitCode -ne 0 -or $status -ne 'PASS') {
        Add-Failure "protocol G1 未通过：exitCode=$exitCode, status=$status"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $text; Status = $status }
}

Add-Event $transcript 'run.started' @{
    evidenceType = 'ONBOARD_HMI_LOCAL_G2'
    runId = $runId
    hmiRoot = $hmiRoot
    protocolRoot = $ProtocolRoot
}
Add-Event $journal 'run.started' @{ runId = $runId; evidenceDirectory = $runDirectory }

if (-not (Test-Path -LiteralPath (Join-Path $ProtocolRoot 'manifest\release.json'))) {
    throw "找不到 protocol manifest：$ProtocolRoot"
}
$contractPath = Join-Path $hmiRoot 'src\SQCD.Agv.Contracts\WireToGateProtocol.cs'
$contractSource = Get-Content -LiteralPath $contractPath -Raw
$hmiIdentity = [ordered]@{
    ProtocolVersion = [int](Read-CSharpConstant $contractSource 'ProtocolVersion' 'int')
    ProfileId = Read-CSharpConstant $contractSource 'ProfileId'
    ReleaseVersion = Read-CSharpConstant $contractSource 'ReleaseVersion'
    Tag = Read-CSharpConstant $contractSource 'Tag'
    Commit = Read-CSharpConstant $contractSource 'Commit'
    ManifestSha256 = Read-CSharpConstant $contractSource 'ManifestSha256'
    SchemaBundleSha256 = Read-CSharpConstant $contractSource 'SchemaBundleSha256'
    VectorsSha256 = Read-CSharpConstant $contractSource 'VectorsSha256'
}

foreach ($key in $expected.Keys) {
    if ($key -eq 'Repository') {
        continue
    }
    Assert-Equal "HMI identity.$key" $hmiIdentity[$key] $expected[$key]
}

$protocolCommit = (& git -C $ProtocolRoot rev-parse HEAD).Trim()
$protocolTagCommit = (& git -C $ProtocolRoot rev-list -n 1 ($expected.Tag + '^{commit}')).Trim()
Assert-Equal 'protocol HEAD' $protocolCommit $expected.Commit
Assert-Equal 'protocol tag commit' $protocolTagCommit $expected.Commit

$release = Get-Content -LiteralPath (Join-Path $ProtocolRoot 'manifest\release.json') -Raw | ConvertFrom-Json
Assert-Equal 'release.protocolVersion' $release.protocolVersion $expected.ProtocolVersion
Assert-Equal 'release.profileId' $release.profileId $expected.ProfileId
Assert-Equal 'release.releaseVersion' $release.releaseVersion $expected.ReleaseVersion
Assert-Equal 'release.schemaBundleSha256' $release.schemaBundleSha256 $expected.SchemaBundleSha256
Assert-Equal 'release.vectorsSha256' $release.vectorsSha256 $expected.VectorsSha256

$index = Get-Content -LiteralPath (Join-Path $ProtocolRoot 'integration-slices\index.json') -Raw | ConvertFrom-Json
$is00 = $index.slices | Where-Object integrationSliceId -eq 'W2G-IS-00'
$is01 = $index.slices | Where-Object integrationSliceId -eq 'W2G-IS-01'
Assert-Equal 'IS-00 vector count' $is00.vectorIds.Count 4
Assert-Equal 'IS-01 vector count' $is01.vectorIds.Count 1
Assert-Equal 'IS-01 vector' ($is01.vectorIds -join ',') 'CV-DEMAND-ACCEPT-TO-PICKUP'

$g1Result = $null
if (-not $SkipProtocolG1) {
    $g1Result = Invoke-ProtocolG1 (Join-Path $logsDirectory 'protocol-g1.log')
} else {
    Add-Event $transcript 'protocol.g1.skipped' @{ reason = 'SkipProtocolG1' }
}

$build = Invoke-LoggedCommand -Name 'dotnet-build-release' -FilePath 'dotnet' -Arguments @('build', '.\SQCD_8005AGV.slnx', '-c', 'Release') -LogPath (Join-Path $logsDirectory 'dotnet-build-release.log')
$test = Invoke-LoggedCommand -Name 'dotnet-test-release' -FilePath 'dotnet' -Arguments @('test', '.\SQCD_8005AGV.slnx', '-c', 'Release', '--no-build', '--results-directory', $resultsDirectory) -LogPath (Join-Path $logsDirectory 'dotnet-test-release.log')
$format = Invoke-LoggedCommand -Name 'dotnet-format-verify' -FilePath 'dotnet' -Arguments @('format', '.\SQCD_8005AGV.slnx', '--verify-no-changes', '--no-restore') -LogPath (Join-Path $logsDirectory 'dotnet-format-verify.log')

$g1Text = if ($g1Result) { $g1Result.Output } else { '' }
$g1ManifestMatch = [regex]::Match($g1Text, '"candidateManifestSha256"\s*:\s*"([^"]+)"')
$protocolStatus = if ($g1Result) { $g1Result.Status } else { 'SKIPPED' }
$hmiStatus = if ($build.ExitCode -eq 0 -and $test.ExitCode -eq 0 -and $format.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
$testSummaryMatch = [regex]::Match($test.Output, 'Total tests:\s*(\d+).*?Passed:\s*(\d+).*?Failed:\s*(\d+)', [System.Text.RegularExpressions.RegexOptions]::Singleline)

Add-Event $transcript 'evidence.journal.bound' @{
    protocolCommit = $protocolCommit
    hmiCommit = $hmiCommit
    g1Status = $protocolStatus
    hmiStatus = $hmiStatus
}
Add-Event $journal 'protocol.identity.checked' @{
    protocolCommit = $protocolCommit
    protocolTag = $expected.Tag
    manifestSha256 = if ($g1ManifestMatch.Success) { $g1ManifestMatch.Groups[1].Value } else { $hmiIdentity.ManifestSha256 }
    schemaBundleSha256 = $expected.SchemaBundleSha256
    vectorsSha256 = $expected.VectorsSha256
}
Add-Event $journal 'hmi.validation.completed' @{
    buildExitCode = $build.ExitCode
    testExitCode = $test.ExitCode
    formatExitCode = $format.ExitCode
    testSummary = if ($testSummaryMatch.Success) { $testSummaryMatch.Value } else { 'unparsed' }
}

$summary = [ordered]@{
    schemaVersion = '1.0.0'
    evidenceType = 'ONBOARD_HMI_LOCAL_G2'
    status = if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
    generatedAtUtc = $runUtc.ToString('o')
    runId = $runId
    hmi = [ordered]@{
        commit = $hmiCommit
        branch = (& git -C $hmiRoot branch --show-current).Trim()
        workingTreeStatus = @(& git -C $hmiRoot status --porcelain)
        protocolIdentity = $hmiIdentity
    }
    protocol = [ordered]@{
        repository = $expected.Repository
        headCommit = $protocolCommit
        tag = $expected.Tag
        tagCommit = $protocolTagCommit
        g1Status = $protocolStatus
        g1CandidateManifestSha256 = if ($g1ManifestMatch.Success) { $g1ManifestMatch.Groups[1].Value } else { $null }
        releaseFile = 'manifest/release.json'
        schemaBundleSha256 = $release.schemaBundleSha256
        vectorsSha256 = $release.vectorsSha256
    }
    commands = [ordered]@{
        build = [ordered]@{ exitCode = $build.ExitCode; log = 'logs/dotnet-build-release.log' }
        test = [ordered]@{ exitCode = $test.ExitCode; log = 'logs/dotnet-test-release.log'; resultsDirectory = 'test-results' }
        format = [ordered]@{ exitCode = $format.ExitCode; log = 'logs/dotnet-format-verify.log' }
    }
    slices = @(
        [ordered]@{
            integrationSliceId = 'W2G-IS-00'
            vectorIds = @($is00.vectorIds)
            onboardHmiG2 = $hmiStatus
            controlServerG2 = 'PENDING_EXTERNAL'
            g3 = 'PENDING_JOINT'
            forbidUnclosedFailOrInconclusive = [bool]$is00.forbidUnclosedFailOrInconclusive
        },
        [ordered]@{
            integrationSliceId = 'W2G-IS-01'
            vectorIds = @($is01.vectorIds)
            onboardHmiG2 = $hmiStatus
            controlServerG2 = 'PENDING_EXTERNAL'
            g3 = 'PENDING_JOINT'
            demandMode = 'READ_ONLY_COMMITTED_PROJECTION'
            demandSource = 'CONTROL_SERVER_SNAPSHOTS_ONLY'
            controlServerOutcomes = @(
                'MESINGEST_FINAL_REREAD',
                'ATOMIC_DEMAND_ACCEPTANCE',
                'DEDUPLICATED_TO_PICKUP_INTENT',
                'RIOT_ORDER_RECONCILIATION',
                'TRUSTED_PICKUP_ARRIVAL'
            )
            forbidUnclosedFailOrInconclusive = [bool]$is01.forbidUnclosedFailOrInconclusive
        }
    )
    failures = @($failures)
    artifacts = [ordered]@{
        transcript = 'transcript.ndjson'
        journal = 'journal.ndjson'
        logs = 'logs'
        testResults = 'test-results'
    }
    knownLimitations = @(
        '本证据是 OnboardHmi 本机 G2；ControlServer G2 和联合 G3 仍需外部/现场门禁。',
        'G1 使用临时盘符运行，仅规避 Windows 工作区路径含 # 时的 Node URL 解码问题，不改变协议仓库内容。',
        '真实车辆停稳信号、Modbus/锁/门/光幕和现场 TLS 未在本机证据中宣称完成。'
    )
}

Add-Event $transcript 'run.completed' @{ status = $summary.status; failures = $failures.Count }
$transcript | Set-Content -LiteralPath $transcriptPath -Encoding utf8
$journal | Set-Content -LiteralPath $journalPath -Encoding utf8
$summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding utf8

Write-Host "G2 evidence: $runDirectory"
Write-Host "Status: $($summary.status)"
if ($failures.Count -gt 0) {
    throw ("本机 G2 证据生成失败：" + [Environment]::NewLine + ($failures -join [Environment]::NewLine))
}

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
    ProtocolVersion = 3
    ProfileId = 'WIRE_TO_GATE_MVP'
    ReleaseVersion = '0.3.0'
    Repository = '8005-agv-protocol'
    Tag = 'protocol-v0.3.0'
    Commit = '345c53c58517968192c87c3e7777ed08ddb48726'
    ManifestSha256 = 'b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138'
    SchemaBundleSha256 = '68bfd531c4b9c08bc80f6d9c5a67264891efa200acdb154eb18e1d083bf4ed98'
    VectorsSha256 = 'bd272b63a1d0663d61c4a38d6e8633d7e7d4f7b561a7915c3df51c7a93bd4576'
}

$failures = [System.Collections.Generic.List[string]]::new()
$runUtc = [DateTime]::UtcNow
$runId = $runUtc.ToString('yyyyMMddTHHmmssfffZ')
$hmiCommit = (& git -C $hmiRoot rev-parse HEAD).Trim()
$shortHmiCommit = if ($hmiCommit.Length -ge 12) { $hmiCommit.Substring(0, 12) } else { $hmiCommit }
$runDirectory = Join-Path (Join-Path $EvidenceRoot 'protocol-v0.3.0') ($runId + '-' + $shortHmiCommit)
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
Assert-Equal 'protocol tag commit' $protocolTagCommit $expected.Commit
$tagIsAncestor = $false
$hasProtocolIdentity = -not [string]::IsNullOrWhiteSpace($protocolCommit) -and -not [string]::IsNullOrWhiteSpace($protocolTagCommit)
if ($hasProtocolIdentity) {
    & git -C $ProtocolRoot merge-base --is-ancestor $protocolTagCommit $protocolCommit | Out-Null
    $tagIsAncestor = $LASTEXITCODE -eq 0
}
if (-not $tagIsAncestor) {
    Add-Failure "protocol HEAD 不是 $($expected.Tag) release 的后继提交：head=$protocolCommit, tag=$protocolTagCommit"
}

$release = Get-Content -LiteralPath (Join-Path $ProtocolRoot 'manifest\release.json') -Raw | ConvertFrom-Json
Assert-Equal 'release.protocolVersion' $release.protocolVersion $expected.ProtocolVersion
Assert-Equal 'release.profileId' $release.profileId $expected.ProfileId
Assert-Equal 'release.releaseVersion' $release.releaseVersion $expected.ReleaseVersion
Assert-Equal 'release.schemaBundleSha256' $release.schemaBundleSha256 $expected.SchemaBundleSha256
Assert-Equal 'release.vectorsSha256' $release.vectorsSha256 $expected.VectorsSha256

$indexRelativePath = 'integration-slices\index.json'
$indexPath = Join-Path $ProtocolRoot $indexRelativePath
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
if (@($index.slices).Count -eq 0) {
    throw "协议仓的 $indexRelativePath 没有任何切片：$indexPath"
}

# 本仓测试按 [Trait("IntegrationSlice", ...)] 分组，而那些标注由
# IntegrationSliceCoverageArchitectureTests 对着 vendored 的同一份 index.json 校验。
# 两份必须逐字节相同，否则「标注覆盖了所有切片」这句话说的是另一个切片表。
$vendoredIndexPath = Join-Path $hmiRoot ('vendor\8005-agv-protocol\' + $expected.Tag + '\' + $indexRelativePath)
if (-not (Test-Path -LiteralPath $vendoredIndexPath)) {
    Add-Failure "找不到 vendored 切片索引：$vendoredIndexPath"
} else {
    $releaseIndexHash = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $vendoredIndexHash = (Get-FileHash -LiteralPath $vendoredIndexPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Equal 'vendored integration-slices/index.json sha256' $vendoredIndexHash $releaseIndexHash
}

$g1Result = $null
if (-not $SkipProtocolG1) {
    $g1Result = Invoke-ProtocolG1 (Join-Path $logsDirectory 'protocol-g1.log')
} else {
    Add-Event $transcript 'protocol.g1.skipped' @{ reason = 'SkipProtocolG1' }
}

$build = Invoke-LoggedCommand -Name 'dotnet-build-release' -FilePath 'dotnet' -Arguments @('build', '.\SQCD_8005AGV.sln', '-c', 'Release') -LogPath (Join-Path $logsDirectory 'dotnet-build-release.log')
$test = Invoke-LoggedCommand -Name 'dotnet-test-release' -FilePath 'dotnet' -Arguments @('test', '.\SQCD_8005AGV.sln', '-c', 'Release', '--no-build', '--results-directory', $resultsDirectory) -LogPath (Join-Path $logsDirectory 'dotnet-test-release.log')
$format = Invoke-LoggedCommand -Name 'dotnet-format-verify' -FilePath 'dotnet' -Arguments @('format', '.\SQCD_8005AGV.sln', '--verify-no-changes', '--no-restore') -LogPath (Join-Path $logsDirectory 'dotnet-format-verify.log')

# 整仓那一趟是**前置**：它跑掉全部 193 条，包括没有挂切片标注的横切测试，以及守着标注本身的
# IntegrationSliceCoverageArchitectureTests。前置不绿，任何切片都不许声称 PASS。
$preconditionStatus = if ($build.ExitCode -eq 0 -and $test.ExitCode -eq 0 -and $format.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }

# 切片结论来自它自己那批测试，不是整仓那个结论的复制。
$sliceRuns = [ordered]@{}
foreach ($slice in $index.slices) {
    $sliceId = [string]$slice.integrationSliceId
    $sliceResults = Join-Path $resultsDirectory $sliceId
    New-Item -ItemType Directory -Force -Path $sliceResults | Out-Null
    $sliceRun = Invoke-LoggedCommand `
        -Name ('dotnet-test-' + $sliceId) `
        -FilePath 'dotnet' `
        -Arguments @('test', '.\SQCD_8005AGV.sln', '-c', 'Release', '--no-build', '--filter', ('IntegrationSlice=' + $sliceId), '--results-directory', $sliceResults) `
        -LogPath (Join-Path $logsDirectory ('dotnet-test-' + $sliceId + '.log'))

    # VSTest 对「过滤器一条都没选中」返回 0。不数一遍的话，标注写错会让切片静默变绿——
    # 那正是这次改造要消灭的失败形态，所以空结果在这里当失败。
    $passed = 0
    $failed = 0
    foreach ($match in [regex]::Matches($sliceRun.Output, 'Failed:\s*(\d+),\s*Passed:\s*(\d+)')) {
        $failed += [int]$match.Groups[1].Value
        $passed += [int]$match.Groups[2].Value
    }
    if ($passed -eq 0 -and $failed -eq 0) {
        Add-Failure "$sliceId 的过滤器一条测试都没选中：标注缺失或写错，证据不能为它出结论"
    }

    $sliceStatus = if ($preconditionStatus -eq 'PASS' -and $sliceRun.ExitCode -eq 0 -and $passed -gt 0) { 'PASS' } else { 'FAIL' }
    Add-Event $journal 'slice.validated' @{
        integrationSliceId = $sliceId
        status = $sliceStatus
        passed = $passed
        failed = $failed
        exitCode = $sliceRun.ExitCode
    }
    $sliceRuns[$sliceId] = [pscustomobject]@{
        Status = $sliceStatus
        Passed = $passed
        Failed = $failed
        ExitCode = $sliceRun.ExitCode
        Log = 'logs/dotnet-test-' + $sliceId + '.log'
        Results = 'test-results/' + $sliceId
    }
}

$g1Text = if ($g1Result) { $g1Result.Output } else { '' }
$g1ManifestMatch = [regex]::Match($g1Text, '"candidateManifestSha256"\s*:\s*"([^"]+)"')
$protocolStatus = if ($g1Result) { $g1Result.Status } else { 'SKIPPED' }
$hmiStatus = if ($preconditionStatus -eq 'PASS' -and @($sliceRuns.Values | Where-Object Status -ne 'PASS').Count -eq 0) { 'PASS' } else { 'FAIL' }
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
    sliceCount = $sliceRuns.Count
    slicesPassed = @($sliceRuns.Values | Where-Object Status -eq 'PASS').Count
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
        tagIsAncestorOfHead = $tagIsAncestor
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
    precondition = [ordered]@{
        status = $preconditionStatus
        scope = 'WHOLE_SOLUTION_BUILD_TEST_FORMAT'
        note = '含未挂切片标注的横切测试，以及守着标注本身的 IntegrationSliceCoverageArchitectureTests。'
    }
    slices = @(
        foreach ($slice in $index.slices) {
            $sliceId = [string]$slice.integrationSliceId
            $run = $sliceRuns[$sliceId]
            $entry = [ordered]@{
                integrationSliceId = $sliceId
                vectorIds = @($slice.vectorIds)
                onboardHmiG2 = $run.Status
                controlServerG2 = 'PENDING_EXTERNAL'
                g3 = 'PENDING_JOINT'
                testFilter = 'IntegrationSlice=' + $sliceId
                testsPassed = $run.Passed
                testsFailed = $run.Failed
                testLog = $run.Log
                testResults = $run.Results
                forbidUnclosedFailOrInconclusive = [bool]$slice.forbidUnclosedFailOrInconclusive
            }
            if ($sliceId -eq 'W2G-IS-01') {
                $entry['demandMode'] = 'READ_ONLY_COMMITTED_PROJECTION'
                $entry['demandSource'] = 'CONTROL_SERVER_SNAPSHOTS_ONLY'
                $entry['controlServerOutcomes'] = @(
                    'MESINGEST_FINAL_REREAD',
                    'ATOMIC_DEMAND_ACCEPTANCE',
                    'DEDUPLICATED_TO_PICKUP_INTENT',
                    'RIOT_ORDER_RECONCILIATION',
                    'TRUSTED_PICKUP_ARRIVAL'
                )
            }
            $entry
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
        '真实车辆停稳信号、Modbus/锁/门/光幕和现场明文网络未在本机证据中宣称完成。',
        '切片结论来自 [Trait("IntegrationSlice", ...)] 选出的测试子集：证据声称的是「本仓有这些具名测试覆盖该切片的车载端职责」，不是「该切片端到端已验证」——端到端属于 G3。',
        '标注本身由 IntegrationSliceCoverageArchitectureTests 守卫：切片集双向相等、每切片下限 3 条、vendored index.json 按哈希钉字节。'
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

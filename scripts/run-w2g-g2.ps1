[CmdletBinding()]
param(
    [string]$ProtocolRoot = '',
    [string]$EvidenceRoot = '',
    # Without this the run tests the whole solution and writes one verdict, which is all
    # ONBOARD_HMI_G2 could ever say before the IntegrationSlice trait existed: FP-IS-00 and
    # FP-IS-01 shared a conclusion and the other six slices did not appear at all. With it the run
    # certifies exactly one slice and writes a gate-result.json for it, so eight runs make the eight
    # per-slice artefacts track A's recertification is defined in terms of.
    #
    # The pattern is the frozen sixteen. A slice that is in the family but has no test here is
    # refused rather than certified -- see the preflight below.
    [ValidatePattern('^FP-IS-(0[0-9]|1[0-5])$')][string]$Slice,
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

# 协议 v2 候选的身份。这是本仓库的第二份副本，权威副本是
# src/SQCD.Agv.Contracts/WireToGateProtocol.cs 的 WireToGateRelease；
# ProtocolIdentityArchitectureTests.TheGateScriptExpectsTheSameIdentityAsTheAssembly
# 逐字段比对这两份，任一处漂移即测试红。
#
# Tag 指向一个还没打的 tag：规格 6.6 第 6 条要产品负责人 attestation ＋ 注释 tag
# （规格原文写两名；协议治理 2026-09-08 改为一名，v2 候选 2026-09-12 跟上）
# protocol-v1.0.0，两件都没发生（协议仓 git tag --list 只有 v0.1.0/v0.1.1/v0.2.0/v0.3.0）。
# 这个字段仍写它，是因为 $defs/ProtocolReleaseIdentity 对 tag 是 required ＋ minLength 1 ＋
# ^protocol-v；ApprovalStatus 承担「它还没被批准」这半句。所以下面绑的是 commit，不是 tag。
$expected = [ordered]@{
    ProtocolVersion = 2
    ProfileId = 'AGV_FULL_PRODUCT'
    ReleaseVersion = '1.0.0'
    Repository = '8005-agv-protocol'
    Tag = 'protocol-v1.0.0'
    Commit = '16e2567a7033883f00fc999f7fa08f954dd13a26'
    ManifestSha256 = '25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0'
    SchemaBundleSha256 = '225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff'
    VectorsSha256 = '51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df'
    ApprovalStatus = 'SUPERSEDING_CANDIDATE'
}

$failures = [System.Collections.Generic.List[string]]::new()
$runUtc = [DateTime]::UtcNow
$runId = $runUtc.ToString('yyyyMMddTHHmmssfffZ')
$hmiCommit = (& git -C $hmiRoot rev-parse HEAD).Trim()
$shortHmiCommit = if ($hmiCommit.Length -ge 12) { $hmiCommit.Substring(0, 12) } else { $hmiCommit }

# The slice preflight runs before any directory exists, and before the Release build the run would
# otherwise pay for. A filter that selects nothing exits 0 -- measured on this solution, 2026-09-09,
# `--filter "IntegrationSlice=FP-IS-13"` returned exit code 0 over zero executed tests -- so without
# this a slice nobody has built here would be written out as PASS. That is the defect ticket 14
# closed on the control server side (docs/defects/20260908-empty-slice-filter-mints-a-green-g2.md);
# this is the same hole on this end.
#
# Refusing, rather than writing INCONCLUSIVE evidence. An evidence directory that exists is a run
# that happened; a slice with no tests here has nothing to run, and the honest artefact is none.
#
# A test is recognised by the shape of its fully qualified name, not by how VSTest indents it.
# `^\s*` accepts today's four-space indent and would accept none; what the line has to be is
# SQCD.Agv.<assembly>.<something>, and the second dot is the load-bearing part.
#
# Why that dot matters here: this preflight runs before the script's own build step, so
# `dotnet test --list-tests` builds, and MSBuild prints one "  SQCD.Agv.Core -> ...\*.dll" line per
# project. Measured on 2026-09-09, before the dot was required: -Slice FP-IS-04 counted 11 where the
# truth is 2 -- the nine build lines plus the two tests -- and wrote that 11 into gate-result.json.
# A fully qualified test name has a dot after the assembly's namespace; a build line has a space.
$isSliceRun = -not [string]::IsNullOrWhiteSpace($Slice)
$sliceIndexPath = Join-Path $ProtocolRoot 'integration-slices\index.json'
$sliceEntry = $null
$selectedTestCount = $null
$sliceIndexSha256 = $null
if ($isSliceRun) {
    if (-not (Test-Path -LiteralPath $sliceIndexPath -PathType Leaf)) {
        throw "找不到切片索引：$sliceIndexPath"
    }
    $sliceIndexSha256 = (Get-FileHash -LiteralPath $sliceIndexPath -Algorithm SHA256).Hash.ToLowerInvariant()

    # @() so a duplicated integrationSliceId in the index is a loud count mismatch rather than an
    # array quietly flattening its vectorIds into the evidence.
    $sliceMatches = @((Get-Content -LiteralPath $sliceIndexPath -Raw | ConvertFrom-Json).slices |
        Where-Object { $_.integrationSliceId -eq $Slice })
    if ($sliceMatches.Count -ne 1) {
        throw "切片索引里 '$Slice' 匹配到 $($sliceMatches.Count) 条，应当恰好 1 条：$sliceIndexPath"
    }
    $sliceEntry = $sliceMatches[0]

    Push-Location $hmiRoot
    try {
        $listed = @(& dotnet test '.\SQCD_8005AGV.sln' -c Release --list-tests --filter "IntegrationSlice=$Slice" |
            Where-Object { $_ -match '^\s*SQCD\.Agv\.\S+\.\S' })
        $listExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    # Checked, because --list-tests builds. A compile break also produces zero matching lines, and
    # reporting that as "this slice has no tests" would blame the slice for a broken tree. The build
    # this run logs comes later; this one's output is on the console, which is where a preflight
    # failure belongs -- no evidence directory exists yet.
    if ($listExitCode -ne 0) {
        throw ("列举切片 '$Slice' 的测试失败（exit=$listExitCode）。这通常是构建坏了，" +
               '不是这一片没有测试——先修构建再出证。')
    }

    $selectedTestCount = $listed.Count
    if ($selectedTestCount -eq 0) {
        throw ("切片 '$Slice' 在本仓选不中任何测试，ONBOARD_HMI_G2 没有东西可证。" +
               "它的向量是 " + ($sliceEntry.vectorIds -join ', ') + '。')
    }
}

$runRoot = Join-Path $EvidenceRoot $expected.Tag
if ($isSliceRun) {
    $runRoot = Join-Path $runRoot $Slice
}
$runDirectory = Join-Path $runRoot ($runId + '-' + $shortHmiCommit)
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

# G1 要 node 与 pnpm，这台机器上两者都不在 PATH 上，而协议仓也不带 node_modules。
# 隔壁 run-staged-g3.ps1 早就处理了同一件事（它的第 33-61 行与第 2189-2194 行），
# 这里照抄它的三步而不是另发明一套：
#   1. node 不在 PATH 就用 codex runtime 里那份，**并把它的目录前置到 PATH**——
#      少这一步 pnpm 自己起得来，但它派生的 `node tools/g1-validate.mjs` 起不来；
#   2. pnpm 不在 PATH 就用 node 直接跑随 node 一起装的那份 pnpm.cjs；
#   3. g1 校验依赖 ajv，协议仓没有 node_modules，所以跑 g1 之前先 install。
#
# 2026-09-09 逐步实测过：只补第 2 步时 `pnpm g1` 报
# `'node' is not recognized as an internal or external command`；三步齐全时 G1 返回
# "status": "PASS"，candidateManifestSha256 与协议仓已提交的 evidence/g1-result.json 逐字段相同。
function Initialize-ProtocolG1Toolchain {
    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    $nodeExecutable = if ($null -ne $nodeCommand) {
        $nodeCommand.Source
    } else {
        Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
    }
    if (-not (Test-Path -LiteralPath $nodeExecutable -PathType Leaf)) { return $null }

    $nodeDirectory = Split-Path -Parent $nodeExecutable
    if (($env:PATH -split ';') -notcontains $nodeDirectory) {
        $env:PATH = "$nodeDirectory;$env:PATH"
    }

    $pnpmCommand = Get-Command pnpm -ErrorAction SilentlyContinue
    if ($null -ne $pnpmCommand) {
        return [pscustomobject]@{ FilePath = $pnpmCommand.Source; PrefixArguments = @() }
    }
    $bundledPnpm = Join-Path (Split-Path -Parent $nodeDirectory) 'node_modules\pnpm\bin\pnpm.cjs'
    if (-not (Test-Path -LiteralPath $bundledPnpm -PathType Leaf)) { return $null }
    return [pscustomobject]@{ FilePath = $nodeExecutable; PrefixArguments = @($bundledPnpm) }
}

function Invoke-ProtocolG1 {
    param([string]$LogPath)

    # Initialize-，不是 Resolve-：它会改 $env:PATH。进程作用域，跑完就没了，但名字要说出来。
    $toolchain = Initialize-ProtocolG1Toolchain
    if ($null -eq $toolchain) {
        Add-Failure '这台机器上找不到可用的 node/pnpm，协议 G1 无法运行。'
        return [pscustomobject]@{ ExitCode = 1; Output = ''; Status = 'NOT_RUN' }
    }

    # g1-validate.mjs 把仓库里的文件逐个与 manifest 的清单对账，排除项写作
    # `p.startsWith(".git/")`——那条规则假定 .git 是目录。协议仓若是一个 linked worktree，
    # 它的 .git 是一个内含 `gitdir: ...` 的文件，排除不掉，于是多出恰好一个条目，
    # G1 报 `manifest file count` ＋ `manifest missing .git` 而 FAIL。
    #
    # 那不是协议内容的问题，把它报成 FAIL 会诬告候选。协议仓不能为此修改：
    # g1-validate.mjs 在 manifest 的清单里，改它就改掉 manifestSha256，两端所有身份绑定全废。
    # 所以在这里认出这个形状并如实说明。run-staged-g3.ps1 不受影响——它用 git clone
    # 取协议仓，那种 .git 是目录。
    $protocolGitPath = Join-Path $ProtocolRoot '.git'
    if (Test-Path -LiteralPath $protocolGitPath -PathType Leaf) {
        Add-Failure ("协议仓 $ProtocolRoot 是一个 linked worktree（.git 是文件），" +
            'g1-validate.mjs 的 .git/ 排除规则对它不成立，G1 必然 FAIL 在 manifest file count。' +
            '请把 -ProtocolRoot 指向一份普通克隆，或用 -SkipProtocolG1 并在证据里说明。')
        return [pscustomobject]@{ ExitCode = 1; Output = ''; Status = 'NOT_RUN' }
    }

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
            # --frozen-lockfile：装的就是锁文件里那几个包，不会顺手改动被测仓库的依赖状态。
            # node_modules 在协议仓的 .gitignore 里，装完那个仓的工作树仍然干净。
            $installArguments = $toolchain.PrefixArguments + @('install', '--frozen-lockfile')
            $installOutput = & $toolchain.FilePath $installArguments 2>&1
            $installExitCode = $LASTEXITCODE
            if ($installExitCode -ne 0) {
                $installOutput | Out-File -LiteralPath $LogPath -Encoding utf8
                # 起了就要有对应的收尾事件，否则 transcript 上这一段悬着，读的人分不清
                # 「装依赖失败」与「跑到一半被杀」。
                Add-Event $transcript 'protocol.g1.completed' @{
                    exitCode = $installExitCode
                    status = 'NOT_RUN'
                    stage = 'install'
                    log = (Split-Path -Leaf $LogPath)
                }
                Add-Failure "协议依赖安装失败，G1 未运行：exitCode=$installExitCode"
                return [pscustomobject]@{
                    ExitCode = $installExitCode
                    Output = ($installOutput -join [Environment]::NewLine)
                    Status = 'NOT_RUN'
                }
            }
            $g1Arguments = $toolchain.PrefixArguments + @('g1')
            $output = & $toolchain.FilePath $g1Arguments 2>&1
            $exitCode = $LASTEXITCODE
            # @() 包一层再相加：两边都可能是单个字符串，而 'a' + @('b','c') 在 PowerShell 里
            # 是字符串拼接（得到 "ab c"），会把整份 G1 日志压成一行。
            (@($installOutput) + @($output)) | Out-File -LiteralPath $LogPath -Encoding utf8
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
    integrationSliceId = $Slice
    selectedTestCount = $selectedTestCount
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
    ApprovalStatus = Read-CSharpConstant $contractSource 'ApprovalStatus'
}

foreach ($key in $expected.Keys) {
    if ($key -eq 'Repository') {
        continue
    }
    Assert-Equal "HMI identity.$key" $hmiIdentity[$key] $expected[$key]
}

$protocolCommit = (& git -C $ProtocolRoot rev-parse HEAD).Trim()

# tag 还没打，所以绑的是候选 commit 本身。tag 一旦打出来必须指向同一个 commit，
# 打错地方比没打更危险，所以这里查「存在则必须相等」而不是「必须存在」。
$protocolTagCommit = (& git -C $ProtocolRoot rev-list -n 1 ($expected.Tag + '^{commit}') 2>$null)
$protocolTagCommit = if ($null -eq $protocolTagCommit) { '' } else { ([string]$protocolTagCommit).Trim() }
$tagExists = -not [string]::IsNullOrWhiteSpace($protocolTagCommit)
if ($tagExists -and $protocolTagCommit -ne $expected.Commit) {
    Add-Failure "$($expected.Tag) 已存在但指向 $protocolTagCommit，不是候选 commit $($expected.Commit)"
}

$candidateIsAncestor = $false
if (-not [string]::IsNullOrWhiteSpace($protocolCommit)) {
    & git -C $ProtocolRoot merge-base --is-ancestor $expected.Commit $protocolCommit | Out-Null
    $candidateIsAncestor = $LASTEXITCODE -eq 0
}
if (-not $candidateIsAncestor) {
    Add-Failure "protocol HEAD 不是候选 commit 的后继提交：head=$protocolCommit, candidate=$($expected.Commit)"
}

$release = Get-Content -LiteralPath (Join-Path $ProtocolRoot 'manifest\release.json') -Raw | ConvertFrom-Json
Assert-Equal 'release.protocolVersion' $release.protocolVersion $expected.ProtocolVersion
Assert-Equal 'release.profileId' $release.profileId $expected.ProfileId
Assert-Equal 'release.releaseVersion' $release.releaseVersion $expected.ReleaseVersion
Assert-Equal 'release.schemaBundleSha256' $release.schemaBundleSha256 $expected.SchemaBundleSha256
Assert-Equal 'release.vectorsSha256' $release.vectorsSha256 $expected.VectorsSha256

$index = Get-Content -LiteralPath (Join-Path $ProtocolRoot 'integration-slices\index.json') -Raw | ConvertFrom-Json
$is00 = $index.slices | Where-Object integrationSliceId -eq 'FP-IS-00'
$is01 = $index.slices | Where-Object integrationSliceId -eq 'FP-IS-01'
Assert-Equal 'IS-00 vector count' $is00.vectorIds.Count 4
Assert-Equal 'IS-01 vector count' $is01.vectorIds.Count 1
Assert-Equal 'IS-01 vector' ($is01.vectorIds -join ',') 'CV-DEMAND-ACCEPT-TO-PICKUP'

$g1Result = $null
if (-not $SkipProtocolG1) {
    $g1Result = Invoke-ProtocolG1 (Join-Path $logsDirectory 'protocol-g1.log')
} else {
    Add-Event $transcript 'protocol.g1.skipped' @{ reason = 'SkipProtocolG1' }
}

$build = Invoke-LoggedCommand -Name 'dotnet-build-release' -FilePath 'dotnet' -Arguments @('build', '.\SQCD_8005AGV.sln', '-c', 'Release') -LogPath (Join-Path $logsDirectory 'dotnet-build-release.log')

# Build and format stay whole-repository even for a slice run. They are properties of the tree, not
# of the slice, and narrowing them would leave each slice's evidence covering only the files that
# slice happens to touch.
$testArguments = @('test', '.\SQCD_8005AGV.sln', '-c', 'Release', '--no-build', '--results-directory', $resultsDirectory)
if ($isSliceRun) {
    # LogFilePrefix, not LogFileName. This solution has two test projects and a slice can select
    # tests in both; LogFileName gives them the same path and the second silently overwrites the
    # first. Measured on 2026-09-09: -Slice FP-IS-01 selected 5 tests and left one .trx holding 3.
    # The prefix form appends the framework and a timestamp, so each project keeps its own file.
    $testArguments += @('--filter', "IntegrationSlice=$Slice", '--logger', "trx;LogFilePrefix=onboard-$Slice")
}
$test = Invoke-LoggedCommand -Name 'dotnet-test-release' -FilePath 'dotnet' -Arguments $testArguments -LogPath (Join-Path $logsDirectory 'dotnet-test-release.log')

# The prefix only makes a collision unlikely -- its timestamp has one-second resolution, and two
# projects can finish inside one second. So the evidence is counted rather than trusted: the results
# it records must be exactly the tests the preflight said the filter selects. This is what turns a
# lost .trx from a quiet under-report into a FAIL, and it also catches a filter that selected one
# set and ran another.
$recordedTestCount = $null
if ($isSliceRun) {
    $recordedTestCount = 0
    foreach ($trx in @(Get-ChildItem -LiteralPath $resultsDirectory -Filter '*.trx' -File)) {
        $recordedTestCount += ([regex]::Matches(
            (Get-Content -LiteralPath $trx.FullName -Raw), '<UnitTestResult\b')).Count
    }
    if ($recordedTestCount -ne $selectedTestCount) {
        Add-Failure ("切片 $Slice 的测试结果条数与选中条数不符：trx 里 $recordedTestCount 条，" +
                     "选中 $selectedTestCount 条。证据不能声称覆盖了它没记下的测试。")
    }
}
$format = Invoke-LoggedCommand -Name 'dotnet-format-verify' -FilePath 'dotnet' -Arguments @('format', '.\SQCD_8005AGV.sln', '--verify-no-changes', '--no-restore') -LogPath (Join-Path $logsDirectory 'dotnet-format-verify.log')

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

# One row per slice this run has something to say about. Without -Slice that is still the two the
# whole-solution run could ever speak for, and they still share $hmiStatus -- which is exactly the
# limitation -Slice exists to remove, so the knownLimitations entry below stays on that path.
function New-SliceRow {
    param([object]$Entry)

    $row = [ordered]@{
        integrationSliceId = $Entry.integrationSliceId
        vectorIds = @($Entry.vectorIds)
        onboardHmiG2 = $hmiStatus
        controlServerG2 = 'PENDING_EXTERNAL'
        g3 = 'PENDING_JOINT'
    }

    # FP-IS-01 carries three facts about where its demand comes from. They are properties of that
    # slice, not of the run, so they travel with the row rather than with the caller.
    if ($Entry.integrationSliceId -eq 'FP-IS-01') {
        $row['demandMode'] = 'READ_ONLY_COMMITTED_PROJECTION'
        $row['demandSource'] = 'CONTROL_SERVER_SNAPSHOTS_ONLY'
        $row['controlServerOutcomes'] = @(
            'MESINGEST_FINAL_REREAD',
            'ATOMIC_DEMAND_ACCEPTANCE',
            'DEDUPLICATED_TO_PICKUP_INTENT',
            'RIOT_ORDER_RECONCILIATION',
            'TRUSTED_PICKUP_ARRIVAL'
        )
    }

    $row['forbidUnclosedFailOrInconclusive'] = [bool]$Entry.forbidUnclosedFailOrInconclusive
    return $row
}

$sliceRows = if ($null -ne $sliceEntry) {
    @((New-SliceRow $sliceEntry))
} else {
    @((New-SliceRow $is00), (New-SliceRow $is01))
}

$summary = [ordered]@{
    # 1.1.0, not 1.0.0: this run adds integrationSliceId, selectedTestCount and
    # integrationSliceIndexSha256, matching the control server's gate-result bump for the same three
    # facts. Additive, so a 1.0.0 reader still parses it -- but a consumer that cannot tell the two
    # shapes apart cannot tell a whole-solution verdict from a per-slice one either, which is the
    # whole reason -Slice exists.
    schemaVersion = '1.1.0'
    evidenceType = 'ONBOARD_HMI_LOCAL_G2'
    status = if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
    generatedAtUtc = $runUtc.ToString('o')
    runId = $runId
    integrationSliceId = $Slice
    selectedTestCount = $selectedTestCount
    recordedTestCount = $recordedTestCount
    integrationSliceIndexSha256 = $sliceIndexSha256
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
        tagExists = $tagExists
        tagCommit = $protocolTagCommit
        candidateCommit = $expected.Commit
        candidateIsAncestorOfHead = $candidateIsAncestor
        approvalStatus = $expected.ApprovalStatus
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
    slices = @($sliceRows)
    failures = @($failures)
    artifacts = [ordered]@{
        transcript = 'transcript.ndjson'
        journal = 'journal.ndjson'
        logs = 'logs'
        testResults = 'test-results'
        gateResult = if ($isSliceRun) { 'gate-result.json' } else { $null }
    }
    knownLimitations = @(
        '本证据是 OnboardHmi 本机 G2；ControlServer G2 和联合 G3 仍需外部/现场门禁。',
        'G1 使用临时盘符运行，仅规避 Windows 工作区路径含 # 时的 Node URL 解码问题，不改变协议仓库内容。',
        '真实车辆停稳信号、Modbus/锁/门/光幕和现场明文网络未在本机证据中宣称完成。',
        ('本证据绑定的是协议 v2 候选，approvalStatus=' + $expected.ApprovalStatus + '，不是已批准发布：' +
            $expected.Tag + ' 这个 tag 在协议仓里尚未打出（规格 6.6 第 6 条要产品负责人 attestation，2026-09-08 起为一名）。'),
        $(if (-not $isSliceRun) {
            '本次未传 -Slice：测试跑的是整个解决方案，不按切片过滤，summary.json 里 FP-IS-00 与 FP-IS-01 ' +
            '两片共享同一个 onboardHmiG2 结论。按切片各出一份证据请传 -Slice FP-IS-NN。'
        } else {
            '本次只证 ' + $Slice + ' 一片：测试按 IntegrationSlice=' + $Slice + ' 过滤，选中 ' +
            $selectedTestCount + ' 条。构建与 dotnet format 仍是全仓的，不随切片收窄。' +
            '其余切片的结论不在本目录里。'
        })
    )
}

Add-Event $transcript 'run.completed' @{ status = $summary.status; failures = $failures.Count }
$transcript | Set-Content -LiteralPath $transcriptPath -Encoding utf8
$journal | Set-Content -LiteralPath $journalPath -Encoding utf8
$summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding utf8

# A slice run also writes gate-result.json, aligned with what test-wire-to-gate.ps1 writes on the
# control server side: two ends of one slice should be readable by one reader, so every field that
# side has is here under that side's name, with that side's schemaVersion and PASS/FAIL rule.
#
# It is a superset, not an identical shape. This end adds implementationBranch (that side runs on one
# branch; this one runs on w2g/*), recordedTestCount (that end has one test project and so cannot
# lose a .trx to a name collision), and buildExitCode/formatExitCode (that end's script runs neither
# -- here both are part of the verdict). A reader written for the control server's 1.1.0 parses this;
# a reader that requires exactly its field set does not.
#
# summary.json stays as well: it carries the transcript, the G1 result and the identity checks, none
# of which the gate result has room for.
if ($isSliceRun) {
    $gateResult = [ordered]@{
        schemaVersion = '1.1.0'
        gate = 'ONBOARD_HMI_G2'
        integrationSliceId = $Slice
        status = $summary.status
        startedAt = $runUtc.ToString('o')
        finishedAt = ([DateTime]::UtcNow).ToString('o')
        implementationRepository = '8005-agv-onboard-hmi'
        implementationCommit = $hmiCommit
        implementationBranch = $summary.hmi.branch
        protocolReleaseVersion = $expected.ReleaseVersion
        protocolTag = $expected.Tag
        protocolProfileId = $expected.ProfileId
        protocolVersion = $expected.ProtocolVersion
        protocolApprovalStatus = $expected.ApprovalStatus
        protocolRepositoryCommit = $expected.Commit
        protocolManifestSha256 = $expected.ManifestSha256
        protocolSchemaBundleSha256 = $expected.SchemaBundleSha256
        protocolVectorsSha256 = $expected.VectorsSha256
        integrationSliceIndexSha256 = $sliceIndexSha256
        selectedTestCount = $selectedTestCount
        recordedTestCount = $recordedTestCount
        vectorIds = @($sliceEntry.vectorIds)
        buildExitCode = $build.ExitCode
        testExitCode = $test.ExitCode
        formatExitCode = $format.ExitCode
    }
    $gateResult | ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath (Join-Path $runDirectory 'gate-result.json') -Encoding utf8
}

Write-Host "G2 evidence: $runDirectory"
Write-Host "Status: $($summary.status)"
if ($failures.Count -gt 0) {
    throw ("本机 G2 证据生成失败：" + [Environment]::NewLine + ($failures -join [Environment]::NewLine))
}

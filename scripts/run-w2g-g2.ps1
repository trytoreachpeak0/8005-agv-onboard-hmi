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
    #
    # Several slices in one call (onboard-hmi#295): `-Slice FP-IS-00,FP-IS-04` (also what
    # `pwsh -File` passes as one string, hence the comma split below rather than a ValidatePattern
    # on the raw argument). Build, dotnet format and protocol G1 then run once for all of them, and
    # each slice still gets its own directory and gate-result.json -- see "Several slices" below.
    [string[]]$Slice = @(),
    # Every slice in the index except those $slicesNotImplementedHere names, which are skipped and
    # listed in multi-slice-summary.json. Any other slice that selects no test refuses the whole run,
    # exactly as -Slice does -- see the table below for why skipping is not decided by the filter.
    [switch]$AllSlices,
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
# Resolve both against the caller's location now, before anything below changes it. A relative
# value taken as given resolves wherever the script happens to be standing: under the subst drive
# root in Invoke-ProtocolG1 (every slice exited 1 without a gate-result.json), or under $hmiRoot
# after Push-Location (evidence silently written somewhere else). Fixing it here rather than in each
# consumer keeps New-Item, the logs, --results-directory and gate-result.json on one absolute path.
# G2ScriptPathArchitectureTests keeps these two lines ahead of the first location change
# (onboard-hmi#287).
$EvidenceRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($EvidenceRoot)
$ProtocolRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProtocolRoot)

$requestedSlices = @($Slice | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($requested in $requestedSlices) {
    if ($requested -cnotmatch '^FP-IS-(0[0-9]|1[0-5])$') {
        throw "-Slice 的取值 '$requested' 不是 FP-IS-00～FP-IS-15 之一。"
    }
}
$duplicateSlices = @($requestedSlices | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
if ($duplicateSlices.Count -gt 0) {
    throw "-Slice 里重复给了：$($duplicateSlices -join ', ')。同一片在一次运行里只能出一份证据。"
}
if ($AllSlices -and $requestedSlices.Count -gt 0) {
    throw '-AllSlices 与 -Slice 只能给一个。'
}

# 协议 v3.0.0 的身份。这是本仓库的第二份副本，权威副本是
# src/SQCD.Agv.Contracts/WireToGateProtocol.cs 的 WireToGateRelease；
# ProtocolIdentityArchitectureTests.TheGateScriptExpectsTheSameIdentityAsTheAssembly
# 逐字段比对这两份，任一处漂移即测试红。
#
# protocol-v3.0.0 已于 2026-10-09 发布（8005-agv-program#152）：注释 tag 指向下面的 Commit，
# 外置 attestation 里有一份批准，由产品负责人授权的 AI agent 给出。发布内容与候选
# （8005-agv-program#151 冻结的 3f091cb2…，候选期的绑定见 8005-agv-onboard-hmi#214）一字未改，
# 所以九个身份值原样保留，只把 ApprovalStatus 由 SUPERSEDING_CANDIDATE 改为 APPROVED_RELEASE
# （8005-agv-control-server#393，照 8005-agv-onboard-hmi#79 的先例）。
# 下面照旧检查 tag 若存在必须指向 Commit，并且 ApprovalStatus 声称已发布时 tag 必须存在。
#
# ProtocolVersion 只在同一 profileId 内单调递增（WIRE_TO_GATE_MVP 0.3.0 与 AGV_FULL_PRODUCT 2.0.0 都是 3），
# 所以身份比较一律逐字段比完整身份，不得只比这个整数。
$expected = [ordered]@{
    ProtocolVersion = 4
    ProfileId = 'AGV_FULL_PRODUCT'
    ReleaseVersion = '3.0.0'
    Repository = '8005-agv-protocol'
    Tag = 'protocol-v3.0.0'
    Commit = '3f091cb2eae7c58cec54a95dd9389c9180bc7b4c'
    ManifestSha256 = 'd5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e'
    SchemaBundleSha256 = 'e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43'
    VectorsSha256 = 'be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e'
    ApprovalStatus = 'APPROVED_RELEASE'
}

$failures = [System.Collections.Generic.List[string]]::new()
$runUtc = [DateTime]::UtcNow
$runId = $runUtc.ToString('yyyyMMddTHHmmssfffZ')
$hmiCommit = (& git -C $hmiRoot rev-parse HEAD).Trim()
$shortHmiCommit = if ($hmiCommit.Length -ge 12) { $hmiCommit.Substring(0, 12) } else { $hmiCommit }

# The slice preflight runs before any directory exists, and before the Release build the run would
# otherwise pay for. A filter that selects nothing exits 0 -- measured on this solution, 2026-09-09,
# `--filter "IntegrationSlice=FP-IS-13"` returned exit code 0 over zero executed tests -- so without
# this a slice nobody has built here would be written out as PASS. (FP-IS-13 was such a slice then; it
# selects its tests since 8005-agv-onboard-hmi#222 flipped it, and FP-IS-09 is the one left today.) That is the defect ticket 14
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
$isSliceRun = $AllSlices -or $requestedSlices.Count -gt 0
$sliceIndexPath = Join-Path $ProtocolRoot 'integration-slices\index.json'
$sliceIndexSha256 = $null
$slicesWithoutTests = @()

# The slices of the index this line has not implemented: the only ones -AllSlices may skip. Written
# here rather than inferred from "the filter selects nothing", because that inference is the
# defect it would hide: an implemented slice whose tests all went missing (a mistyped trait, a test
# project dropped from the solution) also selects nothing, and -AllSlices used to write fourteen
# PASS verdicts and exit 0 over it (onboard-hmi#295 review, M1; the -Slice form of the same hole is
# docs/defects/20260908-empty-slice-filter-mints-a-green-g2.md). The architecture test cannot stand
# in for this check: it runs in the push/PR round, not in the per-slice round a batch exit cites,
# and not at all if its project leaves the solution.
#
# A second copy of a fact the test assembly holds -- the index minus
# ProtocolVectorTestBindingArchitectureTests.SlicesThisLineImplements -- and
# G2ScriptSliceTableArchitectureTests compares the two. Keep it on one line; that test reads it.
$slicesNotImplementedHere = @('FP-IS-09')

# Lists the tests one slice's filter selects. The first call of a run builds (as the single-slice
# preflight always has); later calls of a multi-slice run pass --no-build, because the tree they
# would build is the one the first call just built.
function Get-SliceSelection {
    param([string]$SliceId, [switch]$NoBuild)

    $listArguments = @('test', '.\SQCD_8005AGV.sln', '-c', 'Release', '--list-tests', '--filter', "IntegrationSlice=$SliceId")
    if ($NoBuild) { $listArguments += '--no-build' }
    Push-Location $hmiRoot
    try {
        $listedLines = @(& dotnet @listArguments |
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
        throw ("列举切片 '$SliceId' 的测试失败（exit=$listExitCode）。这通常是构建坏了，" +
               '不是这一片没有测试——先修构建再出证。')
    }
    return , $listedLines
}

# One entry per verdict this run writes: one per slice, or a single entry with no slice for the
# whole-solution run. Every slice is preflighted before any directory exists, so a multi-slice run
# that names one empty slice is refused as a whole, exactly as that slice alone would be.
$runs = [System.Collections.Generic.List[object]]::new()
if ($isSliceRun) {
    if (-not (Test-Path -LiteralPath $sliceIndexPath -PathType Leaf)) {
        throw "找不到切片索引：$sliceIndexPath"
    }
    $sliceIndexSha256 = (Get-FileHash -LiteralPath $sliceIndexPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $indexSlices = @((Get-Content -LiteralPath $sliceIndexPath -Raw | ConvertFrom-Json).slices)
    $candidateSlices = if ($AllSlices) {
        @($indexSlices | ForEach-Object integrationSliceId | Sort-Object -Unique)
    } else {
        $requestedSlices
    }

    foreach ($candidate in $candidateSlices) {
        # @() so a duplicated integrationSliceId in the index is a loud count mismatch rather than an
        # array quietly flattening its vectorIds into the evidence.
        $sliceMatches = @($indexSlices | Where-Object { $_.integrationSliceId -eq $candidate })
        if ($sliceMatches.Count -ne 1) {
            throw "切片索引里 '$candidate' 匹配到 $($sliceMatches.Count) 条，应当恰好 1 条：$sliceIndexPath"
        }

        $listed = Get-SliceSelection -SliceId $candidate -NoBuild:($runs.Count -gt 0 -or $slicesWithoutTests.Count -gt 0)
        $tabledAsNotImplemented = $slicesNotImplementedHere -ccontains $candidate
        if ($AllSlices -and $tabledAsNotImplemented) {
            # Listed all the same, so a slice somebody built without updating the table is a refusal
            # rather than a slice quietly left out of the batch exit's evidence.
            if ($listed.Count -gt 0) {
                throw ("切片 '$candidate' 在 `$slicesNotImplementedHere 里，却选中了 $($listed.Count) 条测试。" +
                       '它已经实现了：把它从表里删掉（G2ScriptSliceTableArchitectureTests 会要求同时改 SlicesThisLineImplements）。')
            }
            $slicesWithoutTests += [ordered]@{ integrationSliceId = $candidate; vectorIds = @($sliceMatches[0].vectorIds) }
            continue
        }
        if ($listed.Count -eq 0) {
            throw ("切片 '$candidate' 在本仓选不中任何测试，ONBOARD_HMI_G2 没有东西可证。" +
                   "它的向量是 " + ($sliceMatches[0].vectorIds -join ', ') + '。' +
                   $(if ($AllSlices) { '它不在 $slicesNotImplementedHere 里，所以这是一片已实现切片的测试全丢了（trait 写错、测试工程不在解决方案里），不是还没建。' } else { '' }))
        }
        $runs.Add([pscustomobject]@{ Slice = $candidate; Entry = $sliceMatches[0]; Listed = $listed; SelectedTestCount = $listed.Count })
    }
    if ($runs.Count -eq 0) {
        throw "-AllSlices 在切片索引里没有找到任何能在本仓选中测试的切片：$sliceIndexPath"
    }
} else {
    $runs.Add([pscustomobject]@{ Slice = $null; Entry = $null; Listed = $null; SelectedTestCount = $null })
}
$isMultiSliceRun = $AllSlices -or $runs.Count -gt 1

# Where each verdict goes. A single slice and the whole solution keep the layout they always had. A
# multi-slice run writes each slice to exactly the directory a one-slice call would have used --
# <Tag>/<Slice>/<runId>-<sha12>/, all sharing one runId -- plus one multi-slice/<runId>-<sha12>/
# directory that holds the shared steps' logs and transcript and multi-slice-summary.json.
$tagRoot = Join-Path $EvidenceRoot $expected.Tag
$runDirectoryName = $runId + '-' + $shortHmiCommit
foreach ($run in $runs) {
    $run | Add-Member -NotePropertyName Directory -NotePropertyValue (
        $null -eq $run.Slice ? (Join-Path $tagRoot $runDirectoryName) : (Join-Path (Join-Path $tagRoot $run.Slice) $runDirectoryName))
    $run | Add-Member -NotePropertyName LogsDirectory -NotePropertyValue (Join-Path $run.Directory 'logs')
    $run | Add-Member -NotePropertyName ResultsDirectory -NotePropertyValue (Join-Path $run.Directory 'test-results')
    $run | Add-Member -NotePropertyName Transcript -NotePropertyValue ([System.Collections.Generic.List[string]]::new())
    $run | Add-Member -NotePropertyName Journal -NotePropertyValue ([System.Collections.Generic.List[string]]::new())
    $run | Add-Member -NotePropertyName Failures -NotePropertyValue ([System.Collections.Generic.List[string]]::new())
    New-Item -ItemType Directory -Force -Path $run.LogsDirectory, $run.ResultsDirectory | Out-Null
}
$multiSliceDirectory = $null
$logsDirectory = $runs[0].LogsDirectory
if ($isMultiSliceRun) {
    $multiSliceDirectory = Join-Path (Join-Path $tagRoot 'multi-slice') $runDirectoryName
    $logsDirectory = Join-Path $multiSliceDirectory 'logs'
    New-Item -ItemType Directory -Force -Path $logsDirectory | Out-Null
}

# The shared steps (identity checks, protocol G1, build, format) write here once; each run's own
# transcript, journal and failures start from a copy of them.
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
    param(
        [string]$Message,
        [System.Collections.Generic.List[string]]$Target = $script:failures
    )
    $null = $Target.Add($Message)
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
        [string]$LogPath,
        [string]$SchemaReportDirectory = '',
        # The shared lists by default; a slice's test run passes that slice's own.
        [System.Collections.Generic.List[string]]$TranscriptList = $script:transcript,
        [System.Collections.Generic.List[string]]$FailureList = $script:failures
    )

    Add-Event $TranscriptList 'command.started' @{ name = $Name; arguments = $Arguments }
    Push-Location $hmiRoot
    # WireToGateG2Tests 在测试进程结束时，把本次经 WireToGateProtocolSerializer.Create／RebindSessionGeneration
    # 发出的每一条协议报文（车载端产品与 FakeControlServer 两个产地）交给独立进程
    # tools/SQCD.Agv.SchemaConformance，按本仓 vendor 的协议 schema 逐条校验（OutboundSchemaConformance.cs，
    # 8005-agv-onboard-hmi#74）。违约以 test assembly cleanup failure 让 dotnet test 退出码非 0，而控制台
    # 摘要仍写 Failed: 0——所以一律按退出码判，不解析摘要。schema-coverage.json 与（违约时）
    # schema-violations.json、schema-conformance.txt 落在这个目录里。
    #
    # 成本：每次跑到 WireToGateG2Tests 多出约 45～75 秒 schema 编译（2026-09-17 本机实测 45、61、62、
    # 75 秒，随机器负载波动；逐条校验约 4 秒，测试本身约 8 秒）。试过把各消息 schema 合成一份只编译
    # 一次，同一批报文 70 秒只降到 57 秒——开销在代码生成本身，不值得为此让错误定位变复杂，所以没换。
    if (-not [string]::IsNullOrWhiteSpace($SchemaReportDirectory)) {
        $env:WIRE_TO_GATE_SCHEMA_REPORT_DIR = $SchemaReportDirectory
    }
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
        $output | Out-File -LiteralPath $LogPath -Encoding utf8
    } finally {
        Pop-Location
        Remove-Item Env:WIRE_TO_GATE_SCHEMA_REPORT_DIR -ErrorAction SilentlyContinue
    }
    Add-Event $TranscriptList 'command.completed' @{ name = $Name; exitCode = $exitCode; log = (Split-Path -Leaf $LogPath) }
    if ($exitCode -ne 0) {
        Add-Failure "$Name exited with code $exitCode" $FailureList
    }
    return [pscustomobject]@{
        Name = $Name
        ExitCode = $exitCode
        Output = ($output -join [Environment]::NewLine)
        LogPath = $LogPath
    }
}

function Read-SchemaConformance {
    param(
        [string]$Directory,
        [string]$RelativeDirectory
    )

    $path = Join-Path $Directory 'schema-coverage.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }
    $coverage = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    return [ordered]@{
        linesChecked = $coverage.linesChecked
        linesInViolation = $coverage.linesInViolation
        linesInKnownViolation = $coverage.linesInKnownViolation
        schemaCompilationMilliseconds = $coverage.schemaCompilationMilliseconds
        coverage = $RelativeDirectory + '/schema-coverage.json'
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
            #
            # 参数一律 splat（@installArguments），不能把数组当一个参数传。PATH 上的 pnpm 若是
            # corepack 装的 pnpm.ps1，`& pnpm.ps1 $数组` 会把两个元素并成一个字符串
            # "install --frozen-lockfile" 交给 pnpm，pnpm 当它是要执行的命令名，最后落到
            # GNU install 上报 `unknown option -- frozen-lockfile`。2026-09-09 没暴露，是因为那时
            # pnpm 不在 PATH，走的是 `node pnpm.cjs`，原生程序会把数组逐项展开。2026-09-12 在
            # 控制端 PATH 上有 C:\Program Files\nodejs 时实测复现，run-staged-g3.ps1 的
            # Invoke-LoggedCommand 一直是 splat，不受影响。
            $installArguments = $toolchain.PrefixArguments + @('install', '--frozen-lockfile')
            $installOutput = & $toolchain.FilePath @installArguments 2>&1
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
            $output = & $toolchain.FilePath @g1Arguments 2>&1
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

foreach ($run in $runs) {
    $runStarted = @{
        evidenceType = 'ONBOARD_HMI_LOCAL_G2'
        runId = $runId
        hmiRoot = $hmiRoot
        protocolRoot = $ProtocolRoot
        integrationSliceId = $run.Slice
        selectedTestCount = $run.SelectedTestCount
    }
    if ($isMultiSliceRun) { $runStarted['multiSliceDirectory'] = $multiSliceDirectory }
    Add-Event $run.Transcript 'run.started' $runStarted
    Add-Event $run.Journal 'run.started' @{ runId = $runId; evidenceDirectory = $run.Directory }
}
if ($isMultiSliceRun) {
    Add-Event $transcript 'run.started' @{
        evidenceType = 'ONBOARD_HMI_LOCAL_G2_MULTI_SLICE'
        runId = $runId
        hmiRoot = $hmiRoot
        protocolRoot = $ProtocolRoot
        integrationSliceIds = @($runs | ForEach-Object Slice)
    }
}

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

# git 查不到时 stdout 为空、PowerShell 拿到 $null；用 ?. 让它落到下面的空值判断记失败，而不是在这里崩掉、连 summary.json 都不写。
$protocolCommit = (& git -C $ProtocolRoot rev-parse HEAD)?.Trim()

# 绑的是 commit。tag 存在就必须指向同一个 commit：打错地方比没打更危险。ApprovalStatus 声称
# 已发布时 tag 还必须存在，否则常量被悄悄改成已发布也不会有人发现。协议检出里没有 tag 时，
# 先在协议仓 `git fetch --tags`。
$protocolTagCommit = (& git -C $ProtocolRoot rev-list -n 1 ($expected.Tag + '^{commit}') 2>$null)
$protocolTagCommit = if ($null -eq $protocolTagCommit) { '' } else { ([string]$protocolTagCommit).Trim() }
$tagExists = -not [string]::IsNullOrWhiteSpace($protocolTagCommit)
if ($tagExists -and $protocolTagCommit -ne $expected.Commit) {
    Add-Failure "$($expected.Tag) 已存在但指向 $protocolTagCommit，不是绑定的 commit $($expected.Commit)"
}
if ($expected.ApprovalStatus -eq 'APPROVED_RELEASE' -and -not $tagExists) {
    Add-Failure "ApprovalStatus 为 APPROVED_RELEASE，但协议检出 $ProtocolRoot 里没有 $($expected.Tag) 这个 tag"
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
# slice happens to touch. For the same reason a multi-slice run does them once: the tree is the same
# for every slice, so fifteen runs of dotnet format said the same thing fifteen times (629 seconds of
# the batch-8 exit, ci-research/batch-exit-timeline-and-speedup.md 3.2). It runs before the tests
# here, which used to come first; nothing reads one from the other.
$format = Invoke-LoggedCommand -Name 'dotnet-format-verify' -FilePath 'dotnet' -Arguments @('format', '.\SQCD_8005AGV.sln', '--verify-no-changes', '--no-restore') -LogPath (Join-Path $logsDirectory 'dotnet-format-verify.log')

$g1Text = if ($g1Result) { $g1Result.Output } else { '' }
$g1ManifestMatch = [regex]::Match($g1Text, '"candidateManifestSha256"\s*:\s*"([^"]+)"')
$protocolStatus = if ($g1Result) { $g1Result.Status } else { 'SKIPPED' }

# Several slices: each slice directory gets a copy of the shared logs under the names a one-slice
# run gives them, so summary.json's commands.*.log and every existing reader keep working on any one
# slice directory alone. The copies are the logs of the one build, format and G1 this run did, not
# reruns; multiSliceRun in summary.json and gate-result.json says so and names the directory that
# holds the originals.
if ($isMultiSliceRun) {
    foreach ($run in $runs) {
        foreach ($sharedLog in @(Get-ChildItem -LiteralPath $logsDirectory -File)) {
            Copy-Item -LiteralPath $sharedLog.FullName -Destination $run.LogsDirectory
        }
    }
}

function Get-RelativeEvidencePath {
    param([string]$From, [string]$To)
    return [System.IO.Path]::GetRelativePath($From, $To).Replace('\', '/')
}

# One row per slice this run has something to say about. Without -Slice that is still the two the
# whole-solution run could ever speak for, and they still share one onboardHmiG2 verdict -- which is
# exactly the limitation -Slice exists to remove, so the knownLimitations entry below stays on that path.
function New-SliceRow {
    param([object]$Entry, [string]$HmiStatus)

    $row = [ordered]@{
        integrationSliceId = $Entry.integrationSliceId
        vectorIds = @($Entry.vectorIds)
        onboardHmiG2 = $HmiStatus
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

foreach ($run in $runs) {
    $runSlice = $run.Slice
    $runIsSlice = $null -ne $runSlice
    $resultsDirectory = $run.ResultsDirectory
    # This run's transcript and failures start from everything the shared steps recorded.
    $run.Transcript.AddRange($transcript)
    $run.Failures.AddRange($failures)

    $testArguments = @('test', '.\SQCD_8005AGV.sln', '-c', 'Release', '--no-build', '--results-directory', $resultsDirectory)
    if ($runIsSlice) {
        # LogFilePrefix, not LogFileName. This solution has two test projects and a slice can select
        # tests in both; LogFileName gives them the same path and the second silently overwrites the
        # first. Measured on 2026-09-09: -Slice FP-IS-01 selected 5 tests and left one .trx holding 3.
        # The prefix form appends the framework and a timestamp, so each project keeps its own file.
        $testArguments += @('--filter', "IntegrationSlice=$runSlice", '--logger', "trx;LogFilePrefix=onboard-$runSlice")
    }
    $test = Invoke-LoggedCommand -Name 'dotnet-test-release' -FilePath 'dotnet' -Arguments $testArguments -LogPath (Join-Path $run.LogsDirectory 'dotnet-test-release.log') -SchemaReportDirectory $resultsDirectory -TranscriptList $run.Transcript -FailureList $run.Failures

    # 出站 schema 校验的摘要。整仓那一趟一定跑到 WireToGateG2Tests，所以那里没有 schema-coverage.json 只有
    # 一种解释：校验没挂上（fixture 被删、观察点被摘、校验器没构建）。不判失败的话，删掉 fixture 会让这道
    # 门禁无声消失。-Slice 那一趟只在选中了 WireToGateG2Tests 的测试时才有东西可验：选中了就同样要求覆盖
    # 文件在；没选中时摘要写 null，违约仍由上面的退出码判。
    $runReachesG2Tests = -not $runIsSlice -or
        @($run.Listed | Where-Object { $_ -match '^\s*SQCD\.Agv\.WireToGateG2Tests\.\S' }).Count -gt 0
    $schemaConformance = $null
    if ($runReachesG2Tests) {
        $schemaConformance = Read-SchemaConformance $resultsDirectory 'test-results'
        if ($null -eq $schemaConformance) {
            Add-Failure ('dotnet test 跑到了 WireToGateG2Tests，却没有产出 test-results/schema-coverage.json：' +
                         '出站 schema 校验没有运行（OutboundSchemaConformance 没挂上，或校验器没构建）。') $run.Failures
        }
    }

    # The prefix only makes a collision unlikely -- its timestamp has one-second resolution, and two
    # projects can finish inside one second. So the evidence is counted rather than trusted: the results
    # it records must be exactly the tests the preflight said the filter selects. This is what turns a
    # lost .trx from a quiet under-report into a FAIL, and it also catches a filter that selected one
    # set and ran another.
    $recordedTestCount = $null
    if ($runIsSlice) {
        $recordedTestCount = 0
        foreach ($trx in @(Get-ChildItem -LiteralPath $resultsDirectory -Filter '*.trx' -File)) {
            $recordedTestCount += ([regex]::Matches(
                (Get-Content -LiteralPath $trx.FullName -Raw), '<UnitTestResult\b')).Count
        }
        if ($recordedTestCount -ne $run.SelectedTestCount) {
            Add-Failure ("切片 $runSlice 的测试结果条数与选中条数不符：trx 里 $recordedTestCount 条，" +
                         "选中 $($run.SelectedTestCount) 条。证据不能声称覆盖了它没记下的测试。") $run.Failures
        }
    }

    $hmiStatus = if ($build.ExitCode -eq 0 -and $test.ExitCode -eq 0 -and $format.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    # One line per test assembly, e.g. "Passed!  - Failed: 0, Passed: 307, Skipped: 0, Total: 307, Duration: ...".
    # The old pattern looked for "Total tests:", which this SDK never prints, so every summary said 'unparsed'
    # (evidence of 2026-09-14 included). English only: a zh-CN machine prints these lines translated, which is
    # why the CI workflow sets DOTNET_CLI_UI_LANGUAGE=en. The verdict is the exit code either way; this is a
    # summary for the reader.
    $testSummaryLines = @([regex]::Matches($test.Output, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)'))
    $testSummaryText = if ($testSummaryLines.Count -gt 0) {
        $sum = { param([int]$Group) ($testSummaryLines | ForEach-Object { [int]$_.Groups[$Group].Value } | Measure-Object -Sum).Sum }
        "Total: $(& $sum 4), Passed: $(& $sum 2), Failed: $(& $sum 1), Skipped: $(& $sum 3), Assemblies: $($testSummaryLines.Count)"
    } else {
        'unparsed'
    }

    Add-Event $run.Transcript 'evidence.journal.bound' @{
        protocolCommit = $protocolCommit
        hmiCommit = $hmiCommit
        g1Status = $protocolStatus
        hmiStatus = $hmiStatus
    }
    # profileId 与 protocolVersion 一起写，从不单写后者：同一个整数在两个 profile 上都出现过
    # （WIRE_TO_GATE_MVP 0.3.0 与 AGV_FULL_PRODUCT 2.0.0 都是 3），单写的证据读不出是哪一条线。
    Add-Event $run.Journal 'protocol.identity.checked' @{
        protocolCommit = $protocolCommit
        protocolTag = $expected.Tag
        profileId = $expected.ProfileId
        protocolVersion = $expected.ProtocolVersion
        releaseVersion = $expected.ReleaseVersion
        manifestSha256 = if ($g1ManifestMatch.Success) { $g1ManifestMatch.Groups[1].Value } else { $hmiIdentity.ManifestSha256 }
        schemaBundleSha256 = $expected.SchemaBundleSha256
        vectorsSha256 = $expected.VectorsSha256
    }
    Add-Event $run.Journal 'hmi.validation.completed' @{
        buildExitCode = $build.ExitCode
        testExitCode = $test.ExitCode
        formatExitCode = $format.ExitCode
        testSummary = $testSummaryText
    }

    $sliceRows = if ($runIsSlice) {
        @((New-SliceRow $run.Entry $hmiStatus))
    } else {
        @((New-SliceRow $is00 $hmiStatus), (New-SliceRow $is01 $hmiStatus))
    }

    # null for a one-slice or whole-solution run. For a slice of a multi-slice run: where the shared
    # steps' originals are, relative to this slice's directory, and which slices shared them.
    $multiSliceRun = if ($isMultiSliceRun) {
        [ordered]@{
            runId = $runId
            directory = Get-RelativeEvidencePath $run.Directory $multiSliceDirectory
            integrationSliceIds = @($runs | ForEach-Object Slice)
            sharedSteps = @('protocol-g1', 'dotnet-build-release', 'dotnet-format-verify')
            g1Status = $protocolStatus
            protocolHeadCommit = $protocolCommit
            protocolG1Log = if ($g1Result) { 'logs/protocol-g1.log' } else { $null }
        }
    } else {
        $null
    }

    $summary = [ordered]@{
        # 1.1.0 added integrationSliceId, selectedTestCount and integrationSliceIndexSha256, matching the
        # control server's gate-result bump for the same three facts: a consumer that cannot tell the two
        # shapes apart cannot tell a whole-solution verdict from a per-slice one either, which is the whole
        # reason -Slice exists. 1.2.0 adds schemaConformance, the outbound schema gate
        # (8005-agv-onboard-hmi#74). 1.3.0 adds multiSliceRun (8005-agv-onboard-hmi#295), null unless
        # this slice was certified by a multi-slice call. All additive, so an older reader still parses it.
        schemaVersion = '1.3.0'
        evidenceType = 'ONBOARD_HMI_LOCAL_G2'
        status = if ($run.Failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
        generatedAtUtc = $runUtc.ToString('o')
        runId = $runId
        integrationSliceId = $runSlice
        selectedTestCount = $run.SelectedTestCount
        recordedTestCount = $recordedTestCount
        integrationSliceIndexSha256 = $sliceIndexSha256
        multiSliceRun = $multiSliceRun
        hmi = [ordered]@{
            commit = $hmiCommit
            # detached HEAD（CI 检出 PR、红基线用 git worktree add --detach）上 git 什么都不打印，branch 记 null。
            branch = (& git -C $hmiRoot branch --show-current)?.Trim()
            workingTreeStatus = @(& git -C $hmiRoot status --porcelain)
            protocolIdentity = $hmiIdentity
        }
        protocol = [ordered]@{
            repository = $expected.Repository
            headCommit = $protocolCommit
            tag = $expected.Tag
            profileId = $expected.ProfileId
            protocolVersion = $expected.ProtocolVersion
            releaseVersion = $expected.ReleaseVersion
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
        schemaConformance = $schemaConformance
        slices = @($sliceRows)
        failures = @($run.Failures)
        artifacts = [ordered]@{
            transcript = 'transcript.ndjson'
            journal = 'journal.ndjson'
            logs = 'logs'
            testResults = 'test-results'
            gateResult = if ($runIsSlice) { 'gate-result.json' } else { $null }
        }
        knownLimitations = @(
            '本证据是 OnboardHmi 本机 G2；ControlServer G2 和联合 G3 仍需外部/现场门禁。',
            'G1 使用临时盘符运行，仅规避 Windows 工作区路径含 # 时的 Node URL 解码问题，不改变协议仓库内容。',
            '真实车辆停稳信号、Modbus/锁/门/光幕和现场明文网络未在本机证据中宣称完成。',
            ('出站 schema 校验（schemaConformance）只验 WireToGateG2Tests 进程里经 WireToGateProtocolSerializer.Create／' +
             'RebindSessionGeneration 产出的报文（车载端产品与 FakeControlServer 两个产地）；入站不验；messageType ' +
             '覆盖只报告不判死——没被任何测试发出的消息，它的发送方法缺字段这道门禁看不见。违约按 dotnet test 退出码判，' +
             '控制台摘要仍会写 Failed: 0。'),
            $(if ($expected.ApprovalStatus -eq 'APPROVED_RELEASE') {
                '本证据绑定的是已发布的 ' + $expected.Tag + '（commit ' + $expected.Commit + '）；发布批准记在外置 attestation 里，本证据不复核它。'
            } else {
                '本证据绑定的是协议候选 (' + $expected.ProfileId + ', protocolVersion ' + $expected.ProtocolVersion +
                ') releaseVersion ' + $expected.ReleaseVersion + '，approvalStatus=' + $expected.ApprovalStatus +
                '，不是已批准发布：' + $expected.Tag + ' 这个 tag 尚未打出。同一个 protocolVersion 在 MVP 线的 ' +
                'WIRE_TO_GATE_MVP 0.3.0 上也出现过，所以身份要连 profileId 一起读。'
            }),
            $(if (-not $runIsSlice) {
                '本次未传 -Slice：测试跑的是整个解决方案，不按切片过滤，summary.json 里 FP-IS-00 与 FP-IS-01 ' +
                '两片共享同一个 onboardHmiG2 结论。按切片各出一份证据请传 -Slice FP-IS-NN。'
            } else {
                '本次只证 ' + $runSlice + ' 一片：测试按 IntegrationSlice=' + $runSlice + ' 过滤，选中 ' +
                $run.SelectedTestCount + ' 条。构建与 dotnet format 仍是全仓的，不随切片收窄。' +
                '其余切片的结论不在本目录里。'
            })
        ) + @(if ($isMultiSliceRun) {
            '本片由一次多片调用出证（multiSliceRun）：构建、dotnet format 与协议 G1 对 ' +
            (@($runs | ForEach-Object Slice) -join ', ') + ' 只跑了一次，logs/ 里的 dotnet-build-release.log、' +
            'dotnet-format-verify.log、protocol-g1.log 是那一次的副本，原件在 multiSliceRun.directory。' +
            '测试仍按本片单独过滤、单独跑一次 dotnet test。'
        })
    }

    Add-Event $run.Transcript 'run.completed' @{ status = $summary.status; failures = $run.Failures.Count }
    $run.Transcript | Set-Content -LiteralPath (Join-Path $run.Directory 'transcript.ndjson') -Encoding utf8
    $run.Journal | Set-Content -LiteralPath (Join-Path $run.Directory 'journal.ndjson') -Encoding utf8
    $summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $run.Directory 'summary.json') -Encoding utf8

    # A slice run also writes gate-result.json, aligned with what test-wire-to-gate.ps1 writes on the
    # control server side: two ends of one slice should be readable by one reader, so every field that
    # side has is here under that side's name, with that side's schemaVersion and PASS/FAIL rule.
    #
    # It is a superset, not an identical shape. This end adds implementationBranch (that side runs on one
    # branch; this one runs on w2g/*), recordedTestCount (that end has one test project and so cannot
    # lose a .trx to a name collision), buildExitCode/formatExitCode (that end's script runs neither
    # -- here both are part of the verdict), and multiSliceRun (null unless a multi-slice call). A
    # reader written for the control server's 1.1.0 parses this; a reader that requires exactly its
    # field set does not.
    #
    # summary.json stays as well: it carries the transcript, the G1 result and the identity checks, none
    # of which the gate result has room for.
    if ($runIsSlice) {
        $gateResult = [ordered]@{
            # 1.2.0: adds schemaConformance (see the summary's schemaVersion). null when this slice selected
            # no WireToGateG2Tests test; a violation still fails the run through testExitCode. 1.3.0: adds
            # multiSliceRun.
            schemaVersion = '1.3.0'
            gate = 'ONBOARD_HMI_G2'
            integrationSliceId = $runSlice
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
            selectedTestCount = $run.SelectedTestCount
            recordedTestCount = $recordedTestCount
            vectorIds = @($run.Entry.vectorIds)
            buildExitCode = $build.ExitCode
            testExitCode = $test.ExitCode
            formatExitCode = $format.ExitCode
            schemaConformance = $schemaConformance
            multiSliceRun = $multiSliceRun
        }
        $gateResult | ConvertTo-Json -Depth 30 |
            Set-Content -LiteralPath (Join-Path $run.Directory 'gate-result.json') -Encoding utf8
    }

    $run | Add-Member -NotePropertyName Status -NotePropertyValue $summary.status
    Write-Host "G2 evidence: $($run.Directory)"
    Write-Host "Status: $($summary.status)"
}

# The multi-slice directory: the shared steps' transcript and journal, and one line per slice. It is an
# index, not a verdict of its own -- each slice's gate-result.json is the verdict for that slice.
if ($isMultiSliceRun) {
    Add-Event $transcript 'run.completed' @{
        status = @($runs | Where-Object Status -ne 'PASS').Count -eq 0 ? 'PASS' : 'FAIL'
        failingSlices = @($runs | Where-Object Status -ne 'PASS' | ForEach-Object Slice)
    }
    $transcript | Set-Content -LiteralPath (Join-Path $multiSliceDirectory 'transcript.ndjson') -Encoding utf8
    $journal | Set-Content -LiteralPath (Join-Path $multiSliceDirectory 'journal.ndjson') -Encoding utf8
    [ordered]@{
        schemaVersion = '1.0.0'
        evidenceType = 'ONBOARD_HMI_LOCAL_G2_MULTI_SLICE'
        status = @($runs | Where-Object Status -ne 'PASS').Count -eq 0 ? 'PASS' : 'FAIL'
        runId = $runId
        generatedAtUtc = $runUtc.ToString('o')
        finishedAtUtc = ([DateTime]::UtcNow).ToString('o')
        hmiCommit = $hmiCommit
        protocolHeadCommit = $protocolCommit
        allSlices = [bool]$AllSlices
        integrationSliceIndexSha256 = $sliceIndexSha256
        sharedSteps = [ordered]@{
            protocolG1 = [ordered]@{ status = $protocolStatus; log = if ($g1Result) { 'logs/protocol-g1.log' } else { $null } }
            build = [ordered]@{ exitCode = $build.ExitCode; log = 'logs/dotnet-build-release.log' }
            format = [ordered]@{ exitCode = $format.ExitCode; log = 'logs/dotnet-format-verify.log' }
            failures = @($failures)
        }
        slices = @($runs | ForEach-Object {
            [ordered]@{
                integrationSliceId = $_.Slice
                status = $_.Status
                selectedTestCount = $_.SelectedTestCount
                directory = Get-RelativeEvidencePath $multiSliceDirectory $_.Directory
                failures = @($_.Failures)
            }
        })
        slicesWithoutTests = @($slicesWithoutTests)
    } | ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath (Join-Path $multiSliceDirectory 'multi-slice-summary.json') -Encoding utf8
    Write-Host "Multi-slice evidence: $multiSliceDirectory"
    foreach ($skipped in $slicesWithoutTests) {
        Write-Host "No tests here, not certified: $($skipped.integrationSliceId)"
    }
}

# Every slice is written before anything throws, so one red slice never costs the others their evidence.
$failedRuns = @($runs | Where-Object Status -ne 'PASS')
if ($failedRuns.Count -gt 0) {
    $lines = foreach ($failedRun in $failedRuns) {
        $label = $failedRun.Slice ?? '整个解决方案'
        $failedRun.Failures | ForEach-Object { "[$label] $_" }
    }
    throw ("本机 G2 证据生成失败：" + [Environment]::NewLine + (@($lines) -join [Environment]::NewLine))
}

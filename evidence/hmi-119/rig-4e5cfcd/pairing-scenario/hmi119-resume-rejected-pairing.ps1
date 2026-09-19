#Requires -Version 7

<#
一次性配对验证（onboard-hmi#119 × control-server#187），不入库：续行命令在车上开锁之前被门禁挡住 →
车回 SlotOperationCommandRejected（correlationId = 续行命令 messageId）→ 服务端关闭恢复会话、CLOSED 快照到车 →
需求与旅程仍阻断 → 故障撤掉后同车能开新会话。

前半段照 g3-exception-resume：装载以 UNKNOWN 结束、两次重启（第二次的恢复状态报告带着服务端授权
RESUME_AFTER_REPAIR 要的已证实断点）。按「申请恢复」之前，经模拟器把装载仓的锁反馈钉成 0（未锁），
车载端门禁因此以 LOCK_NOT_CLOSED 挡住续行命令。仓门与货物的物理事实不变。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig.'
}
$ids = @('P-01', 'P-02', 'P-03', 'P-04', 'P-05', 'P-06', 'P-07')

function Get-SessionRow([string]$SessionId) {
    return @(Invoke-L2Query -Connection $connection -Sql (
        "SELECT * FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$SessionId'"))[0]
}

# --- 0. 装载以 UNKNOWN 结束，两次重启 -------------------------------------------------------------------

$load = Invoke-G3UnknownLoad $Context 'H119'
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$first = $load.First
$null = & $Context.RestartOnboard
$onboard = $Context.Onboard
$null = Wait-L2Condition -Description 'the restarted onboard replayed the result in its new session' `
    -Journal $journal -Criterion 'replayed-result' -TimeoutSeconds 240 `
    -Probe {
        $row = @((Get-G3Inbound $connection 'OperationResult') | Where-Object { $_.MessageId -eq $first.MessageId })[0]
        if ($null -ne $row -and $row.Generation -gt $first.Generation -and $null -ne $row.ResponseLine) { $row } else { $null }
    } -Until { param($v) $null -ne $v }
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$stageBefore = Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$journal.Note("Before the resume press: load $(Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") / stage $stageBefore / slot $(Get-G3SlotState $simulator $load.Slot).")

# --- 0b. 四个管理员恢复入口都在（onboard-hmi#112 的回归点，规格 21.2 第 7 条） --------------------------

$entries = @('申请恢复', '补偿清空', '故障交接', '强制机械恢复')
$null = Wait-G3ButtonOffered $onboard $journal '申请恢复' 'onboard-resume-entry-first' 120
$entryState = @($entries | ForEach-Object { "$_=$([bool]$onboard.ButtonAvailable($_))" })
$assertions.Add('P-00', '重启进入 RecoveryRequired 后四个管理员恢复入口都出现',
    (@($entryState | Where-Object { $_ -like '*=False' }).Count -eq 0), ($entries | ForEach-Object { "$_=True" }) -join ', ', $entryState -join ', ')

# --- 1. 锁反馈钉成 0，按「申请恢复」 -----------------------------------------------------------------------

$null = $simulator.Command('Put', "slots/$($load.Slot)/lock-feedback-override", @{ mode = 'FIXED_0' })
$overridden = Wait-L2Condition -Description 'the loaded slot reads unlocked' -Journal $journal `
    -Criterion 'lock-override' -TimeoutSeconds 15 `
    -Probe { Get-G3SlotState $simulator $load.Slot } -Until { param($v) $v -like 'CLOSED/EMPTY/0/*' }
$journal.Note("Slot $($load.Slot) after the override: $overridden.")
$unlockingBefore = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count

$offered = Wait-G3ButtonOffered $onboard $journal '申请恢复' 'onboard-resume-entry' 120
if (-not $offered) { Add-G3NotReached $assertions $ids '车载端没有给出「申请恢复」入口'; return }
$null = Invoke-G3ConfirmedButton $onboard $journal '申请恢复' '申请恢复原操作'

$resume = Wait-L2Condition -Description 'the server issued SlotOperationResumeCommand' `
    -Journal $journal -Criterion 'resume-command' -TimeoutSeconds 90 `
    -Probe {
        $commands = @((Get-G3Outbound $connection 'SlotOperationResumeCommand') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
        if ($commands.Count -ge 1) { $commands[0] }
        elseif (@($onboard.WindowTitles()) -contains '恢复申请失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($resume -is [string] -or $null -eq $resume) {
    $action = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object { [string]$_.Payload.demandId -eq $demandId })[0]
    Add-G3NotReached $assertions $ids "服务端没有下发续行命令（$(if ($action) { "$($action.Response) $($action.ResponsePayload | ConvertTo-Json -Depth 6 -Compress)" } else { '无 RecoveryActionSubmitted' })）"
    return
}
$resumeMessageId = [string]$resume.MessageId
$journal.Note("SlotOperationResumeCommand $resumeMessageId issued.")

# --- 2. 车回拒绝 ----------------------------------------------------------------------------------------

$rejection = Wait-L2Condition -Description 'the server received SlotOperationCommandRejected for the resume' `
    -Journal $journal -Criterion 'resume-rejected' -TimeoutSeconds 60 `
    -Probe { @((Get-G3Inbound $connection 'SlotOperationCommandRejected') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })[0] } `
    -Until { param($v) $null -ne $v }
$rejectionLine = if ($null -ne $rejection) {
    [string](Invoke-L2Query -Connection $connection -Sql "SELECT RequestJson AS Value FROM ProtocolInbox WHERE MessageId = '$($rejection.MessageId)'")[0].Value | ConvertFrom-Json -DateKind String
} else { $null }
$assertions.Add('P-01', '车回 SlotOperationCommandRejected：correlationId = 续行命令 messageId，attempt 正确，原因码 LOCK_NOT_CLOSED（已注册）',
    ($null -ne $rejectionLine -and ([string]$rejectionLine.correlationId).ToLowerInvariant() -eq $resumeMessageId -and
        [string]$rejectionLine.payload.slotOperationAttemptId -eq $attemptId -and [string]$rejectionLine.payload.problem.reasonCode -eq 'LOCK_NOT_CLOSED'),
    "correlationId $resumeMessageId / $attemptId / LOCK_NOT_CLOSED",
    $(if ($rejectionLine) { "correlationId $($rejectionLine.correlationId) / $($rejectionLine.payload.slotOperationAttemptId) / $($rejectionLine.payload.problem.reasonCode) / 服务端应答 $($rejection.Response)" } else { '(no rejection)' }))
if ($null -eq $rejection) { Add-G3NotReached $assertions @('P-02', 'P-03', 'P-04', 'P-05', 'P-06', 'P-07') '车没有回拒绝'; return }

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$unlockingAfter = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
$pulses = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $load.Slot })[0]
$assertions.Add('P-02', '续行被挡之后没有任何开锁：UNLOCKING 进度条数不变，装载仓输出复位、门仍关着',
    ($unlockingAfter -eq $unlockingBefore -and [string]$pulses.doorState -eq 'CLOSED' -and [string]$pulses.unlockOutputRaw -in @('0', 'False', 'false')),
    "UNLOCKING $unlockingBefore 不变 / CLOSED / 输出 0",
    "UNLOCKING $unlockingBefore → $unlockingAfter / $($pulses.doorState) / 输出 $($pulses.unlockOutputRaw)")

# --- 3. 服务端收口 --------------------------------------------------------------------------------------

$sessionId = [string]$resume.Payload.exceptionRecoverySessionId
$actionId = [string]$resume.Payload.recoveryActionId
$closed = Wait-L2Condition -Description 'the recovery session closed' -Journal $journal -Criterion 'session-closed' -TimeoutSeconds 60 `
    -Probe { $row = Get-SessionRow $sessionId; if ($null -ne $row -and [string]$row.State -eq 'CLOSED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
$workflow = Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$sessionDump = if ($closed) { ($closed | ConvertTo-Json -Depth 4 -Compress) } else { (Get-SessionRow $sessionId | ConvertTo-Json -Depth 4 -Compress) }
$assertions.Add('P-03', '服务端收口：续行工作流 RecoveryRequired，恢复会话 CLOSED 并带原因码',
    ($null -ne $closed -and $workflow -eq 'RecoveryRequired'),
    'workflow RecoveryRequired / session CLOSED', "workflow $workflow / session $sessionDump")

$closedSnapshot = Wait-L2Condition -Description 'the CLOSED recovery session snapshot reached the vehicle' -Journal $journal `
    -Criterion 'closed-snapshot-acked' -TimeoutSeconds 60 `
    -Probe {
        @((Get-G3Outbound $connection 'ExceptionRecoverySessionSnapshot') | Where-Object {
            [string]$_.Payload.exceptionRecoverySessionId -eq $sessionId -and [string]$_.Payload.state -eq 'CLOSED' -and $_.Acknowledged })[0]
    } -Until { param($v) $null -ne $v }
$assertions.Add('P-04', 'CLOSED 的 ExceptionRecoverySessionSnapshot 下发到车并被车确认',
    ($null -ne $closedSnapshot), 'CLOSED snapshot acknowledged',
    $(if ($closedSnapshot) { "messageId $($closedSnapshot.MessageId) acknowledged" } else { ((Get-G3Outbound $connection 'ExceptionRecoverySessionSnapshot') | ForEach-Object { "$($_.Payload.state) ack=$($_.Acknowledged)" }) -join '; ' }))

$load2 = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$stage2 = Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$assertions.Add('P-05', '需求与旅程仍阻断：装载仍 RecoveryRequired，旅程仍 Blocked',
    ($load2 -eq 'RecoveryRequired' -and $stage2 -eq 'Blocked'), 'RecoveryRequired / Blocked', "$load2 / $stage2")

# --- 4. 故障撤掉，同车开新会话 ----------------------------------------------------------------------------

$null = $simulator.Command('Put', "slots/$($load.Slot)/lock-feedback-override", @{ mode = 'AUTO' })
$null = Wait-L2Condition -Description 'the loaded slot reads locked again' -Journal $journal `
    -Criterion 'lock-restored' -TimeoutSeconds 15 `
    -Probe { Get-G3SlotState $simulator $load.Slot } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
$offeredAgain = Wait-G3ButtonOffered $onboard $journal '补偿清空' 'onboard-compensation-entry-after-close' 90
$assertions.Add('P-06', '会话关闭后车载端恢复入口重新出现（补偿清空）', [bool]$offeredAgain, 'offered', [string]$offeredAgain)
if (-not $offeredAgain) { Add-G3NotReached $assertions @('P-07') '恢复入口没有重新出现'; return }
$null = Invoke-G3ConfirmedButton $onboard $journal '补偿清空' '补偿清空'
$second = Wait-L2Condition -Description 'a new recovery session opened for the same vehicle' -Journal $journal `
    -Criterion 'second-session' -TimeoutSeconds 90 `
    -Probe {
        @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object {
            [string]$_.Payload.demandId -eq $demandId -and $_.Response -eq 'ExceptionRecoverySessionOpened' -and
            [string]$_.ResponsePayload.exceptionRecoverySessionId -ne $sessionId })[0]
    } -Until { param($v) $null -ne $v }
$assertions.Add('P-07', '同车能开新会话：第二次申请被 ExceptionRecoverySessionOpened 受理，会话 id 与被关闭的不同',
    ($null -ne $second), 'Opened, new session id',
    $(if ($second) { "Opened $($second.ResponsePayload.exceptionRecoverySessionId)" } else { ((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | ForEach-Object { "$($_.Response)" }) -join '; ' }))

$journal.Note('Scenario finished.')

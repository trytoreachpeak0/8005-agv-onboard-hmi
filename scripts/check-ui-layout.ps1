[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$hmiRoot = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $hmiRoot 'src\SQCD.Agv.Wpf\MainWindow.xaml'
$xaml = Get-Content -LiteralPath $xamlPath -Raw
# 倒计时的四个非默认档位（无倒计时、黄、红、到期）各有配色，最后 10 秒的闪烁有灭相位。
$stationDeadlineTiersStyled = @('Absent', 'Warning', 'Critical', 'Expired' | ForEach-Object {
        $xaml -match ('Binding StationDepartureCountdownTier\}" Value="' + $_ + '"')
    }) -notcontains $false -and ($xaml -match 'Binding StationDepartureCountdownDimmed\}" Value="True"')
# 倒计时容器宽高写死，切档时不挤动提示文案、不撑高提示行。
$stationDeadlineFixedSize = $xaml -match 'Grid\.Column="2"\s+Width="\d+"\s+Height="\d+"[^>]*Binding HasStationDepartureCountdown'
$checks = [ordered]@{
    prototypeViewport = ($xaml -match 'Width="1000"[\s\S]*Height="700"[\s\S]*WindowState="Maximized"')
    frontRearSlotGroups = ($xaml -match 'ItemsSource="\{Binding SlotGroups\}"') -and ($xaml -match 'UniformGrid Columns="4"') -and ($xaml -match 'Text="\{Binding OpeningSideText\}"')
    singlePrimaryScanAction = ($xaml -match 'x:Name="ScanTextBox"') -and ($xaml -match 'Content="手动提交"')
    safetyReviewAction = ($xaml -match 'Content="启动安全复核"')
    recoveryActions = ($xaml -match 'Content="重新上报结果"') -and ($xaml -match 'Content="重新打开仓门"') -and ($xaml -match 'Content="取消本次操作"')
    visibleDepartureFact = ($xaml -match 'Text="\{Binding DepartureText\}"')
    visibleJourneyFact = ($xaml -match 'Text="\{Binding VisitText\}"')
    # 旅程事实一行的方向与任务类型（批次6-03，onboard-hmi#115）：UIA 按 AutomationId 找它们。
    visibleStopDirectionAndTaskType = ($xaml -match 'AutomationProperties\.AutomationId="StopDirection"') -and ($xaml -match 'Text="\{Binding StopDirectionText\}"') -and ($xaml -match 'AutomationProperties\.AutomationId="TaskType"') -and ($xaml -match 'Text="\{Binding TaskTypeText\}"')
    # 站点功能名不上界面（v2 服务端保持为空），准入阻断原因也不上界面（只在服务端与看板，规格第 5.3 节）。
    noStationFunctionOrAdmissionReason = ($xaml -notmatch 'PublicStationFunction|StationFunction') -and ($xaml -notmatch 'Admission|准入')
    blockingGuidance = ($xaml -match 'Text="\{Binding Guidance\}"')
    visibleStationDeadline = ($xaml -match 'AutomationProperties\.AutomationId="StationDepartureCountdown"') -and ($xaml -match 'Text="\{Binding StationDepartureCountdownText\}"') -and $stationDeadlineTiersStyled -and $stationDeadlineFixedSize
    visibleSublotRejectionReason = ($xaml -match 'AutomationProperties\.AutomationId="SublotRejectionReason"') -and ($xaml -match 'AutomationProperties\.ItemStatus="\{Binding SublotRejectionReasonCode\}"') -and ($xaml -match 'Text="\{Binding SublotRejectionText\}"') -and ($xaml -match 'Binding HasSublotRejection, Converter')
    # 期待动作超时（REQ-0358，onboard-hmi#109）：提示区单独一行，UIA 按 AutomationId 找它。
    visibleExpectedActionOverdue = ($xaml -match 'AutomationProperties\.AutomationId="ExpectedActionOverdue"') -and ($xaml -match 'Text="\{Binding ExpectedActionOverdueText\}"') -and ($xaml -match 'Binding HasExpectedActionOverdue, Converter')
    # 异常处置会话的原因输入只跟着管理员恢复入口出现（CP-0005 第五节）。
    recoveryReasonInput = ($xaml -match 'AutomationProperties\.AutomationId="RecoveryReason"') -and ($xaml -match 'Text="\{Binding RecoveryReason, UpdateSourceTrigger=PropertyChanged\}"') -and ($xaml -match 'Binding HasRecoveryReasonInput, Converter') -and ($xaml -match 'MaxLength="500"') -and ($xaml -match 'IsEnabled="\{Binding IsRecoveryReasonEditable\}"') -and ($xaml -match 'Binding HasRecoveryReasonCarriedOver, Converter') -and ($xaml -match 'Text="会话已开，原因沿用开会话时填写的"')
    # 一站多条需求与多停靠计划（批次7-13，onboard-hmi#134）：清单列表与计划腿列表，UIA 按 AutomationId 找它们，ItemStatus 给原始值。
    visibleWorklistItems = ($xaml -match 'AutomationProperties\.AutomationId="WorklistItems"') -and ($xaml -match 'ItemsSource="\{Binding WorklistItems\}"') -and ($xaml -match 'AutomationProperties\.AutomationId="WorklistItemSide"') -and ($xaml -match 'AutomationProperties\.ItemStatus="\{Binding SideCode\}"')
    visibleJourneyPlanLegs = ($xaml -match 'AutomationProperties\.AutomationId="JourneyPlanLegs"') -and ($xaml -match 'ItemsSource="\{Binding JourneyPlanLegs\}"') -and ($xaml -match 'AutomationProperties\.ItemStatus="\{Binding ItemStatus\}"')
    # 持货等单、装满与装货结束原因（REQ-0354、REQ-0355）：各自一行，与离站倒计时分开，不合成一个期限。
    visibleCargoHoldingCountdown = ($xaml -match 'AutomationProperties\.AutomationId="CargoHoldingCountdown"') -and ($xaml -match 'Text="\{Binding CargoHoldingCountdownText\}"') -and ($xaml -match 'AutomationProperties\.ItemStatus="\{Binding CargoHoldingCountdownStatus\}"') -and ($xaml -match 'Binding HasCargoHoldingCountdown, Converter')
    visibleVehicleFullNotice = ($xaml -match 'AutomationProperties\.AutomationId="VehicleFullNotice"') -and ($xaml -match 'Text="\{Binding VehicleFullNoticeText\}"') -and ($xaml -match 'Binding HasVehicleFullNotice, Converter')
    visibleLoadingClosedReason = ($xaml -match 'AutomationProperties\.AutomationId="LoadingClosedReason"') -and ($xaml -match 'Text="\{Binding LoadingClosedReasonText\}"') -and ($xaml -match 'AutomationProperties\.ItemStatus="\{Binding LoadingClosedReasonCode\}"') -and ($xaml -match 'Binding HasLoadingClosedReason, Converter')
    # 多条清单项时扫码前取消由操作员在清单里选需求（批次7-14，onboard-hmi#135）：清单可选中，取消入口照常
    # 出现、没选中时按不动，旁边一句提示说要先选。取代批次7-13 那个「暂不可用」的占位按钮。
    selectableWorklistItems = ($xaml -match 'SelectedItem="\{Binding SelectedWorklistItem, Mode=TwoWay\}"') -and ($xaml -match '<ListBox[^>]*AutomationProperties\.AutomationId="WorklistItems"')
    # 显隐绑「入口在不在」、可按与否绑「按得动吗」，两个必须是不同的绑定：把 Visibility 也绑成
    # CanPressLoadCancellation，按钮在没选中时就整个消失了——那正是批次7-13 立这条检查要防的退化，
    # 而视图模型的判据看不见 XAML 绑定，这里是它唯一的守卫。
    loadCancellationSelectionHint = ($xaml -match 'AutomationProperties\.AutomationId="LoadCancellationSelection"') -and ($xaml -match 'Text="\{Binding LoadCancellationSelectionHintText\}"') -and ($xaml -match 'Binding HasLoadCancellationSelectionHint, Converter') -and ($xaml -match 'IsEnabled="\{Binding CanPressLoadCancellation\}"[^>]*Visibility="\{Binding CanRequestLoadCancellation, Converter[^>]*Content="取消装货"')
    # 「暂不可用」那句已被替换，不该再留在界面上：留着会和新提示同时出现，说两件互相矛盾的事。
    noLoadCancellationUnavailableHint = ($xaml -notmatch 'LoadCancellationUnavailable') -and ($xaml -notmatch '扫码前取消暂不可用')
    loadCorrectionTarget = ($xaml -match 'AutomationProperties\.AutomationId="LoadCorrectionTarget"') -and ($xaml -match 'Text="\{Binding LoadCorrectionTargetText\}"')
    # 回落到「上次完成的装货」的三个入口也标出目标子批（批次7-14，onboard-hmi#135）。
    recoveryFallbackTarget = ($xaml -match 'AutomationProperties\.AutomationId="RecoveryFallbackTarget"') -and ($xaml -match 'Text="\{Binding RecoveryFallbackTargetText\}"') -and ($xaml -match 'Binding HasRecoveryFallbackTarget, Converter')
    # 判故障只在服务端（REQ-0359）：本机界面不得出现判故障的按钮或绑定。
    noFaultDeclarationEntry = ($xaml -notmatch 'Content="[^"]*(判故障|判定故障|故障判定|人工判)') -and ($xaml -notmatch 'FaultDeclaration')
    dangerStyle = ($xaml -match 'Background="\{StaticResource DangerBrush\}"')
    boundedLogPanel = ($xaml -match 'Height="110"') -and ($xaml -match '操作记录（最近300条）')
}
$failed = @($checks.GetEnumerator() | Where-Object { -not $_.Value })
$result = [ordered]@{
    audit = 'LOCAL_UI_LAYOUT'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    hmiCommit = (& git -C $hmiRoot rev-parse HEAD).Trim()
    viewport = '1024x768-class / maximized'
    checks = $checks
    status = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }
    failures = @($failed.Name)
    manualChecksRequired = @(
        '触摸屏最小点击目标、DPI 缩放和键盘焦点需在目标工控机确认。',
        '阻断事实、仓位未知、恢复状态和安全复核流程需按真实操作员流程走查。',
        '本机脚本不能替代现场显示器、触摸屏或真实硬件动作验收。'
    )
}
$auditDirectory = Join-Path $hmiRoot 'evidence\audits'
New-Item -ItemType Directory -Force -Path $auditDirectory | Out-Null
$auditPath = Join-Path $auditDirectory ('ui-layout-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.json')
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $auditPath -Encoding utf8
Write-Host "UI layout audit: $auditPath"
Write-Host "Status: $($result.status)"
if ($failed.Count -gt 0) {
    throw ($failed.Name -join [Environment]::NewLine)
}

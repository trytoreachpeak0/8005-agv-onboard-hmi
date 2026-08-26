[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$hmiRoot = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $hmiRoot 'src\SQCD.Agv.Wpf\MainWindow.xaml'
$xaml = Get-Content -LiteralPath $xamlPath -Raw
$checks = [ordered]@{
    prototypeViewport = ($xaml -match 'Width="1000"[\s\S]*Height="700"[\s\S]*WindowState="Maximized"')
    eightSlotFourByTwo = ($xaml -match 'UniformGrid Columns="4" Rows="2"')
    singlePrimaryScanAction = ($xaml -match 'x:Name="ScanTextBox"') -and ($xaml -match 'Content="手动提交"')
    safetyReviewAction = ($xaml -match 'Content="启动安全复核"')
    recoveryActions = ($xaml -match 'Content="重新上报结果"') -and ($xaml -match 'Content="重新打开仓门"') -and ($xaml -match 'Content="取消本次操作"')
    visibleDepartureFact = ($xaml -match 'Text="\{Binding DepartureText\}"')
    visibleJourneyFact = ($xaml -match 'Text="\{Binding VisitText\}"')
    blockingGuidance = ($xaml -match 'Text="\{Binding Guidance\}"')
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

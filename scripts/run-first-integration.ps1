[CmdletBinding()]
param(
    [switch]$StartHmi,
    [switch]$KeepProcesses,
    [int]$ModbusPort = 1502,
    [int]$AutomationPort = 58006
)

$ErrorActionPreference = 'Stop'
$hmiRoot = Split-Path -Parent $PSScriptRoot
$simulatorRoot = Join-Path (Split-Path -Parent $hmiRoot) 'slots-simulator'
$simulatorProject = Join-Path $simulatorRoot 'src/SQCD_8005AGV_Simulator/SQCD_8005AGV_Simulator.csproj'
$hmiProject = Join-Path $hmiRoot 'src/SQCD.Agv.Wpf/SQCD.Agv.Wpf.csproj'

if ($ModbusPort -ne 1502 -or $AutomationPort -ne 58006) {
    throw '当前 simulator.settings.json 的联调脚本端口是 Modbus=1502、HTTP=58006；请先修改 simulator 配置后再使用非默认端口。'
}

if (-not (Test-Path -LiteralPath $simulatorProject)) {
    throw "找不到 slots-simulator 项目：$simulatorProject"
}

$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Wait-TcpPort {
    param([string]$HostName, [int]$Port, [int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync($HostName, $Port)
            if ($task.Wait(500) -and $client.Connected) {
                return
            }
        }
        finally {
            $client.Dispose()
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "等待端口 $HostName`:$Port 超时。"
}

try {
    Write-Host "启动 slots-simulator：Modbus=$ModbusPort，HTTP=$AutomationPort"
    $simulator = Start-Process dotnet `
        -ArgumentList @('run', '--project', $simulatorProject, '--configuration', 'Release') `
        -WorkingDirectory $simulatorRoot `
        -PassThru
    $started.Add($simulator)

    Wait-TcpPort -HostName '127.0.0.1' -Port $ModbusPort
    Wait-TcpPort -HostName '127.0.0.1' -Port $AutomationPort
    $health = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$AutomationPort/api/v1/health"
    Write-Host ("simulator health: " + ($health | ConvertTo-Json -Compress))

    if ($StartHmi) {
        if (-not (Test-Path -LiteralPath $hmiProject)) {
            throw "找不到 HMI 项目：$hmiProject"
        }

        Write-Host "启动 HMI。注意：HMI 与模拟器之间只能使用 Modbus，HTTP 仅供测试控制。"
        $hmi = Start-Process dotnet `
            -ArgumentList @('run', '--project', $hmiProject, '--configuration', 'Release') `
            -WorkingDirectory $hmiRoot `
            -PassThru
        $started.Add($hmi)
    }

    Write-Host ''
    Write-Host '联调环境已就绪。可使用：'
    Write-Host "  HTTP snapshot: http://127.0.0.1:$AutomationPort/api/v1/snapshot"
    Write-Host "  OpenAPI:       http://127.0.0.1:$AutomationPort/openapi/v1.json"
    Write-Host '  Modbus:        127.0.0.1:'$ModbusPort
    Write-Host 'ControlServer 仍需由联调方另行启动；本脚本不会伪造业务权威。'

    if (-not $KeepProcesses) {
        Write-Host '按 Enter 停止本脚本启动的进程。'
        [Console]::ReadLine() | Out-Null
    }
}
finally {
    if (-not $KeepProcesses) {
        foreach ($process in $started) {
            if (-not $process.HasExited) {
                try {
                    & taskkill.exe /PID $process.Id /T /F | Out-Null
                }
                catch {
                    Write-Warning "停止进程 $($process.Id) 失败：$($_.Exception.Message)"
                }
            }
        }
    }
}

[CmdletBinding()]
param(
    [string]$SimulatorRoot = ''
)

$ErrorActionPreference = 'Stop'
$hmiRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SimulatorRoot)) {
    $SimulatorRoot = Join-Path (Split-Path -Parent $hmiRoot) 'slots-simulator'
}

$projects = @(
    'tests\SQCD_8005AGV_Simulator.Tests\SQCD_8005AGV_Simulator.Tests.csproj',
    'tests\SQCD_8005AGV_Simulator.AutomationTests\SQCD_8005AGV_Simulator.AutomationTests.csproj'
)
foreach ($relativeProject in $projects) {
    $project = Join-Path $SimulatorRoot $relativeProject
    if (-not (Test-Path -LiteralPath $project)) {
        throw "找不到 slots-simulator 测试项目：$project"
    }
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($relativeProject in $projects) {
    $project = Join-Path $SimulatorRoot $relativeProject
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    Write-Host "运行 $name"
    Push-Location $SimulatorRoot
    try {
        $output = & dotnet run --project $relativeProject 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    $output | Write-Host
    $results.Add([pscustomobject]@{
        Project = $relativeProject
        ExitCode = $exitCode
        Output = ($output -join [Environment]::NewLine)
    })
    if ($exitCode -ne 0) {
        throw "$name 失败，退出码 $exitCode"
    }
}

Write-Host ''
Write-Host 'slots-simulator 本机黑盒测试全部通过。'
foreach ($result in $results) {
    Write-Host ("  {0}: PASS" -f $result.Project)
}

#Requires -Version 7
<#
.SYNOPSIS
    Offline reproduction for onboard-hmi#297: where a relative -StageRoot lands at the three
    consumers named in the ticket, without starting the real peers.

.DESCRIPTION
    scripts/run-staged-g3-recovery-ack-drop.ps1 needs the real simulator, control server and
    onboard WPF window and a desktop slot, so this reproduces each consumer with the script's own
    expressions and a stand-in:

      A  dll launch       Start-Process dotnet <Join-Path $StageRoot 'simulator\X.dll'>
                          -WorkingDirectory <Join-Path $StageRoot 'simulator'>   (script :389-391,
                          same shape :402-404, :420-422). The stand-in dll is a tiny probe.
      B  control database $env:ConnectionStrings__ControlServer = 'Data Source=' + (Join-Path
                          $StageRoot 'controlserver.db') read by a dotnet child whose working
                          directory is <stage>\control (script :329). The probe opens it with
                          Microsoft.Data.Sqlite, as the control server does. The probe dll is
                          started by absolute path here, so B is observed on its own instead of
                          being masked by A.
      C  proxy transcript  [IO.File]::WriteAllText((Join-Path $StageRoot 'proxy-events.ndjson'), '')
                          in this process, as RecoveryAckDropProxy.RunAsync does (script :47, :87).

    The caller's PowerShell location is <WorkRoot>\caller and the process's .NET current directory
    is <WorkRoot>\elsewhere: the two differ in any interactive session after a cd, and .NET calls
    follow the latter. The stage the caller means is <WorkRoot>\caller\stage.

    -StageRoot conversion: this script reads the target script's text. If it contains the entry
    conversion line, that exact line is executed here before the consumers; otherwise nothing is.
    Running this against the script before and after the fix is therefore the same command.

.OUTPUTS
    <WorkRoot>\result.json, printed as well.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ScriptPath,
    [Parameter(Mandatory)]
    [string]$WorkRoot
)

$ErrorActionPreference = 'Stop'
$ScriptPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ScriptPath)
$WorkRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)
if (Test-Path -LiteralPath $WorkRoot) {
    throw "WorkRoot must not exist yet: $WorkRoot"
}

$caller = Join-Path $WorkRoot 'caller'
$elsewhere = Join-Path $WorkRoot 'elsewhere'
$expectedStage = Join-Path $caller 'stage'
$probeSource = Join-Path $WorkRoot 'probe-src'
foreach ($directory in @($caller, $elsewhere, $probeSource,
        (Join-Path $expectedStage 'simulator'), (Join-Path $expectedStage 'control'))) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

# The probe: report the working directory and the dll path it was started with; when
# ConnectionStrings__ControlServer is set, open it with SQLite and report the file it created.
Set-Content -LiteralPath (Join-Path $probeSource 'Probe.csproj') -Encoding utf8NoBOM -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.30" />
  </ItemGroup>
</Project>
'@
Set-Content -LiteralPath (Join-Path $probeSource 'Program.cs') -Encoding utf8NoBOM -Value @'
using System.Text.Json;
using Microsoft.Data.Sqlite;

string? connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ControlServer");
Console.WriteLine(JsonSerializer.Serialize(new { workingDirectory = Environment.CurrentDirectory }));
string? database = null;
if (!string.IsNullOrEmpty(connectionString))
{
    using SqliteConnection connection = new(connectionString);
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE IF NOT EXISTS Probe (Id INTEGER)";
    command.ExecuteNonQuery();
    database = connection.DataSource;
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    entryAssembly = typeof(Program).Assembly.Location,
    databaseFile = database
}));
'@
# Built outside any repository so no Directory.Build.props, global.json or central package
# management applies to it.
& dotnet build $probeSource -c Release -o (Join-Path $WorkRoot 'probe-bin') --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "probe build failed: exit $LASTEXITCODE"
}
foreach ($role in @('simulator', 'control')) {
    Copy-Item -Path (Join-Path $WorkRoot 'probe-bin\*') -Destination (Join-Path $expectedStage $role) -Recurse
}

$conversionLine = '$StageRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StageRoot)'
$scriptLines = Get-Content -LiteralPath $ScriptPath
$conversionPresent = @($scriptLines | Where-Object { $_.Trim() -eq $conversionLine }).Count -gt 0
$scriptCommit = (& git -C (Split-Path -Parent $ScriptPath) log -1 --format=%H -- $ScriptPath).Trim()

function Test-UnderStage([string]$Path) {
    if ([string]::IsNullOrEmpty($Path)) {
        return $false
    }
    $full = [IO.Path]::GetFullPath($Path)
    return $full.StartsWith($expectedStage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

$savedLocation = Get-Location
$savedCurrentDirectory = [Environment]::CurrentDirectory
$savedConnectionString = $env:ConnectionStrings__ControlServer
try {
    Set-Location -LiteralPath $caller
    [Environment]::CurrentDirectory = $elsewhere

    $StageRoot = 'stage'
    if ($conversionPresent) {
        Invoke-Expression $conversionLine
    }

    # A: the script's simulator launch, verbatim apart from the dll name and -Wait.
    $simulator = Start-Process dotnet `
        -ArgumentList @(Join-Path $StageRoot 'simulator\Probe.dll') `
        -WorkingDirectory (Join-Path $StageRoot 'simulator') `
        -RedirectStandardOutput (Join-Path $StageRoot 'simulator.out.log') `
        -RedirectStandardError (Join-Path $StageRoot 'simulator.err.log') `
        -WindowStyle Hidden `
        -PassThru -Wait
    $simulatorOut = (Get-Content -Raw -LiteralPath (Join-Path $expectedStage 'simulator.out.log')) ?? ''
    $simulatorErr = (Get-Content -Raw -LiteralPath (Join-Path $expectedStage 'simulator.err.log')) ?? ''
    $simulatorLines = @($simulatorOut -split "`r?`n" | Where-Object { $_.StartsWith("{") } | ForEach-Object { $_ | ConvertFrom-Json })
    $simulatorReport = $simulator.ExitCode -eq 0 ? $simulatorLines[-1] : $null
    $siteA = [ordered]@{
        expression = "Start-Process dotnet -ArgumentList @(Join-Path `$StageRoot 'simulator\Probe.dll') -WorkingDirectory (Join-Path `$StageRoot 'simulator')"
        dllArgument = (Join-Path $StageRoot 'simulator\Probe.dll')
        exitCode = $simulator.ExitCode
        started = $null -ne $simulatorReport
        loadedFrom = $simulatorReport?.entryAssembly
        # Inferred, not observed: dotnet resolves a relative app path against its own working
        # directory, which is <stage>\simulator here.
        inferredResolution = [IO.Path]::GetFullPath((Join-Path $StageRoot 'simulator\Probe.dll'), (Join-Path $expectedStage 'simulator'))
        stdout = $simulatorOut.Trim()
        stderr = $simulatorErr.Trim()
        landedUnderStage = (Test-UnderStage $simulatorReport?.entryAssembly)
    }

    # B: the script's connection string, read by a child working in <stage>\control.
    $env:ConnectionStrings__ControlServer = 'Data Source=' + (Join-Path $StageRoot 'controlserver.db')
    $control = Start-Process dotnet `
        -ArgumentList @(Join-Path $expectedStage 'control\Probe.dll') `
        -WorkingDirectory (Join-Path $StageRoot 'control') `
        -RedirectStandardOutput (Join-Path $StageRoot 'control.out.log') `
        -RedirectStandardError (Join-Path $StageRoot 'control.err.log') `
        -WindowStyle Hidden `
        -PassThru -Wait
    $controlOut = (Get-Content -Raw -LiteralPath (Join-Path $expectedStage 'control.out.log')) ?? ''
    $controlErr = (Get-Content -Raw -LiteralPath (Join-Path $expectedStage 'control.err.log')) ?? ''
    $controlLines = @($controlOut -split "`r?`n" | Where-Object { $_.StartsWith("{") } | ForEach-Object { $_ | ConvertFrom-Json })
    $controlReport = $control.ExitCode -eq 0 ? $controlLines[-1] : $null
    $siteB = [ordered]@{
        expression = "`$env:ConnectionStrings__ControlServer = 'Data Source=' + (Join-Path `$StageRoot 'controlserver.db')"
        connectionString = $env:ConnectionStrings__ControlServer
        childWorkingDirectory = $controlLines[0]?.workingDirectory
        # Inferred, not observed: SQLite resolves a relative Data Source against the child process
        # working directory reported above.
        inferredResolution = [IO.Path]::GetFullPath((Join-Path $StageRoot 'controlserver.db'), ($controlLines[0]?.workingDirectory ?? (Join-Path $expectedStage 'control')))
        exitCode = $control.ExitCode
        databaseFile = $controlReport?.databaseFile
        stderr = $controlErr.Trim()
        landedUnderStage = (Test-UnderStage $controlReport?.databaseFile)
    }

    # C: the proxy's first write, File.WriteAllText(transcriptPath, ...), in this process.
    $proxyTranscript = Join-Path $StageRoot 'proxy-events.ndjson'
    $proxyError = $null
    try {
        [IO.File]::WriteAllText($proxyTranscript, '', [Text.UTF8Encoding]::new($false))
    }
    catch {
        $proxyError = $_.Exception.InnerException?.Message ?? $_.Exception.Message
    }
    $resolvedTranscript = [IO.Path]::GetFullPath($proxyTranscript)
    $siteC = [ordered]@{
        expression = "[IO.File]::WriteAllText((Join-Path `$StageRoot 'proxy-events.ndjson'), ...)"
        transcriptArgument = $proxyTranscript
        resolvedByDotNet = $resolvedTranscript
        written = $null -eq $proxyError -and (Test-Path -LiteralPath $resolvedTranscript)
        error = $proxyError
        landedUnderStage = $null -eq $proxyError -and (Test-UnderStage $resolvedTranscript)
    }
}
finally {
    [Environment]::CurrentDirectory = $savedCurrentDirectory
    Set-Location $savedLocation
    $env:ConnectionStrings__ControlServer = $savedConnectionString
}

$result = [ordered]@{
    ticket = 'onboard-hmi#297'
    scriptPath = $ScriptPath
    scriptLastCommit = $scriptCommit
    conversionPresentInScript = $conversionPresent
    callerLocation = $caller
    processCurrentDirectory = $elsewhere
    stageRootArgument = 'stage'
    stageRootAfterEntry = $StageRoot
    expectedStage = $expectedStage
    sites = [ordered]@{
        A_dllLaunch = $siteA
        B_controlDatabase = $siteB
        C_proxyTranscript = $siteC
    }
    allUnderStage = $siteA.landedUnderStage -and $siteB.landedUnderStage -and $siteC.landedUnderStage
}
$json = $result | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $WorkRoot 'result.json'), $json, [Text.UTF8Encoding]::new($false))
$json

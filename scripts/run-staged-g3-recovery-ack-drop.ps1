[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StageRoot,
    [string]$OnboardBuildCommit = ''
)

$ErrorActionPreference = 'Stop'

$controlCommit = 'cc6e2b97e4308fa14b519edf9a0089d0da7d6d14'
$simulatorCommit = 'fb5f7c593742bf98bc3957b8729a38aad5321f28'
$agvId = 'AGV-8005-STAGED-G3-ACK-DROP-01'
$proxyPort = 58114
$controlPort = 58115
$healthPort = 58117
$credential = [Guid]::NewGuid().ToString('N')
$proxyTranscript = Join-Path $StageRoot 'proxy-events.ndjson'

if ([string]::IsNullOrWhiteSpace($OnboardBuildCommit)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OnboardBuildCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw '无法从当前 checkout 解析 OnboardHmi commit；请显式传入 -OnboardBuildCommit。'
    }
}

if ($OnboardBuildCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "OnboardBuildCommit 不是完整 40 位 commit：$OnboardBuildCommit"
}

$onboardCommit = $OnboardBuildCommit.ToLowerInvariant()

$proxySource = @'
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static class RecoveryAckDropProxy
{
    private static readonly object LogGate = new();
    private static int _droppedRecoveryAck;
    private static int _connectionSequence;

    public static async Task RunAsync(
        int listenPort,
        int upstreamPort,
        string transcriptPath,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(transcriptPath, string.Empty, new UTF8Encoding(false));
        TcpListener listener = new(IPAddress.Loopback, listenPort);
        listener.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(listener.Stop);
        Log(transcriptPath, new Dictionary<string, object?>
        {
            ["at"] = DateTimeOffset.UtcNow,
            ["event"] = "proxy-listening",
            ["listenPort"] = listenPort,
            ["upstreamPort"] = upstreamPort
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient downstream;
                try
                {
                    downstream = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                int connectionId = Interlocked.Increment(ref _connectionSequence);
                await HandleConnectionAsync(
                    downstream,
                    upstreamPort,
                    transcriptPath,
                    connectionId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleConnectionAsync(
        TcpClient downstream,
        int upstreamPort,
        string transcriptPath,
        int connectionId,
        CancellationToken cancellationToken)
    {
        using (downstream)
        using (TcpClient upstream = new())
        using (CancellationTokenSource connectionStopping =
               CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, cancellationToken)
                    .ConfigureAwait(false);
                Log(transcriptPath, new Dictionary<string, object?>
                {
                    ["at"] = DateTimeOffset.UtcNow,
                    ["event"] = "connection-opened",
                    ["connectionId"] = connectionId
                });

                Task clientToServer = PumpAsync(
                    downstream.GetStream(),
                    upstream.GetStream(),
                    "client-to-server",
                    transcriptPath,
                    connectionId,
                    dropRecoveryAck: false,
                    connectionStopping.Token);
                Task serverToClient = PumpAsync(
                    upstream.GetStream(),
                    downstream.GetStream(),
                    "server-to-client",
                    transcriptPath,
                    connectionId,
                    dropRecoveryAck: true,
                    connectionStopping.Token);

                await Task.WhenAny(clientToServer, serverToClient).ConfigureAwait(false);
                connectionStopping.Cancel();
                downstream.Close();
                upstream.Close();
                try
                {
                    await Task.WhenAll(clientToServer, serverToClient).ConfigureAwait(false);
                }
                catch (Exception error) when (
                    error is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // A connection ending is expected during the injected recovery fault.
                }
            }
            catch (Exception error) when (
                error is IOException or OperationCanceledException or SocketException)
            {
                Log(transcriptPath, new Dictionary<string, object?>
                {
                    ["at"] = DateTimeOffset.UtcNow,
                    ["event"] = "connection-error",
                    ["connectionId"] = connectionId,
                    ["errorType"] = error.GetType().Name,
                    ["message"] = error.Message
                });
            }
            finally
            {
                Log(transcriptPath, new Dictionary<string, object?>
                {
                    ["at"] = DateTimeOffset.UtcNow,
                    ["event"] = "connection-closed",
                    ["connectionId"] = connectionId
                });
            }
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        string direction,
        string transcriptPath,
        int connectionId,
        bool dropRecoveryAck,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(
            source,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 65536,
            leaveOpen: true);
        await using StreamWriter writer = new(
            destination,
            new UTF8Encoding(false),
            bufferSize: 65536,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            Dictionary<string, object?> metadata = Describe(line, direction, connectionId);
            bool shouldDrop = false;
            if (dropRecoveryAck
                && string.Equals(metadata.GetValueOrDefault("messageType") as string, "DurableAck", StringComparison.Ordinal)
                && string.Equals(metadata.GetValueOrDefault("acceptedMessageType") as string, "RecoveryStateReport", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _droppedRecoveryAck, 1, 0) == 0)
            {
                shouldDrop = true;
            }

            metadata["action"] = shouldDrop ? "dropped" : "forwarded";
            Log(transcriptPath, metadata);
            if (!shouldDrop)
            {
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<string, object?> Describe(
        string line,
        string direction,
        int connectionId)
    {
        Dictionary<string, object?> metadata = new()
        {
            ["at"] = DateTimeOffset.UtcNow,
            ["event"] = "message",
            ["connectionId"] = connectionId,
            ["direction"] = direction
        };
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            metadata["messageType"] = root.GetProperty("messageType").GetString();
            metadata["messageId"] = root.GetProperty("messageId").GetString();
            if (root.TryGetProperty("sessionGeneration", out JsonElement generation)
                && generation.ValueKind == JsonValueKind.Number)
            {
                metadata["sessionGeneration"] = generation.GetInt64();
            }
            if (root.TryGetProperty("payload", out JsonElement payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("acceptedMessageType", out JsonElement acceptedType))
            {
                metadata["acceptedMessageType"] = acceptedType.GetString();
            }
        }
        catch (JsonException error)
        {
            metadata["parseError"] = error.Message;
        }
        return metadata;
    }

    private static void Log(string transcriptPath, Dictionary<string, object?> value)
    {
        string line = JsonSerializer.Serialize(value);
        lock (LogGate)
        {
            File.AppendAllText(transcriptPath, line + "\n", new UTF8Encoding(false));
        }
    }
}
'@

Add-Type -TypeDefinition $proxySource -Language CSharp

$onboardConfig = Join-Path $StageRoot 'onboard\appsettings.json'
$settings = Get-Content -Raw -LiteralPath $onboardConfig | ConvertFrom-Json
$settings.environment = 'Development'
$settings.agvId = $agvId
$settings.onboardInstanceId = 'OBU-8005-STAGED-G3-ACK-DROP-01'
$settings.wireToGate.enabled = $true
$settings.wireToGate.host = '127.0.0.1'
$settings.wireToGate.port = $proxyPort
$settings.wireToGate.onboardInstanceId = '56385c7b-a149-4127-a686-72334178093b'
$settings.wireToGate.onboardBuildCommit = $onboardCommit
$settings.wireToGate.journalPath = Join-Path $StageRoot 'onboard-journal.db'
$settings.logging.directory = Join-Path $StageRoot 'onboard-logs'
$settings.logging.writeToConsole = $true
$settings | ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $onboardConfig -Encoding utf8NoBOM

$env:CONTROL_SERVER_ONBOARD_CREDENTIAL = $credential
$env:ConnectionStrings__ControlServer = 'Data Source=' + (Join-Path $StageRoot 'controlserver.db')
$env:Health__url = "http://127.0.0.1:$healthPort"
$env:OnboardTransport__port = $controlPort.ToString([Globalization.CultureInfo]::InvariantCulture)

function Wait-TcpPort {
    param([int]$Port, [int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync('127.0.0.1', $Port)
            if ($task.Wait(300) -and $client.Connected) {
                return
            }
        }
        finally {
            $client.Dispose()
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "等待端口 $Port 超时。"
}

function Stop-ProcessSafely {
    param([Diagnostics.Process]$Process)

    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
        try {
            $Process.WaitForExit(5000) | Out-Null
        }
        catch {
            # Continue cleanup for the remaining processes.
        }
    }
}

function Read-ProxyEvents {
    if (-not (Test-Path -LiteralPath $proxyTranscript)) {
        return @()
    }

    return @(Get-Content -LiteralPath $proxyTranscript |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json })
}

$control = $null
$onboard = $null
$simulator = $null
$proxyStopping = [Threading.CancellationTokenSource]::new()
$proxyTask = $null
$version = $null
$simulatorHealth = $null
$sessionSnapshot = $null
$timedOutWaitingForReplay = $false

try {
    $simulator = Start-Process dotnet `
        -ArgumentList @(Join-Path $StageRoot 'simulator\SQCD_8005AGV_Simulator.dll') `
        -WorkingDirectory (Join-Path $StageRoot 'simulator') `
        -RedirectStandardOutput (Join-Path $StageRoot 'simulator.out.log') `
        -RedirectStandardError (Join-Path $StageRoot 'simulator.err.log') `
        -WindowStyle Hidden `
        -PassThru
    Wait-TcpPort -Port 1502
    Wait-TcpPort -Port 58006
    $simulatorHealth = Invoke-RestMethod `
        -Uri 'http://127.0.0.1:58006/api/v1/health' `
        -TimeoutSec 3

    $control = Start-Process dotnet `
        -ArgumentList @(Join-Path $StageRoot 'control\ControlServer.Host.dll') `
        -WorkingDirectory (Join-Path $StageRoot 'control') `
        -RedirectStandardOutput (Join-Path $StageRoot 'control.out.log') `
        -RedirectStandardError (Join-Path $StageRoot 'control.err.log') `
        -WindowStyle Hidden `
        -PassThru
    Wait-TcpPort -Port $controlPort
    Wait-TcpPort -Port $healthPort
    $version = Invoke-RestMethod -Uri "http://127.0.0.1:$healthPort/version" -TimeoutSec 3

    $proxyTask = [RecoveryAckDropProxy]::RunAsync(
        $proxyPort,
        $controlPort,
        $proxyTranscript,
        $proxyStopping.Token)
    Wait-TcpPort -Port $proxyPort

    $onboard = Start-Process dotnet `
        -ArgumentList @(Join-Path $StageRoot 'onboard\SQCD.Agv.Wpf.dll') `
        -WorkingDirectory (Join-Path $StageRoot 'onboard') `
        -RedirectStandardOutput (Join-Path $StageRoot 'onboard.out.log') `
        -RedirectStandardError (Join-Path $StageRoot 'onboard.err.log') `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $events = Read-ProxyEvents
        $recoveryReports = @($events | Where-Object {
            $_.event -eq 'message' `
                -and $_.direction -eq 'client-to-server' `
                -and $_.messageType -eq 'RecoveryStateReport'
        })
        $droppedAcks = @($events | Where-Object {
            $_.event -eq 'message' `
                -and $_.direction -eq 'server-to-client' `
                -and $_.messageType -eq 'DurableAck' `
                -and $_.acceptedMessageType -eq 'RecoveryStateReport' `
                -and $_.action -eq 'dropped'
        })
        $connectionCount = @($recoveryReports.connectionId | Sort-Object -Unique).Count
        if ($droppedAcks.Count -eq 1 -and $recoveryReports.Count -ge 2 -and $connectionCount -ge 2) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($droppedAcks.Count -ne 1 -or $recoveryReports.Count -lt 2 -or $connectionCount -lt 2) {
        $timedOutWaitingForReplay = $true
    }

    Start-Sleep -Seconds 4
    try {
        $sessionSnapshot = @(Invoke-RestMethod `
            -Uri "http://127.0.0.1:$healthPort/api/runtime/sessions" `
            -TimeoutSec 3 |
            Where-Object agvId -EQ $agvId |
            Select-Object -First 1)[0]
    }
    catch {
        $sessionSnapshot = $null
    }
}
finally {
    foreach ($process in @($onboard, $control, $simulator)) {
        Stop-ProcessSafely -Process $process
    }
    $proxyStopping.Cancel()
    if ($null -ne $proxyTask) {
        try {
            $proxyTask.Wait(5000) | Out-Null
        }
        catch {
            # The cancellation path closes the listener and active sockets.
        }
    }
    $proxyStopping.Dispose()
    $env:CONTROL_SERVER_ONBOARD_CREDENTIAL = $null
}

$events = Read-ProxyEvents
$recoveryReports = @($events | Where-Object {
    $_.event -eq 'message' `
        -and $_.direction -eq 'client-to-server' `
        -and $_.messageType -eq 'RecoveryStateReport'
})
$droppedAcks = @($events | Where-Object {
    $_.event -eq 'message' `
        -and $_.direction -eq 'server-to-client' `
        -and $_.messageType -eq 'DurableAck' `
        -and $_.acceptedMessageType -eq 'RecoveryStateReport' `
        -and $_.action -eq 'dropped'
})
$recoveryMessageIds = @($recoveryReports.messageId | Sort-Object -Unique)
$recoveryConnections = @($recoveryReports.connectionId | Sort-Object -Unique)
$databaseObservation = $null
Add-Type -Path (Join-Path $StageRoot 'control\Microsoft.Data.Sqlite.dll')
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
    'Data Source=' + (Join-Path $StageRoot 'controlserver.db') + ';Mode=ReadOnly')
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = @'
SELECT MessageId, ContentHash, COUNT(*)
FROM ProtocolInbox
WHERE MessageType = 'RecoveryStateReport'
GROUP BY MessageId, ContentHash
'@
    $reader = $command.ExecuteReader()
    $inboxRows = @()
    while ($reader.Read()) {
        $inboxRows += [ordered]@{
            messageId = $reader.GetString(0)
            contentHash = $reader.GetString(1)
            rowCount = $reader.GetInt64(2)
        }
    }
    $reader.Dispose()
    $command.Dispose()

    $command = $connection.CreateCommand()
    $command.CommandText = @'
SELECT SessionGeneration, Readiness, ReasonCode, CapabilityRevision, SafetyRevision
FROM SessionRecoveries
WHERE AgvId = $agvId
'@
    $command.Parameters.AddWithValue('$agvId', $agvId) | Out-Null
    $reader = $command.ExecuteReader()
    $sessionRow = $null
    if ($reader.Read()) {
        $sessionRow = [ordered]@{
            sessionGeneration = $reader.GetInt64(0)
            readiness = $reader.GetString(1)
            reasonCode = $reader.GetString(2)
            capabilityRevision = if ($reader.IsDBNull(3)) { $null } else { $reader.GetInt64(3) }
            safetyRevision = if ($reader.IsDBNull(4)) { $null } else { $reader.GetInt64(4) }
        }
    }
    $reader.Dispose()
    $command.Dispose()
    $databaseObservation = [ordered]@{
        recoveryStateReportInboxRows = $inboxRows
        currentSession = $sessionRow
    }
}
finally {
    $connection.Dispose()
}
$controlLog = if (Test-Path -LiteralPath (Join-Path $StageRoot 'control.out.log')) {
    Get-Content -Raw -LiteralPath (Join-Path $StageRoot 'control.out.log')
} else {
    ''
}
$onboardLog = if (Test-Path -LiteralPath (Join-Path $StageRoot 'onboard-logs\agv-20260826.log')) {
    Get-Content -Raw -LiteralPath (Join-Path $StageRoot 'onboard-logs\agv-20260826.log')
} else {
    ''
}
$staleGenerationObserved = $controlLog.Contains('Message does not belong to the current connection session.')
$onboardReconnectObserved = $onboardLog.Contains('将在2秒后重连')
$replayedExactIdentity = $recoveryReports.Count -ge 2 `
    -and $recoveryMessageIds.Count -eq 1 `
    -and $recoveryConnections.Count -ge 2
$recovered = $null -ne $sessionSnapshot `
    -and [long]$sessionSnapshot.sessionGeneration -ge 2 `
    -and $sessionSnapshot.reasonCode -eq 'DEPARTURE_SAFETY_NOT_READY'

$status = if ($recovered -and -not $staleGenerationObserved) {
    'PASS_WITH_OFFICIAL_SLICES_REMAINING_INCONCLUSIVE'
} elseif ($replayedExactIdentity -and $staleGenerationObserved) {
    'FAIL_CROSS_REPOSITORY_RECOVERY_REPLAY'
} elseif ($timedOutWaitingForReplay) {
    'INCONCLUSIVE_REPLAY_NOT_OBSERVED_BEFORE_TIMEOUT'
} else {
    'INCONCLUSIVE_UNCLASSIFIED'
}

$runResult = [ordered]@{
    schemaVersion = '1.0.0'
    runKind = 'STAGED_G3_REAL_PEERS_RECOVERY_ACK_DROP'
    runId = Split-Path -Leaf $StageRoot
    startedScope = @('FP-IS-00', 'FP-IS-06')
    status = $status
    protocol = [ordered]@{
        tag = $version.protocolTag
        commit = $version.protocolCommit
        manifestSha256 = $version.manifestSha256
    }
    commits = [ordered]@{
        controlServer = $controlCommit
        onboardHmi = $onboardCommit
        slotsSimulator = $simulatorCommit
    }
    config = [ordered]@{
        onboardConfigSha256 = (Get-FileHash -LiteralPath $onboardConfig -Algorithm SHA256).Hash.ToLowerInvariant()
        insecureLoopback = $true
        freshControlDatabase = $true
        freshOnboardJournal = $true
        fault = 'drop-first-RecoveryStateReport-DurableAck'
        ports = [ordered]@{
            modbus = 1502
            simulatorHttp = 58006
            proxy = $proxyPort
            onboardNdjson = $controlPort
            controlHealth = $healthPort
        }
    }
    observations = [ordered]@{
        droppedRecoveryAckCount = $droppedAcks.Count
        recoveryReportSendCount = $recoveryReports.Count
        recoveryReportMessageIds = $recoveryMessageIds
        recoveryReportConnectionIds = $recoveryConnections
        replayedExactMessageIdentityAcrossConnections = $replayedExactIdentity
        onboardReconnectObserved = $onboardReconnectObserved
        staleSessionGenerationRejectedByControlServer = $staleGenerationObserved
        sessionAfterFault = $sessionSnapshot
        database = $databaseObservation
    }
    expected = [ordered]@{
        outcome = 'The exact durable RecoveryStateReport is accepted after reconnect without duplicate side effects.'
        safeTerminalReadiness = 'RecoveryRequired'
        safeTerminalReasonCode = 'DEPARTURE_SAFETY_NOT_READY'
        movementAttempted = $false
    }
    simulatorHealth = $simulatorHealth
}

$logFiles = Get-ChildItem -Recurse -File -LiteralPath $StageRoot |
    Where-Object Name -Like '*.log'
$runResult['evidenceFiles'] = @($logFiles | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($StageRoot, $_.FullName).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        length = $_.Length
    }
})
$json = $runResult | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText(
    (Join-Path $StageRoot 'run-result.json'),
    $json,
    [Text.UTF8Encoding]::new($false))
$json

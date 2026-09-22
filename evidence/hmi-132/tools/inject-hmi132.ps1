#Requires -Version 7
# Fault injection for onboard-hmi#132: each mutation must apply exactly once, is built and run against the new G2
# test only, and is restored from a byte backup (never git checkout) with a hash check.
param([Parameter(Mandatory)][string]$Worktree, [Parameter(Mandatory)][string]$OutDir, [string[]]$Only)
$ErrorActionPreference = 'Stop'
Set-Location $Worktree
New-Item -ItemType Directory -Force $OutDir | Out-Null

$business = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.cs'
$client = 'src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs'
$n = "`n"

$mutations = [ordered]@{
    M1 = @{
        File = $business
        Old  = "            await _session.ResendOperationResultAsync(resultKey, cancellationToken).ConfigureAwait(false);$n            return true;"
        New  = "            await Task.CompletedTask.ConfigureAwait(false);$n            return false; // M1: nothing resends the result after readiness"
    }
    M2 = @{
        File = $client
        Old  = "            IReadOnlyList<WireToGateDurableMessage> pendingResultReplays =$n                await SendRecoveryStateReportAsync("
        New  = "            // M2: the handshake reads the outbox again and replays a late result before its readiness$n            foreach (WireToGateDurableMessage late in (await _journal.ReadUnacknowledgedOutgoingAsync(cancellationToken).ConfigureAwait(false)).Where(m => m.MessageType == `"OperationResult`"))$n            {$n                await ReplayDurableOutgoingAsync(late, generation, cancellationToken).ConfigureAwait(false);$n            }$n$n            IReadOnlyList<WireToGateDurableMessage> pendingResultReplays =$n                await SendRecoveryStateReportAsync("
    }
    M3 = @{
        File = $business
        Old  = "                        return InterruptedOperationSettlement.ResultAwaitingAck;$n                    }$n$n                    await RecordAcknowledgedCompletedResultAsync(context, cancellationToken).ConfigureAwait(false);"
        New  = "                        return InterruptedOperationSettlement.ResultAwaitingAck;$n                    }$n$n                    await RecordAcknowledgedCompletedResultAsync(context, cancellationToken).ConfigureAwait(false);$n                    await RecordAcknowledgedCompletedResultAsync(context, cancellationToken).ConfigureAwait(false); // M3: settled twice"
    }
}

# `-Only M1,M3` through pwsh -File arrives as the single string 'M1,M3', which names no mutation: without these checks
# nothing ran and the script still exited 0 (review of PR #193). Through pwsh -File, pass one name per call (-Only M1).
# `@($Only | ...)` on a null $Only sends one $null down the pipe and would call it unknown; no -Only means all.
$unknown = $Only ? @($Only | Where-Object { $_ -notin $mutations.Keys }) : @()
if ($unknown.Count -gt 0) { throw "Unknown mutation name(s): $($unknown -join ', '). Known: $($mutations.Keys -join ', ')." }
$ran = 0

foreach ($name in $mutations.Keys) {
    if ($Only -and $name -notin $Only) { continue }
    $ran++
    $m = $mutations[$name]
    $path = Join-Path $Worktree $m.File
    $backup = "$path.hmi132bak"
    $original = [IO.File]::ReadAllText($path)
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash
    $count = ([regex]::Matches($original, [regex]::Escape($m.Old))).Count
    if ($count -ne 1) { throw "$name matched $count times in $($m.File); expected exactly 1." }
    Copy-Item $path $backup -Force
    try {
        [IO.File]::WriteAllText($path, $original.Replace($m.Old, $m.New), [Text.UTF8Encoding]::new($false))
        $diffLines = (git diff --numstat -- $m.File) -join ' '
        "$name applied: $diffLines" | Tee-Object -FilePath (Join-Path $OutDir "$name.txt")
        $build = dotnet build tests/SQCD.Agv.WireToGateG2Tests -c Release --no-incremental 2>&1
        $build | Select-Object -Last 4 | Tee-Object -FilePath (Join-Path $OutDir "$name.txt") -Append
        if (-not ($build -match ' 0 Error\(s\)')) { throw "$name did not compile." }
        dotnet test tests/SQCD.Agv.WireToGateG2Tests -c Release --no-build `
            --filter 'FullyQualifiedName~AResultPutOnFileInsideTheHandshakeWindow' `
            --logger 'console;verbosity=normal' 2>&1 | Out-File -Append (Join-Path $OutDir "$name.txt")
        "$name test exit: $LASTEXITCODE" | Tee-Object -FilePath (Join-Path $OutDir "$name.txt") -Append
    }
    finally {
        Copy-Item $backup $path -Force
        Remove-Item $backup
        # The backup carries its old timestamp, older than the binary the mutation built; without this the next
        # incremental build keeps the mutated assembly (seen on the first run: M3 ran with M2 still compiled in).
        (Get-Item $path).LastWriteTimeUtc = [DateTime]::UtcNow
        $restored = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($restored -ne $hash) { throw "$name restore hash mismatch for $($m.File)." }
        "$name restored: $restored" | Tee-Object -FilePath (Join-Path $OutDir "$name.txt") -Append
    }
}

if ($ran -eq 0) { throw "No mutation ran." }

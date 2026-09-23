import io
import sys

root, name = sys.argv[1], sys.argv[2]
CLIENT = 'src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs'
BUSINESS = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.cs'
NL = chr(10)

MUTATIONS = {
    'M1': (CLIENT,
           '            if (connection.Epoch != connectionEpoch)' + NL +
           '            {' + NL +
           '                throw new WireToGateConnectionGoneException("WIRE_TO_GATE连接已换代，这条报文不属于当前连接。");' + NL +
           '            }' + NL + NL,
           ''),
    # The epoch-and-writer handle is taken away last, after the writer is disposed, instead of first.
    'M7': None,
    'M1b': (CLIENT,
            'await SendLineAsync(stored.WireLine, connectionEpoch, cancellationToken).ConfigureAwait(false);',
            'await SendLineAsync(stored.WireLine, CurrentConnectionEpoch, cancellationToken).ConfigureAwait(false);'),
    'M2': (BUSINESS,
           '            if (exception is WireToGateConnectionGoneException\n'
           '                || judgedOnGeneration is not null\n'
           '                    && _session.Current.SessionGeneration != judgedOnGeneration)\n',
           '            if (Environment.TickCount64 < 0)\n'),
    'M4': (BUSINESS,
           '            if (exception is WireToGateConnectionGoneException' + NL +
           '                || judgedOnGeneration is not null' + NL +
           '                    && _session.Current.SessionGeneration != judgedOnGeneration)' + NL,
           '            if (Environment.TickCount64 >= 0)' + NL),
    # The new session's readiness no longer triggers a safety report: nothing resends the refused one.
    'M5': (BUSINESS,
           '        if (CanPublishSafetyRevision(args.Value))' + NL +
           '        {' + NL +
           '            RequestSafetyStateChange();' + NL,
           '        if (CanPublishSafetyRevision(args.Value) && Environment.TickCount64 < 0)' + NL +
           '        {' + NL +
           '            RequestSafetyStateChange();' + NL),
    # Neither trigger resends at readiness: the session-state one (M5) and #197's controller line.
    'M5x': None,
    # The refused report is dropped: the new session gets a fresh report, not the one that was refused.
    'M6': (BUSINESS,
           '                    exception);' + NL +
           '                return;' + NL +
           '            }' + NL + NL +
           '            _logger.Write(' + NL +
           '                LogSeverity.Error,' + NL,
           '                    exception);' + NL +
           '                _pendingSafetyChange = null;' + NL +
           '                return;' + NL +
           '            }' + NL + NL +
           '            _logger.Write(' + NL +
           '                LogSeverity.Error,' + NL),
    'M3': (BUSINESS,
           '            if (exception is WireToGateConnectionGoneException\n'
           '                || judgedOnGeneration is not null\n',
           '            if (judgedOnGeneration is not null\n'),
}

MUTATIONS['M5x'] = [MUTATIONS['M5'], (BUSINESS,
    '    public void RefreshSafetyAfterFatalFaultLatchChange() => _ = Task.Run(RequestSafetyStateChange);',
    '    public void RefreshSafetyAfterFatalFaultLatchChange() => GC.KeepAlive(this);')]

MUTATIONS['M7'] = [
    (CLIENT,
     'WriteConnection? writeConnection = Interlocked.Exchange(ref _writeConnection, null);',
     'WriteConnection? writeConnection = Volatile.Read(ref _writeConnection);'),
    (CLIENT,
     '            await writeConnection.Writer.DisposeAsync().ConfigureAwait(false);' + NL,
     '            await writeConnection.Writer.DisposeAsync().ConfigureAwait(false);' + NL +
     '            Volatile.Write(ref _writeConnection, null);' + NL)]

# --- round 3 (review of 2466113): M-B scan, M-A close, item 5 rejection, and which path delivers OperationResult.
SCAN_START = ('            _ = Task.Run(RunStaleResendAsync);', '            GC.KeepAlive(this);')
# The scan never runs.
MUTATIONS['M9'] = (CLIENT,) + SCAN_START
# Only the request once the handshake has its receive loop up is gone.
MUTATIONS['M9a'] = (CLIENT,
    '            StartReceiveLoop(generation);' + NL +
    '            // After the loop is up: the pass waits for its acknowledgements through it (onboard-hmi#204).' + NL +
    '            RequestStaleResend();' + NL,
    '            StartReceiveLoop(generation);' + NL)
# Only the request when a durable write is refused as its connection gone is gone.
MUTATIONS['M9b'] = (CLIENT,
    '                RequestStaleResend();' + NL + '                throw;' + NL,
    '                throw;' + NL)
# Close stops at the writer it cannot dispose, as before.
MUTATIONS['M10'] = (CLIENT,
    '            catch (Exception exception) when (exception is InvalidOperationException or IOException)' + NL +
    '            {' + NL +
    '                // A write is still pending on it',
    '            catch (Exception exception) when (exception is InvalidOperationException or IOException && Environment.TickCount64 < 0)' + NL +
    '            {' + NL +
    '                // A write is still pending on it')
# A write ended by the close is passed on as whatever the torn-down stream threw.
MUTATIONS['M11'] = (CLIENT,
    '                exception is IOException or InvalidOperationException' + NL +
    '                && !ReferenceEquals(Volatile.Read(ref _writeConnection), connection))',
    '                exception is IOException or InvalidOperationException' + NL +
    '                && !ReferenceEquals(Volatile.Read(ref _writeConnection), connection)' + NL +
    '                && Environment.TickCount64 < 0)')
# The rejection no longer checks which session the command belongs to.
MUTATIONS['M12'] = (CLIENT,
    '        if (Interlocked.Read(ref _receiveLoopGeneration) != command.SessionGeneration)',
    '        if (Interlocked.Read(ref _receiveLoopGeneration) != command.SessionGeneration && Environment.TickCount64 < 0)')
# No scan, and no #127 restore right after a refused result while a session is up.
MUTATIONS['M9R'] = [MUTATIONS['M9'], (BUSINESS,
    '            if (restoreAfterRelease' + NL + '                || resultUnacknowledged' + NL,
    '            if (restoreAfterRelease' + NL + '                || resultUnacknowledged && Environment.TickCount64 < 0' + NL)]
# No scan, and the #127 resend of an unacknowledged result sends nothing.
MUTATIONS['M9T'] = [MUTATIONS['M9'], (BUSINESS,
    '        CancellationToken cancellationToken)' + NL + '    {' + NL +
    '        if (_session.Current.Readiness is not (WireToGateSessionReadiness.Ready' + NL,
    '        CancellationToken cancellationToken)' + NL + '    {' + NL +
    '        if (Environment.TickCount64 >= 0)' + NL + '        {' + NL + '            return false;' + NL + '        }' + NL + NL +
    '        if (_session.Current.Readiness is not (WireToGateSessionReadiness.Ready' + NL)]

# The pass no longer marks its flow: test doubles cannot tell its outbox read from the handshake's.
MUTATIONS['M13'] = (CLIENT, '        StaleResendFlow.Value = true;' + NL, '')

# Not a mutation: lets the round-3 tests compile against 2466113's client, which has no stale-row pass and so
# nothing that could ever set the flag. Applied by run10.ps1's HEAD state only.
MUTATIONS['SHIM'] = (CLIENT,
    'public sealed class WireToGateSessionClient : IAsyncDisposable' + NL + '{' + NL,
    'public sealed class WireToGateSessionClient : IAsyncDisposable' + NL + '{' + NL +
    '    public static bool InStaleResendPass => false;' + NL + NL)

spec = MUTATIONS[name]
for rel, old, new in (spec if isinstance(spec, list) else [spec]):
    p = root + '/' + rel
    s = io.open(p, encoding='utf-8', newline='').read()
    n = s.count(old)
    print(name, rel.split('/')[-1], 'matches', n)
    if n != 1:
        sys.exit(1)
    io.open(p, 'w', encoding='utf-8', newline='').write(s.replace(old, new))

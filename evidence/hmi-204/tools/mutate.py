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

spec = MUTATIONS[name]
for rel, old, new in (spec if isinstance(spec, list) else [spec]):
    p = root + '/' + rel
    s = io.open(p, encoding='utf-8', newline='').read()
    n = s.count(old)
    print(name, rel.split('/')[-1], 'matches', n)
    if n != 1:
        sys.exit(1)
    io.open(p, 'w', encoding='utf-8', newline='').write(s.replace(old, new))

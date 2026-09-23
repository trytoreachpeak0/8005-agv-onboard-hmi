import io
import sys

root, name = sys.argv[1], sys.argv[2]
CLIENT = 'src/SQCD.Agv.Infrastructure/WireToGateSessionClient.cs'
BUSINESS = 'src/SQCD.Agv.Wpf/WireToGateBusinessService.cs'
NL = chr(10)

MUTATIONS = {
    'M1': (CLIENT,
           '            if (Volatile.Read(ref _connectionEpoch) != connectionEpoch)\n'
           '            {\n'
           '                throw new WireToGateConnectionGoneException("WIRE_TO_GATE连接已换代，这条报文不属于当前连接。");\n'
           '            }\n\n',
           ''),
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
    'M3': (BUSINESS,
           '            if (exception is WireToGateConnectionGoneException\n'
           '                || judgedOnGeneration is not null\n',
           '            if (judgedOnGeneration is not null\n'),
}

rel, old, new = MUTATIONS[name]
p = root + '/' + rel
s = io.open(p, encoding='utf-8', newline='').read()
n = s.count(old)
print(name, rel.split('/')[-1], 'matches', n)
if n != 1:
    sys.exit(1)
io.open(p, 'w', encoding='utf-8', newline='').write(s.replace(old, new))

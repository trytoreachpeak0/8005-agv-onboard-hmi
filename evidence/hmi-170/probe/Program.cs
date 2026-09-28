// Read-only field probe for onboard-hmi#170.
//
// It runs the product's own ModbusTcpIoModuleClient poll loop (FC01 + FC02 only -- this program
// never calls PulseUnlockAsync, so no FC05 can leave it) against a real IO module and records:
//   events.ndjson  connection transitions, connected snapshots, the client's own log lines
//   frames.ndjson  (relay mode) every MBAP frame in both directions, as bytes on the wire
//   summary.json   the verdicts computed from the two files above
//
// Relay mode puts a byte-transparent TCP relay on loopback between the client and the module.
// The relay forwards every byte unchanged and only watches a copy, so the client under test is
// still the one doing Modbus; the relay is how we see the transaction ids, which the client
// deliberately does not expose. Direct mode runs without the relay, so the connection verdict
// can be taken without anything in between.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace C2000TxidProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        using var recorder = new Recorder(options.OutputDirectory);
        recorder.Event("probe-start", new
        {
            options.Mode,
            options.ModuleHost,
            options.ModulePort,
            options.UnitId,
            options.DurationSeconds,
            options.PollIntervalMs,
            options.ReconnectDelayMs,
            options.StopOnDisconnect,
            options.StopOnRuleViolation,
            clientAssembly = typeof(ModbusTcpIoModuleClient).Assembly.FullName,
            clientVersion = typeof(ModbusTcpIoModuleClient).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            machine = Environment.MachineName,
            vehicleClockUtc = DateTimeOffset.UtcNow,
        });

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            recorder.Event("stop-requested", new { reason = "ctrl-c" });
            stopping.Cancel();
        };

        Relay? relay = null;
        string clientHost = options.ModuleHost;
        int clientPort = options.ModulePort;
        if (options.Mode == "relay")
        {
            relay = new Relay(options.ModuleHost, options.ModulePort, recorder, () =>
            {
                if (!options.StopOnRuleViolation)
                {
                    // Recorded by the relay already; this run collects the rule instead of judging it.
                    return;
                }

                recorder.Event("stop-requested", new { reason = "response id is not (request id & 0xFF)" });
                stopping.Cancel();
            });
            clientPort = relay.Start();
            clientHost = IPAddress.Loopback.ToString();
            recorder.Event("relay-listening", new { port = clientPort });
        }

        var settings = new IoModuleSettings
        {
            Host = clientHost,
            Port = clientPort,
            UnitId = options.UnitId,
            PollIntervalMs = options.PollIntervalMs,
            ReconnectDelayMs = options.ReconnectDelayMs,
        };

        var transitions = new List<(double T, bool Connected)>();
        bool probeStopping = false;
        var connectedSnapshotTimes = new List<double>();
        string? lastIo = null;
        var logger = new RecordingLogger(recorder);
        await using var client = new ModbusTcpIoModuleClient(settings, logger);

        client.ConnectionChanged += (_, e) =>
        {
            double t = recorder.Elapsed;
            bool duringStop = Volatile.Read(ref probeStopping);
            recorder.Event("connection", new { connected = e.Value, duringProbeStop = duringStop });
            if (duringStop)
            {
                // StopAsync publishes its own disconnect; that one is ours, not the module's.
                return;
            }

            lock (transitions)
            {
                transitions.Add((t, e.Value));
            }

            if (!e.Value && options.StopOnDisconnect)
            {
                recorder.Event("stop-requested", new { reason = "disconnect with --stop-on-disconnect" });
                stopping.Cancel();
            }
        };
        client.SnapshotChanged += (_, e) =>
        {
            IoSnapshot snapshot = e.Value;
            if (!snapshot.IsConnected)
            {
                return;
            }

            lock (connectedSnapshotTimes)
            {
                connectedSnapshotTimes.Add(recorder.Elapsed);
            }

            string io = string.Join(' ', snapshot.Lockers.Select(l =>
                $"{Bit(l.UnlockOutputRaw)}{Bit(l.LockFeedbackRaw)}{Bit(l.LightCurtainRaw)}"));
            if (io != lastIo)
            {
                recorder.Event("io", new { lockers = io, legend = "per slot: DO lockDI curtainDI" });
                lastIo = io;
            }
        };

        await client.StartAsync(stopping.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }

        // Read the counter before StopAsync publishes its own disconnect.
        int counter = ReadTransactionCounter(client);
        bool connectedAtEnd = client.IsConnected;
        Volatile.Write(ref probeStopping, true);
        recorder.Event("stopping", new { connectedAtEnd, transactionCounter = counter });
        await client.StopAsync();
        if (relay is not null)
        {
            await relay.StopAsync();
        }

        using Relay? disposeRelay = relay;

        Summary summary = Summary.Build(options, transitions, connectedSnapshotTimes, counter, connectedAtEnd, relay);
        recorder.WriteSummary(summary);
        Console.WriteLine(JsonSerializer.Serialize(summary, Recorder.JsonOptions));
        return summary.Pass ? 0 : 1;
    }

    private static string Bit(bool? value) => value switch { true => "1", false => "0", null => "?" };

    // The free-running counter is private; reading it is how direct mode learns how many ids the
    // client consumed without anything on the wire. Name checked here, so a rename fails loudly.
    private static int ReadTransactionCounter(ModbusTcpIoModuleClient client)
    {
        FieldInfo field = typeof(ModbusTcpIoModuleClient).GetField("_transactionId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(ModbusTcpIoModuleClient), "_transactionId");
        return (int)field.GetValue(client)!;
    }
}

internal sealed record Options(
    string Mode,
    string ModuleHost,
    int ModulePort,
    byte UnitId,
    int DurationSeconds,
    int PollIntervalMs,
    int ReconnectDelayMs,
    bool StopOnDisconnect,
    bool StopOnRuleViolation,
    string OutputDirectory)
{
    public const string Usage =
        "C2000TxidProbe --mode direct|relay --out <dir> [--host 192.168.71.150] [--port 502] [--unit-id 255] " +
        "[--seconds 300] [--poll-ms 100] [--reconnect-ms 1000] [--keep-going-after-disconnect] [--keep-going-after-rule-violation]";

    public static Options Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        bool keepGoing = false;
        bool keepGoingAfterRule = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--keep-going-after-disconnect")
            {
                keepGoing = true;
                continue;
            }

            if (args[i] == "--keep-going-after-rule-violation")
            {
                keepGoingAfterRule = true;
                continue;
            }

            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            }

            map[args[i][2..]] = args[++i];
        }

        string mode = Get(map, "mode", null);
        if (mode is not ("direct" or "relay"))
        {
            throw new ArgumentException("--mode must be direct or relay.");
        }

        return new Options(
            mode,
            Get(map, "host", "192.168.71.150"),
            int.Parse(Get(map, "port", "502"), CultureInfo.InvariantCulture),
            byte.Parse(Get(map, "unit-id", "255"), CultureInfo.InvariantCulture),
            int.Parse(Get(map, "seconds", "300"), CultureInfo.InvariantCulture),
            int.Parse(Get(map, "poll-ms", "100"), CultureInfo.InvariantCulture),
            int.Parse(Get(map, "reconnect-ms", "1000"), CultureInfo.InvariantCulture),
            !keepGoing,
            !keepGoingAfterRule,
            Get(map, "out", null));
    }

    private static string Get(Dictionary<string, string> map, string key, string? fallback) =>
        map.TryGetValue(key, out string? value) ? value
        : fallback ?? throw new ArgumentException($"--{key} is required.");
}

internal sealed class Recorder : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LineOptions = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StreamWriter _events;
    private readonly StreamWriter _frames;
    private readonly string _directory;
    private readonly object _gate = new();

    public Recorder(string directory)
    {
        _directory = directory;
        _events = new StreamWriter(Path.Combine(directory, "events.ndjson"), append: false) { AutoFlush = true };
        _frames = new StreamWriter(Path.Combine(directory, "frames.ndjson"), append: false) { AutoFlush = false };
    }

    /// <summary>Milliseconds since the probe started, monotonic. The vehicle wall clock is not trusted.</summary>
    public double Elapsed => _clock.Elapsed.TotalMilliseconds;

    public void Event(string kind, object data)
    {
        string line = JsonSerializer.Serialize(new { t = Math.Round(Elapsed, 3), kind, data }, LineOptions);
        lock (_gate)
        {
            _events.WriteLine(line);
        }
    }

    public void Frame(FrameRecord frame)
    {
        string line = JsonSerializer.Serialize(frame, LineOptions);
        lock (_gate)
        {
            _frames.WriteLine(line);
        }
    }

    public void WriteSummary(Summary summary)
    {
        lock (_gate)
        {
            _frames.Flush();
        }

        File.WriteAllText(Path.Combine(_directory, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _events.Dispose();
            _frames.Dispose();
        }
    }
}

internal sealed class RecordingLogger(Recorder recorder) : IAppLogger
{
    public event EventHandler<LogEntryEventArgs>? EntryWritten;

    public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
    {
        recorder.Event("client-log", new { severity = severity.ToString(), source, message, exception = exception?.ToString() });
        _ = EntryWritten;
    }
}

internal sealed record FrameRecord(
    double T,
    int Connection,
    string Dir,
    int TransactionId,
    int ProtocolId,
    int Length,
    int UnitId,
    int Function,
    string Hex);

/// <summary>
/// Byte-transparent loopback relay. Each accepted client connection gets its own upstream
/// connection; bytes are forwarded as they arrive and a copy is framed by MBAP length for the log.
/// </summary>
internal sealed class Relay(string upstreamHost, int upstreamPort, Recorder recorder, Action onRuleViolation) : IDisposable
{
    private readonly Dictionary<int, Queue<int>> _outstanding = [];
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _tasks = [];
    private readonly List<FrameRecord> _frames = [];
    private int _connections;

    public IReadOnlyList<FrameRecord> Frames
    {
        get
        {
            lock (_frames)
            {
                return [.. _frames];
            }
        }
    }

    public int Connections => Volatile.Read(ref _connections);

    public int Start()
    {
        _listener.Start();
        _tasks.Add(Task.Run(AcceptLoopAsync));
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _stop.Dispose();
        _listener.Dispose();
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await Task.WhenAll(_tasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient downstream;
            try
            {
                downstream = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            int connection = Interlocked.Increment(ref _connections);
            lock (_tasks)
            {
                _tasks.Add(Task.Run(() => PumpConnectionAsync(connection, downstream)));
            }
        }
    }

    private async Task PumpConnectionAsync(int connection, TcpClient downstream)
    {
        using (downstream)
        using (var upstream = new TcpClient { NoDelay = true })
        {
            downstream.NoDelay = true;
            try
            {
                await upstream.ConnectAsync(upstreamHost, upstreamPort, _stop.Token);
                recorder.Event("relay-connection-open", new { connection });
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                Task toModule = PumpAsync(connection, "req", downstream.GetStream(), upstream.GetStream(), linked.Token);
                Task fromModule = PumpAsync(connection, "resp", upstream.GetStream(), downstream.GetStream(), linked.Token);
                await Task.WhenAny(toModule, fromModule);
                await linked.CancelAsync();
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                recorder.Event("relay-connection-error", new { connection, error = exception.GetType().Name, exception.Message });
            }

            recorder.Event("relay-connection-closed", new { connection });
        }
    }

    private async Task PumpAsync(int connection, string dir, NetworkStream from, NetworkStream to, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        var pending = new List<byte>();
        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer, token);
                if (read == 0)
                {
                    return;
                }

                // Record before forwarding: otherwise the module's answer can be logged by the other
                // pump before the request that caused it, and the pairing reads it as unsolicited.
                double t = recorder.Elapsed;
                pending.AddRange(buffer.AsSpan(0, read));
                Drain(connection, dir, pending, t);
                await to.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private void Drain(int connection, string dir, List<byte> pending, double t)
    {
        while (pending.Count >= 6)
        {
            byte[] head = [.. pending.GetRange(0, 6)];
            int length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4, 2));
            if (length is < 1 or > 260)
            {
                // Not an MBAP frame we can delimit. Record the raw bytes rather than guess.
                RecordFrame(new FrameRecord(t, connection, dir, -1, -1, length, -1, -1, Convert.ToHexString([.. pending])));
                pending.Clear();
                return;
            }

            int total = 6 + length;
            if (pending.Count < total)
            {
                return;
            }

            byte[] frame = [.. pending.GetRange(0, total)];
            pending.RemoveRange(0, total);
            RecordFrame(new FrameRecord(
                t,
                connection,
                dir,
                BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(0, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2, 2)),
                length,
                frame[6],
                frame.Length > 7 ? frame[7] : -1,
                Convert.ToHexString(frame)));
        }
    }

    private void RecordFrame(FrameRecord frame)
    {
        lock (_frames)
        {
            _frames.Add(frame);
        }

        recorder.Frame(frame);
        CheckRule(frame);
    }

    // Checked live, frame by frame, so a run stops at the first response that is not
    // (request id & 0xFF) even when --keep-going-after-disconnect lets it ride over disconnects.
    private void CheckRule(FrameRecord frame)
    {
        int? requestId = null;
        lock (_outstanding)
        {
            if (!_outstanding.TryGetValue(frame.Connection, out Queue<int>? queue))
            {
                queue = new Queue<int>();
                _outstanding[frame.Connection] = queue;
            }

            if (frame.Dir == "req")
            {
                queue.Enqueue(frame.TransactionId);
                return;
            }

            if (queue.TryDequeue(out int id))
            {
                requestId = id;
            }
        }

        if (requestId is null || frame.TransactionId != (requestId.Value & 0xFF))
        {
            recorder.Event("rule-violation", new
            {
                connection = frame.Connection,
                requestId,
                responseId = frame.TransactionId,
                frameT = frame.T,
                hex = frame.Hex,
            });
            onRuleViolation();
        }
    }
}

internal sealed record Pairing(
    int Requests,
    int Responses,
    int Paired,
    int EchoEqualsRequest,
    int EchoEqualsLowByteOnly,
    int EchoOther,
    int NotLowByteRule,
    int DistinctRequestIds,
    int MinRequestId,
    int MaxRequestId,
    int Wraps,
    IReadOnlyList<string> FirstMismatches,
    IReadOnlyList<string> SampleAroundHighIds);

internal sealed record IntervalStats(int Count, double MeanMs, double MedianMs, double P05Ms, double P95Ms, double MaxMs);

internal sealed record Summary(
    string Mode,
    int TransitionCount,
    IReadOnlyList<string> Transitions,
    bool ConnectedAtEnd,
    int ConnectedSnapshots,
    int TransactionCounter,
    IntervalStats? PollInterval,
    double? RequestsPerSecond,
    Pairing? Pairing,
    int RelayConnections,
    bool Pass,
    IReadOnlyList<string> Verdicts)
{
    public static Summary Build(
        Options options,
        List<(double T, bool Connected)> transitions,
        List<double> connectedSnapshotTimes,
        int counter,
        bool connectedAtEnd,
        Relay? relay)
    {
        var verdicts = new List<string>();
        bool pass = true;

        List<string> transitionText = transitions.Select(x => $"{x.T:F0}ms {(x.Connected ? "connected" : "disconnected")}").ToList();
        bool oneConnect = transitions.Count == 1 && transitions[0].Connected && connectedAtEnd;
        verdicts.Add(oneConnect
            ? "connection: exactly one transition (connected) and still connected at the end"
            : $"connection: {transitions.Count} transitions, connectedAtEnd={connectedAtEnd} -- NOT the single connect expected");
        pass &= oneConnect;

        IntervalStats? poll = null;
        double? rate = null;
        List<double> times;
        lock (connectedSnapshotTimes)
        {
            times = [.. connectedSnapshotTimes];
        }

        if (times.Count > 1)
        {
            poll = Stats(times.Zip(times.Skip(1), (a, b) => b - a).ToList());
            rate = 2 * (times.Count - 1) / ((times[^1] - times[0]) / 1000.0);
        }

        Pairing? pairing = null;
        if (relay is not null)
        {
            pairing = Pair(relay.Frames);
            bool allEqual = pairing.Paired > 0 && pairing.EchoEqualsRequest == pairing.Paired && pairing.Requests == pairing.Paired;
            verdicts.Add($"pairing: {pairing.EchoEqualsRequest}/{pairing.Paired} responses echo the request id exactly, " +
                $"{pairing.EchoEqualsLowByteOnly} echo only its low byte, {pairing.EchoOther} echo something else; " +
                $"{pairing.Requests - pairing.Paired} requests unanswered");
            // Judged per pair against (id & 0xFF) directly. An exact echo of an id above 255 is NOT
            // the low-byte rule, even though the pairing tally files it under "equal".
            bool lowByteRule = pairing.NotLowByteRule == 0 && pairing.Paired > 0;
            verdicts.Add(lowByteRule
                ? "rule: every response id equals (request id & 0xFF)"
                : "rule: at least one response id is NOT (request id & 0xFF) -- STOP and report");
            verdicts.Add(pairing is { DistinctRequestIds: 255, MinRequestId: 1, MaxRequestId: 255 } && pairing.Wraps >= 1
                ? $"range: request ids cover 1..255 exactly, wrapped {pairing.Wraps} times"
                : $"range: ids {pairing.MinRequestId}..{pairing.MaxRequestId}, {pairing.DistinctRequestIds} distinct, {pairing.Wraps} wraps");
            if (options.StopOnDisconnect)
            {
                pass &= allEqual && pairing.DistinctRequestIds == 255 && pairing.MinRequestId == 1
                    && pairing.MaxRequestId == 255 && pairing.Wraps >= 1;
            }

            pass &= lowByteRule;
        }

        return new Summary(options.Mode, transitions.Count, transitionText, connectedAtEnd, times.Count, counter,
            poll, rate, pairing, relay?.Connections ?? 0, pass, verdicts);
    }

    private static Pairing Pair(IReadOnlyList<FrameRecord> frames)
    {
        int requests = 0;
        int responses = 0;
        int equal = 0;
        int lowByte = 0;
        int other = 0;
        int notLowByte = 0;
        var ids = new List<int>();
        var mismatches = new List<string>();
        var high = new List<string>();

        foreach (IGrouping<int, FrameRecord> connection in frames.GroupBy(f => f.Connection))
        {
            var queue = new Queue<FrameRecord>();
            foreach (FrameRecord frame in connection)
            {
                if (frame.Dir == "req")
                {
                    requests++;
                    ids.Add(frame.TransactionId);
                    queue.Enqueue(frame);
                    continue;
                }

                responses++;
                if (!queue.TryDequeue(out FrameRecord? request))
                {
                    other++;
                    notLowByte++;
                    mismatches.Add($"conn {connection.Key}: response {frame.TransactionId} with no outstanding request");
                    continue;
                }

                if (frame.TransactionId != (request.TransactionId & 0xFF))
                {
                    notLowByte++;
                }

                string pairText = $"conn {connection.Key}: req {request.TransactionId} (0x{request.TransactionId:X4}) -> resp {frame.TransactionId} (0x{frame.TransactionId:X4})";
                if (frame.TransactionId == request.TransactionId)
                {
                    equal++;
                }
                else if (frame.TransactionId == (request.TransactionId & 0xFF))
                {
                    lowByte++;
                    if (mismatches.Count < 40)
                    {
                        mismatches.Add(pairText + " low byte");
                    }
                }
                else
                {
                    other++;
                    if (mismatches.Count < 40)
                    {
                        mismatches.Add(pairText + " OTHER");
                    }
                }

                if (request.TransactionId >= 250 && high.Count < 40)
                {
                    high.Add(pairText);
                }
            }
        }

        int wraps = ids.Zip(ids.Skip(1), (a, b) => b < a ? 1 : 0).Sum();
        return new Pairing(requests, responses, equal + lowByte + other, equal, lowByte, other, notLowByte,
            ids.Distinct().Count(), ids.Count > 0 ? ids.Min() : -1, ids.Count > 0 ? ids.Max() : -1, wraps,
            mismatches, high);
    }

    private static IntervalStats Stats(List<double> values)
    {
        values.Sort();
        double At(double q) => values[(int)Math.Clamp(Math.Round(q * (values.Count - 1)), 0, values.Count - 1)];
        return new IntervalStats(values.Count, Math.Round(values.Average(), 2), Math.Round(At(0.5), 2),
            Math.Round(At(0.05), 2), Math.Round(At(0.95), 2), Math.Round(values[^1], 2));
    }
}

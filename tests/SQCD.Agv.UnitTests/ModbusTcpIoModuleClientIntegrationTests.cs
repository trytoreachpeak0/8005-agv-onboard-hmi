using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class ModbusTcpIoModuleClientIntegrationTests
{
    [Fact]
    public async Task ClientReadsConfiguredMappingAndWritesFc05Address()
    {
        await using FakeModbusServer server = new();
        server.Start();
        IoModuleSettings settings = new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PollIntervalMs = 20,
            RequestTimeoutMs = 500,
            ReconnectDelayMs = 50
        };
        await using ModbusTcpIoModuleClient client = new(settings, new NullLogger());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        LockerSnapshot initial = client.CurrentSnapshot.GetLocker(0);
        Assert.True(initial.IsLocked);
        Assert.False(initial.HasCargo);

        await client.PulseUnlockAsync(0, timeout.Token);

        Assert.Equal(1, server.WriteCount);
        Assert.Equal((ushort)100, server.LastWriteAddress);

        server.SetInput(0, false);
        await WaitUntilAsync(() => client.CurrentSnapshot.GetLocker(0).LockFeedbackRaw is false, timeout.Token);
        Assert.False(client.CurrentSnapshot.GetLocker(0).IsLocked);

        await client.StopAsync(timeout.Token);
    }

    /// <summary>
    /// A module that echoes only the low byte of the transaction id keeps answering past request
    /// 255, and every one of those answers is still paired with the request that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is onboard-hmi#52. The vehicles' Kangnaide C2000 module echoes one byte, so a 16-bit
    /// counter stops matching at request 256 -- measured on agv01 on 2026-09-13, at a 50 ms and at
    /// a 200 ms read interval alike. The client then closes the transport, reconnects, resumes the
    /// counter at 257 and mismatches <i>every</i> request from there on. The simulator echoes all
    /// 16 bits, which is why the field windows never saw it.
    /// </para>
    /// <para>
    /// <b>Why the assertions are what they are.</b> "600 requests and nothing threw" would also
    /// pass against a client that does not check the transaction id at all, which is a worse defect
    /// than the one being fixed. So the pairing is asserted directly: the id the module wrote back
    /// is, request by request, the id that was sent -- including past index 255, where the old
    /// counter broke. That equality holds exactly when every id fits in one byte, which is the
    /// second assertion, and it is the one that goes red if the range is ever widened back towards
    /// <c>ushort.MaxValue</c>. The distinct-count assertion keeps the run honest: it says the
    /// counter really did traverse and re-enter its whole range rather than stopping short of the
    /// wrap. The connection-transition assertion says the client accepted all of it -- one
    /// transition to connected and no drop -- so the ids were not merely in range, they were
    /// matched. <see cref="ResponseCarryingAnInRangeButDifferentTransactionIdIsRejected"/> supplies
    /// the other half: that matching still rejects.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LowByteEchoingModuleKeepsPairingResponsesPastRequest255()
    {
        // 600 requests is the field measurement: 300 consecutive reads of two requests each, the
        // run that came back with zero errors once the ids were confined to one byte.
        const int requestsToRun = 600;
        const int firstRequestThatUsedToBreak = 255;

        await using FakeModbusServer server = new(EchoLowByteOnly);
        server.Start();
        IoModuleSettings settings = new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PollIntervalMs = 0,
            RequestTimeoutMs = 2_000,
            ReconnectDelayMs = 20
        };

        List<bool> transitions = [];
        await using ModbusTcpIoModuleClient client = new(settings, new NullLogger());
        client.ConnectionChanged += (_, args) =>
        {
            lock (transitions)
            {
                transitions.Add(args.Value);
            }
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => server.RequestCount >= requestsToRun, timeout.Token);

        bool connectedAtTheEnd = client.IsConnected;
        bool[] transitionsWhileRunning;
        lock (transitions)
        {
            transitionsWhileRunning = [.. transitions];
        }

        await client.StopAsync(timeout.Token);
        (ushort[] requests, ushort[] responses) = server.TransactionIdLog();

        Assert.True(
            requests.Length >= requestsToRun,
            $"The run has to cross the wrap point to prove anything; it issued {requests.Length} "
            + $"requests, fewer than the {requestsToRun} asked for.");

        ushort[] outOfRange = [.. requests.Where(id => id is < 1 or > 255)];
        Assert.True(
            outOfRange.Length == 0,
            "These transaction ids do not fit in one byte, so a module that echoes one byte cannot "
            + "return them unchanged and every one of these requests is lost: "
            + string.Join(", ", outOfRange.Distinct().Order()));

        Assert.Equal(255, requests.Distinct().Count());

        Assert.Equal(requests, responses);
        Assert.True(
            requests[firstRequestThatUsedToBreak..]
                .SequenceEqual(responses[firstRequestThatUsedToBreak..]),
            "A response past request 255 came back carrying an identifier other than the one its "
            + "own request was sent with. That is exactly the failure this ticket is about, and "
            + "the client cannot tell such a response from a stale one.");

        Assert.Equal<bool[]>([true], transitionsWhileRunning);
        Assert.True(connectedAtTheEnd, "The client dropped the IO module during the run.");
    }

    /// <summary>
    /// A response carrying an unrelated transaction id is still rejected.
    /// </summary>
    /// <remarks>
    /// Without this, the test above could be satisfied by deleting the check outright. The value
    /// chosen is far outside the request range, so it stands for a frame from another conversation
    /// entirely.
    /// </remarks>
    [Fact]
    public async Task ResponseCarryingAnUnrelatedTransactionIdIsRejected()
    {
        IOException failure = await ExpectPulseUnlockToFailAsync(_ => 0x1234);

        Assert.Contains("响应头无效", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A response carrying a transaction id that is inside the legal range, but is not this
    /// request's, is still rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sharper half of the negative case, and the reason it is a separate test. Weaken the
    /// comparison to "the id is in range" -- an easy thing to reach for once the range is a real
    /// constraint -- and
    /// <see cref="LowByteEchoingModuleKeepsPairingResponsesPastRequest255"/> stays green, because
    /// every id it sees is in range, and
    /// <see cref="ResponseCarryingAnUnrelatedTransactionIdIsRejected"/> stays green too, because
    /// 0x1234 fails a range check on its own. This one does not: the echoed id is always 1..255 and
    /// always a value this very connection hands out, just never for this request. Verified by
    /// injection -- with the check replaced by <c>responseTransactionId is &lt; 1 or &gt; 255</c>,
    /// this is the only test of the four that goes red.
    /// </para>
    /// <para>
    /// Note what this does <b>not</b> guard, so nobody reads more into it than is there. Comparing
    /// only the low bytes is <i>not</i> a defect under a 1..255 range -- every id fits in a byte, so
    /// that comparison is equivalent to the full one, and all four tests stay green under it, as
    /// they should. The equality earns its keep against a range check, not against a byte
    /// comparison.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ResponseCarryingAnInRangeButDifferentTransactionIdIsRejected()
    {
        IOException failure = await ExpectPulseUnlockToFailAsync(
            id => (ushort)((id % 255) + 1));

        Assert.Contains("响应头无效", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The vehicles' Kangnaide C2000 module: only the low byte of the transaction id comes back,
    /// so request 256 answers as transaction 0.
    /// </summary>
    private static ushort EchoLowByteOnly(ushort requestTransactionId) =>
        (ushort)(requestTransactionId & 0xFF);

    /// <summary>
    /// Drives one FC05 request against a module echoing <paramref name="echoTransactionId"/> and
    /// returns the failure it produced.
    /// </summary>
    /// <remarks>
    /// <see cref="ModbusTcpIoModuleClient.PulseUnlockAsync"/> rather than the poll loop, because it
    /// hands the exception straight back instead of swallowing it into a reconnect -- so the
    /// assertion can be on which failure occurred, not merely that the client went offline.
    /// </remarks>
    private static async Task<IOException> ExpectPulseUnlockToFailAsync(
        Func<ushort, ushort> echoTransactionId)
    {
        await using FakeModbusServer server = new(echoTransactionId);
        server.Start();
        IoModuleSettings settings = new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PollIntervalMs = 20,
            RequestTimeoutMs = 2_000,
            ReconnectDelayMs = 50
        };
        await using ModbusTcpIoModuleClient client = new(settings, new NullLogger());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        return await Assert.ThrowsAsync<IOException>(
            () => client.PulseUnlockAsync(0, timeout.Token));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>
    /// A Modbus TCP server that answers on loopback, with the transaction id it echoes back under
    /// the test's control.
    /// </summary>
    /// <remarks>
    /// The default echoes all 16 bits, which is what the slots simulator does and what every
    /// rehearsal therefore saw. <see cref="EchoLowByteOnly"/> is the vehicles' real module.
    /// </remarks>
    private sealed class FakeModbusServer(Func<ushort, ushort>? echoTransactionId = null)
        : IAsyncDisposable
    {
        private readonly object _stateGate = new();
        private readonly bool[] _outputs = new bool[16];
        private readonly bool[] _inputs = Enumerable.Repeat(true, 16).ToArray();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Func<ushort, ushort> _echoTransactionId = echoTransactionId ?? (id => id);
        private readonly List<ushort> _requestTransactionIds = [];
        private readonly List<ushort> _responseTransactionIds = [];
        private Task? _serverTask;
        private int _writeCount;
        private int _lastWriteAddress;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int WriteCount => Volatile.Read(ref _writeCount);

        public ushort LastWriteAddress => (ushort)Volatile.Read(ref _lastWriteAddress);

        /// <summary>
        /// The transaction id of every request received and of the response actually written back
        /// for it, in arrival order, read as one pair so the two can never be a request apart.
        /// </summary>
        public (ushort[] Requests, ushort[] Responses) TransactionIdLog()
        {
            lock (_stateGate)
            {
                return ([.. _requestTransactionIds], [.. _responseTransactionIds]);
            }
        }

        public int RequestCount
        {
            get
            {
                lock (_stateGate)
                {
                    return _requestTransactionIds.Count;
                }
            }
        }

        public void Start()
        {
            _listener.Start();
            _serverTask = RunAsync(_shutdown.Token);
        }

        public void SetInput(int channel, bool value)
        {
            lock (_stateGate)
            {
                _inputs[channel] = value;
            }
        }

        /// <summary>
        /// Accepts connections in a loop, so a client that drops the transport after a rejected
        /// response can reconnect and be served again.
        /// </summary>
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    try
                    {
                        await ServeAsync(client.GetStream(), cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or SocketException)
                    {
                    }
                }
            }
            catch (Exception exception)
                when (exception is OperationCanceledException or IOException or SocketException
                    or ObjectDisposedException)
            {
            }
        }

        private async Task ServeAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] header = new byte[7];
                await stream.ReadExactlyAsync(header, cancellationToken);
                ushort requestTransactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                byte[] requestPdu = new byte[length - 1];
                await stream.ReadExactlyAsync(requestPdu, cancellationToken);
                byte[] responsePdu = CreateResponse(requestPdu);

                ushort responseTransactionId = _echoTransactionId(requestTransactionId);
                lock (_stateGate)
                {
                    _requestTransactionIds.Add(requestTransactionId);
                    _responseTransactionIds.Add(responseTransactionId);
                }

                byte[] response = new byte[7 + responsePdu.Length];
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), responseTransactionId);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)(responsePdu.Length + 1));
                response[6] = header[6];
                responsePdu.CopyTo(response, 7);
                await stream.WriteAsync(response, cancellationToken);
            }
        }

        private byte[] CreateResponse(byte[] request)
        {
            byte function = request[0];
            if (function is 0x01 or 0x02)
            {
                ushort startAddress = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
                ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
                bool[] source = function == 0x01 ? _outputs : _inputs;
                ushort baseAddress = function == 0x01 ? (ushort)100 : (ushort)200;
                byte[] response = new byte[2 + ((count + 7) / 8)];
                response[0] = function;
                response[1] = (byte)(response.Length - 2);
                lock (_stateGate)
                {
                    for (int index = 0; index < count; index++)
                    {
                        int channel = startAddress - baseAddress + index;
                        if (source[channel])
                        {
                            response[2 + (index / 8)] |= (byte)(1 << (index % 8));
                        }
                    }
                }

                return response;
            }

            if (function == 0x05)
            {
                ushort address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
                Interlocked.Increment(ref _writeCount);
                Interlocked.Exchange(ref _lastWriteAddress, address);
                return request.ToArray();
            }

            return [unchecked((byte)(function | 0x80)), 0x01];
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            if (_serverTask is not null)
            {
                await _serverTask;
            }

            _shutdown.Dispose();
        }
    }

    private sealed class NullLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            EntryWritten?.Invoke(this, new LogEntryEventArgs(new LogEntry(DateTimeOffset.Now, severity, source, message)));
        }
    }
}

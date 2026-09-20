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
    /// than the one being fixed. So the pairing is asserted directly, over the tail that starts at
    /// the index where the old counter broke: the id the module wrote back is, request by request,
    /// the id that was sent.
    /// </para>
    /// <para>
    /// <b>Be clear about which of these carry independent weight, because two of them do not.</b>
    /// Against this module -- <see cref="EchoLowByteOnly"/> -- "every id fits in one byte" and
    /// "every response pairs with its request" are the same statement: <c>id &amp; 0xFF == id</c>
    /// holds exactly for ids in 1..255. So the pairing assertion and the range assertion cannot
    /// fail independently, and pairing is written first only because its failure message names the
    /// defect; the range assertion that follows is there to say <i>why</i> pairing held, and lists
    /// the offending ids when it does not. An earlier revision also asserted pairing over the
    /// whole log and then over the tail, which added a line that could never be the first to go
    /// red; that one is gone.
    /// </para>
    /// <para>
    /// The rest do carry weight of their own. The distinct-count assertion says the counter really
    /// traversed and re-entered its whole range rather than stopping short of the wrap. The
    /// connection assertions say the client accepted all of it -- one transition to connected, no
    /// drop, still connected at the end -- so the ids were not merely in range, they were matched.
    /// <see cref="CounterKeepsRunningAcrossAReconnectSoIdsStayPairedAfterwards"/> covers what this
    /// test structurally cannot: it never reconnects, so nothing here would notice a counter that
    /// restarts with each transport.
    /// <see cref="ResponseCarryingAnInRangeButDifferentTransactionIdIsRejected"/> supplies the
    /// other half: that matching still rejects.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LowByteEchoingModuleKeepsPairingResponsesPastRequest255()
    {
        // 600 requests is the field measurement: 300 consecutive reads of two requests each, the
        // run that came back with zero errors once the ids were confined to one byte.
        const int requestsToRun = 600;
        const int firstIndexThatUsedToBreak = 255;

        await using FakeModbusServer server = new(EchoLowByteOnly);
        server.Start();
        IoModuleSettings settings = new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PollIntervalMs = 1,
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

        Assert.True(
            requests[firstIndexThatUsedToBreak..]
                .SequenceEqual(responses[firstIndexThatUsedToBreak..]),
            "A response past request 255 came back carrying an identifier other than the one its "
            + "own request was sent with. That is exactly the failure this ticket is about, and "
            + "the client cannot tell such a response from a stale one.");

        // Why the ids paired: 1..255 is the range in which a one-byte echo is the identity. This
        // does not fail independently of the assertion above -- it explains it, and names the ids.
        // The bounds are spelled out rather than read from MinTransactionId/MaxTransactionId on
        // purpose: sourcing them from the constants would keep this green through the very edit it
        // exists to catch.
        ushort[] outOfRange = [.. requests.Where(id => id is < 1 or > 255)];
        Assert.True(
            outOfRange.Length == 0,
            "These transaction ids fall outside the 1..255 the client is meant to issue: "
            + string.Join(", ", outOfRange.Distinct().Order())
            + ". Anything above 255 cannot survive a one-byte echo, so every request carrying one "
            + "is lost; 0 survives it perfectly well and is excluded for the unrelated reason "
            + "given on MinTransactionId.");

        Assert.Equal(255, requests.Distinct().Count());

        Assert.Equal<bool[]>([true], transitionsWhileRunning);
        Assert.True(connectedAtTheEnd, "The client dropped the IO module during the run.");
    }

    /// <summary>
    /// The transaction counter keeps running across a reconnect, so the ids issued after one are
    /// still inside the range and still pair with their responses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// onboard-hmi#52 has two halves, and until this test only one of them had a guard. The first
    /// is that request 256 mismatches. The second is what follows from it: the client closes the
    /// transport, reconnects, resumes the counter at 257, and from there mismatches <i>every</i>
    /// request, so the module never comes back. That second half is why the field symptom was "IO
    /// goes dead about 13 seconds after connecting" and not "one poll is lost".
    /// </para>
    /// <para>
    /// <b>What only this test can see.</b> No other test in this class drops a connection --
    /// <see cref="LowByteEchoingModuleKeepsPairingResponsesPastRequest255"/> runs one transport end
    /// to end, and the two rejection tests stop at the first throw. So none of them would notice a
    /// counter that restarts per transport: write <c>_transactionId = 0</c> into
    /// <c>EnsureConnectedAsync</c>, or move the counter into an object rebuilt with the connection,
    /// and they all stay green. The comment on <c>MaxTransactionId</c> asserts in prose that the
    /// counter survives a reconnect, and leans on it; the continuity assertion below is where that
    /// claim makes a noise when someone takes it away. Verified by injection -- with
    /// <c>_transactionId = 0</c> added to <c>EnsureConnectedAsync</c>, this is the only one of the
    /// five tests in this class that goes red, and it goes red on that assertion
    /// (<c>carried 2, not 47, the successor of 46</c>).
    /// </para>
    /// <para>
    /// The drop itself has to survive the edits the other tests deliberately tolerate, or this one
    /// would stop reconnecting under them and report that as its own failure. See the comment on
    /// <c>ForeignIdFor</c> for how the injected response is made wrong under a range check, a full
    /// comparison and a low-byte comparison alike.
    /// </para>
    /// <para>
    /// This is also the only test that drives <see cref="FakeModbusServer"/>'s accept loop round a
    /// second time.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CounterKeepsRunningAcrossAReconnectSoIdsStayPairedAfterwards()
    {
        const int requestsToRun = 600;

        // 1-based. The request the module answers with a foreign id, which forces the client to
        // drop the transport. Chosen past the first wrap of the 1..255 counter, so the reconnect
        // happens to a counter that has already cycled rather than to a fresh one.
        const int requestAnsweredWithAForeignId = 300;
        const int lastIndexBeforeTheDrop = requestAnsweredWithAForeignId - 1;
        const int firstIndexAfterTheDrop = requestAnsweredWithAForeignId;

        // The id that request is answered with. Both halves of it are derived rather than fixed,
        // so that it is wrong in every way a client might check, whichever id the request happened
        // to carry: above 255, so a range check rejects it; unequal, so a full comparison rejects
        // it; and with a low byte one past the request's own, so a comparison of low bytes rejects
        // it too. A fixed constant would only be wrong in those ways for the id this particular
        // request index happens to produce, and would stop being so if the index ever moved --
        // leaving the drop this test is built on not to happen at all.
        static ushort ForeignIdFor(ushort requestTransactionId) =>
            (ushort)(0x1200 | ((requestTransactionId + 1) & 0xFF));

        int served = 0;
        ushort EchoLowByteExceptOnce(ushort requestTransactionId) =>
            Interlocked.Increment(ref served) == requestAnsweredWithAForeignId
                ? ForeignIdFor(requestTransactionId)
                : (ushort)(requestTransactionId & 0xFF);

        await using FakeModbusServer server = new(EchoLowByteExceptOnce);
        server.Start();
        IoModuleSettings settings = new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PollIntervalMs = 1,
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
            "The run has to continue well past the drop to prove anything; it issued "
            + $"{requests.Length} requests, fewer than the {requestsToRun} asked for.");

        // The injection itself, asserted before anything is concluded from it. Without this an
        // off-by-one in the counting above would leave the connection up for the whole run, and
        // every assertion below would pass without a reconnect ever having happened.
        Assert.Equal(
            ForeignIdFor(requests[lastIndexBeforeTheDrop]),
            responses[lastIndexBeforeTheDrop]);
        Assert.Equal<bool[]>([true, false, true], transitionsWhileRunning);
        Assert.True(connectedAtTheEnd, "The client never got the IO module back after the drop.");

        // The point of the test. The counter is free-running rather than per-connection, so the
        // first id after the reconnect is the successor of the last one sent before it. Spelled out
        // rather than read from the client's constants, for the same reason as in the test above.
        ushort lastIdBeforeTheDrop = requests[lastIndexBeforeTheDrop];
        ushort expectedFirstIdAfterTheDrop =
            lastIdBeforeTheDrop == 255 ? (ushort)1 : (ushort)(lastIdBeforeTheDrop + 1);
        Assert.True(
            requests[firstIndexAfterTheDrop] == expectedFirstIdAfterTheDrop,
            $"The first request after the reconnect carried {requests[firstIndexAfterTheDrop]}, "
            + $"not {expectedFirstIdAfterTheDrop}, the successor of {lastIdBeforeTheDrop} -- the "
            + "last id sent on the transport that was dropped. The counter was restarted or "
            + "rebuilt along with the connection. While the range is 1..255 that is not itself a "
            + "defect, but it is the property the comment on MaxTransactionId states and relies "
            + "on, and nothing else in this suite would have noticed it going away.");

        Assert.True(
            requests[firstIndexAfterTheDrop..].SequenceEqual(responses[firstIndexAfterTheDrop..]),
            "After the reconnect a response came back carrying an identifier other than the one "
            + "its own request was sent with. That is the second half of #52, the half where the "
            + "client never recovers the module at all.");

        // As in the test above: this explains the pairing rather than failing independently of it.
        ushort[] outOfRangeAfterTheDrop =
            [.. requests[firstIndexAfterTheDrop..].Where(id => id is < 1 or > 255)];
        Assert.True(
            outOfRangeAfterTheDrop.Length == 0,
            "These transaction ids were issued after the reconnect and fall outside the 1..255 the "
            + "client is meant to issue: "
            + string.Join(", ", outOfRangeAfterTheDrop.Distinct().Order()));
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
    /// this is the only one of the five tests in this class that goes red.
    /// </para>
    /// <para>
    /// Note what this does <b>not</b> guard, so nobody reads more into it than is there. Comparing
    /// only the low bytes is <i>not</i> a defect under a 1..255 range -- every id fits in a byte, so
    /// that comparison is equivalent to the full one, and all five tests stay green under it, as
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

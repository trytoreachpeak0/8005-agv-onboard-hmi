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
    /// ADR-cross-0035 BatchUnlock on one module: a set of target slots whose DO channels are contiguous is
    /// unlocked with a single FC0F write of ones, and no FC05 is sent.
    /// </summary>
    [Fact]
    public async Task ABatchUnlockOfContiguousChannelsIsOneMultipleCoilWrite()
    {
        await using FakeModbusServer server = new();
        server.Start();
        await using ModbusTcpIoModuleClient client = new(CreateSettings(server.Port), new NullLogger());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        await client.PulseUnlockBatchAsync([3, 1, 2], timeout.Token);

        CoilWrite write = Assert.Single(server.CoilWrites);
        Assert.Equal(0x0F, write.Function);
        Assert.Equal((ushort)101, write.StartAddress);
        Assert.Equal([true, true, true], write.Values);
        Assert.Equal(0, server.WriteCount);

        await client.StopAsync(timeout.Token);
    }

    /// <summary>
    /// A channel between two targets is never written, not even with a zero: whether writing 0 to an idle
    /// unlock output is inert depends on the module (the simulator's FollowOutput model reacts to it), so
    /// a gap splits the set into one FC0F write per contiguous run of target channels.
    /// </summary>
    [Fact]
    public async Task ABatchUnlockSplitsAtAChannelThatIsNotATarget()
    {
        await using FakeModbusServer server = new();
        server.Start();
        await using ModbusTcpIoModuleClient client = new(CreateSettings(server.Port), new NullLogger());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);
        server.SetOutput(2, true);

        await client.PulseUnlockBatchAsync([0, 1, 3, 5, 6], timeout.Token);

        Assert.Collection(
            server.CoilWrites,
            write => AssertCoilWrite(write, 100, 2),
            write => AssertCoilWrite(write, 103, 1),
            write => AssertCoilWrite(write, 105, 2));
        Assert.DoesNotContain(
            server.CoilWrites,
            write => write.StartAddress <= 102 && 102 < write.StartAddress + write.Values.Length);
        Assert.True(server.GetOutput(2));

        await client.StopAsync(timeout.Token);
    }

    private static void AssertCoilWrite(CoilWrite write, ushort startAddress, int count)
    {
        Assert.Equal(0x0F, write.Function);
        Assert.Equal(startAddress, write.StartAddress);
        Assert.Equal(Enumerable.Repeat(true, count), write.Values);
    }

    private static IoModuleSettings CreateSettings(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        PollIntervalMs = 20,
        RequestTimeoutMs = 500,
        ReconnectDelayMs = 50
    };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class FakeModbusServer : IAsyncDisposable
    {
        private readonly object _stateGate = new();
        private readonly bool[] _outputs = new bool[16];
        private readonly bool[] _inputs = Enumerable.Repeat(true, 16).ToArray();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private Task? _serverTask;
        private int _writeCount;
        private int _lastWriteAddress;
        private readonly List<CoilWrite> _coilWrites = [];

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int WriteCount => Volatile.Read(ref _writeCount);

        public ushort LastWriteAddress => (ushort)Volatile.Read(ref _lastWriteAddress);

        public void Start()
        {
            _listener.Start();
            _serverTask = RunAsync(_shutdown.Token);
        }

        public IReadOnlyList<CoilWrite> CoilWrites
        {
            get
            {
                lock (_stateGate)
                {
                    return _coilWrites.ToArray();
                }
            }
        }

        public void SetOutput(int channel, bool value)
        {
            lock (_stateGate)
            {
                _outputs[channel] = value;
            }
        }

        public bool GetOutput(int channel)
        {
            lock (_stateGate)
            {
                return _outputs[channel];
            }
        }

        public void SetInput(int channel, bool value)
        {
            lock (_stateGate)
            {
                _inputs[channel] = value;
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                NetworkStream stream = client.GetStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] header = new byte[7];
                    await stream.ReadExactlyAsync(header, cancellationToken);
                    ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                    byte[] requestPdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(requestPdu, cancellationToken);
                    byte[] responsePdu = CreateResponse(requestPdu);

                    byte[] response = new byte[7 + responsePdu.Length];
                    header.AsSpan(0, 4).CopyTo(response);
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), (ushort)(responsePdu.Length + 1));
                    response[6] = header[6];
                    responsePdu.CopyTo(response, 7);
                    await stream.WriteAsync(response, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
            {
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

            if (function == 0x0F)
            {
                ushort startAddress = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1, 2));
                ushort quantity = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3, 2));
                bool[] values = Enumerable.Range(0, quantity)
                    .Select(index => (request[6 + (index / 8)] & (1 << (index % 8))) != 0)
                    .ToArray();
                lock (_stateGate)
                {
                    _coilWrites.Add(new CoilWrite(function, startAddress, values));
                    for (int index = 0; index < quantity; index++)
                    {
                        _outputs[startAddress - 100 + index] = values[index];
                    }
                }

                return request.AsSpan(0, 5).ToArray();
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

    private sealed record CoilWrite(byte Function, ushort StartAddress, bool[] Values);

    private sealed class NullLogger : IAppLogger
    {
        public event EventHandler<LogEntryEventArgs>? EntryWritten;

        public void Write(LogSeverity severity, string source, string message, Exception? exception = null)
        {
            EntryWritten?.Invoke(this, new LogEntryEventArgs(new LogEntry(DateTimeOffset.Now, severity, source, message)));
        }
    }
}

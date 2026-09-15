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

    // 现场三台车上的康耐德 C2000 仓位模块只回写 MBAP 事务号的低 8 位：第 256 条请求的应答事务号是 0（onboard-hmi#52）。
    // 客户端若按 16 位递增并严格比对，约 255 条请求后就断开，而且重连后每一条都对不上。
    [Fact]
    public async Task ClientKeepsReadingPastTransaction255WhenModuleEchoesOnlyLowByte()
    {
        await using FakeModbusServer server = new(echoOnlyLowTransactionByte: true);
        server.Start();
        IoModuleSettings settings = new()
        {
            Host = "127.0.0.1",
            Port = server.Port,
            PollIntervalMs = 1,
            RequestTimeoutMs = 500,
            ReconnectDelayMs = 50
        };
        await using ModbusTcpIoModuleClient client = new(settings, new NullLogger());
        int disconnects = 0;
        client.ConnectionChanged += (_, args) =>
        {
            if (!args.Value)
            {
                Interlocked.Increment(ref disconnects);
            }
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await client.StartAsync(timeout.Token);
        await WaitUntilAsync(() => client.IsConnected, timeout.Token);

        await WaitUntilAsync(() => server.RequestCount >= 600 || Volatile.Read(ref disconnects) > 0, timeout.Token);

        Assert.Equal(0, Volatile.Read(ref disconnects));
        Assert.True(client.IsConnected);
        Assert.Equal(1, server.AcceptedConnections);

        await client.StopAsync(timeout.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class FakeModbusServer(bool echoOnlyLowTransactionByte = false) : IAsyncDisposable
    {
        private readonly object _stateGate = new();
        private readonly bool[] _outputs = new bool[16];
        private readonly bool[] _inputs = Enumerable.Repeat(true, 16).ToArray();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private Task? _serverTask;
        private int _writeCount;
        private int _lastWriteAddress;
        private int _requestCount;
        private int _acceptedConnections;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);

        public int WriteCount => Volatile.Read(ref _writeCount);

        public ushort LastWriteAddress => (ushort)Volatile.Read(ref _lastWriteAddress);

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

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                Interlocked.Increment(ref _acceptedConnections);
                NetworkStream stream = client.GetStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] header = new byte[7];
                    await stream.ReadExactlyAsync(header, cancellationToken);
                    ushort length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                    byte[] requestPdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(requestPdu, cancellationToken);
                    byte[] responsePdu = CreateResponse(requestPdu);
                    Interlocked.Increment(ref _requestCount);

                    byte[] response = new byte[7 + responsePdu.Length];
                    header.AsSpan(0, 4).CopyTo(response);
                    if (echoOnlyLowTransactionByte)
                    {
                        // 高字节清零，只留低 8 位，与现场 C2000 模块的应答一致。
                        response[0] = 0;
                    }
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

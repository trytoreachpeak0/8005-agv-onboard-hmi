using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

public sealed record WireToGateSessionOptions(
    string Host,
    int Port,
    string AgvId,
    string OnboardInstanceId,
    string CredentialEnvironmentVariable,
    bool UseTls,
    string? ServerCertificateSha256,
    TimeSpan ConnectTimeout);

public sealed record WireToGateHandshakeResult(
    long SessionGeneration,
    VehicleBusinessReadiness Readiness,
    string ReasonCode,
    IReadOnlyList<SlotIoState> SlotStates);

public sealed class WireToGateSessionClient(
    WireToGateSessionOptions options,
    ISlotIoProvider ioProvider,
    IOnboardExecutionJournal journal) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] UnsafeReasonCodes = ["ONBOARD_IO_NOT_SAFE"];
    private TcpClient? _client;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public async Task<WireToGateHandshakeResult> ConnectAndRecoverAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        string credential = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"Credential environment variable '{options.CredentialEnvironmentVariable}' is not set.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        await DisposeConnectionAsync().ConfigureAwait(false);
        _client = new TcpClient();
        await _client.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
        _stream = await CreateStreamAsync(_client, timeout.Token).ConfigureAwait(false);
        _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        StreamReader reader = _reader;
        StreamWriter writer = _writer;

        WireToGateSessionPlanner planner = new(options.OnboardInstanceId, credential);
        ReadOnlyMemory<byte> hello = planner.CreateSessionHello(options.AgvId, 0);
        await writer.WriteAsync(Encoding.UTF8.GetString(hello.Span).AsMemory(), timeout.Token).ConfigureAwait(false);
        using JsonDocument accepted = JsonDocument.Parse(await ReadLineAsync(reader, timeout.Token).ConfigureAwait(false));
        RequireType(accepted.RootElement, "SessionAccepted");
        long generation = accepted.RootElement.GetProperty("sessionGeneration").GetInt64();

        IReadOnlyList<SlotIoState> states = await ioProvider.ReadAllAsync(timeout.Token).ConfigureAwait(false);
        object[] slots = states.OrderBy(item => item.PhysicalSlotNumber).Select(ToProtocolSlot).ToArray();
        await ExchangeAsync(writer, reader, "CapabilitySnapshot", generation, new
        {
            capabilityVersion = 1,
            observedAt = DateTimeOffset.UtcNow,
            slotModelVersion = "eight-slot-v1",
            activeSlotConfigurationVersion = "eight-slot-http-v1",
            slotStates = slots,
            supportsBatchUnlock = true,
            onboardJournalFormatVersion = 1
        }, "SnapshotAppliedAck", timeout.Token).ConfigureAwait(false);

        bool safe = states.Count == 8 && states.All(item =>
            item.Online && item.Occupancy != SlotOccupancy.Unknown &&
            item.DoorLock == SlotDoorLock.Locked && item.UnlockOutput == UnlockOutputState.Reset);
        await ExchangeAsync(writer, reader, "SafetyStateSnapshot", generation, new
        {
            safetyStateVersion = 1,
            observedAt = DateTimeOffset.UtcNow,
            safety = new
            {
                departureSafe = safe,
                vehicleStopped = true,
                allTargetSlotsLocked = states.All(item => item.DoorLock == SlotDoorLock.Locked),
                allUnlockOutputsReset = states.All(item => item.UnlockOutput == UnlockOutputState.Reset),
                unknownPresent = states.Any(item => !item.Online || item.Occupancy == SlotOccupancy.Unknown ||
                    item.DoorLock == SlotDoorLock.Unknown || item.UnlockOutput == UnlockOutputState.Unknown),
                reasonCodes = safe ? Array.Empty<string>() : UnsafeReasonCodes
            },
            slotStates = slots
        }, "SnapshotAppliedAck", timeout.Token).ConfigureAwait(false);

        IReadOnlyList<JournalAttempt> unsettled = await journal.ReadUnsettledAsync(timeout.Token).ConfigureAwait(false);
        string recoveryMessageId = Guid.NewGuid().ToString("D");
        await SendAsync(writer, "RecoveryStateReport", recoveryMessageId, generation, new
        {
            reportId = Guid.NewGuid().ToString("D"),
            observedAt = DateTimeOffset.UtcNow,
            unsettledSlotOperationAttemptId = unsettled.Count == 0 ? null : unsettled[0].SlotOperationAttemptId,
            provenRecoveryCheckpoint = WireToGateProtocol.SelectRecoveryCheckpoint(unsettled),
            activeUnlockSlots = states.Where(item => item.UnlockOutput == UnlockOutputState.Active)
                .Select(item => item.PhysicalSlotNumber).Order().ToArray(),
            forcedRecoveryGeneration = unsettled.Select(item => item.ForcedRecoveryGeneration).DefaultIfEmpty(0).Max(),
            pendingResults = unsettled.Where(item => item.Status == JournalAttemptStatus.ResultPendingAck)
                .Select(WireToGateProtocol.ToPendingResultReference)
                .ToArray(),
            journalContentSha256 = ComputeJournalHash(unsettled)
        }, timeout.Token).ConfigureAwait(false);
        using JsonDocument durableAck = JsonDocument.Parse(await ReadLineAsync(reader, timeout.Token).ConfigureAwait(false));
        RequireType(durableAck.RootElement, "DurableAck");
        using JsonDocument readiness = JsonDocument.Parse(await ReadLineAsync(reader, timeout.Token).ConfigureAwait(false));
        RequireType(readiness.RootElement, "SessionReadiness");
        JsonElement payload = readiness.RootElement.GetProperty("payload");
        string readinessValue = payload.GetProperty("readiness").GetString() ?? "RECOVERY_REQUIRED";
        string reason = payload.TryGetProperty("reasonCodes", out JsonElement reasonCodes) &&
                        reasonCodes.ValueKind == JsonValueKind.Array && reasonCodes.GetArrayLength() > 0
            ? reasonCodes[0].GetString() ?? "UNKNOWN"
            : readinessValue == "READY" ? "READY" : "UNKNOWN";
        return new WireToGateHandshakeResult(
            generation,
            readinessValue == "READY" ? VehicleBusinessReadiness.Ready : VehicleBusinessReadiness.RecoveryRequired,
            reason,
            states);
    }

    public async Task SendHeartbeatAsync(long sessionGeneration, CancellationToken cancellationToken)
    {
        StreamWriter writer = _writer ?? throw new InvalidOperationException("WIRE_TO_GATE session is not connected.");
        StreamReader reader = _reader ?? throw new InvalidOperationException("WIRE_TO_GATE session is not connected.");
        await SendAsync(writer, "Heartbeat", Guid.NewGuid().ToString("D"), sessionGeneration, new
        {
            observedAt = DateTimeOffset.UtcNow,
            onboardStatus = "RUNNING"
        }, cancellationToken).ConfigureAwait(false);
        using JsonDocument response = JsonDocument.Parse(await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false));
        RequireType(response.RootElement, "HeartbeatAck");
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task<Stream> CreateStreamAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream network = client.GetStream();
        if (!options.UseTls)
        {
            if (!IsLoopback(options.Host))
            {
                throw new InvalidOperationException("Plaintext ControlServer transport is allowed only on loopback.");
            }
            return network;
        }
        SslStream ssl = new(network, leaveInnerStreamOpen: false, ValidateServerCertificate);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = options.Host
        }, cancellationToken).ConfigureAwait(false);
        return ssl;
    }

    private bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        _ = sender;
        _ = chain;
        if (certificate is null || string.IsNullOrWhiteSpace(options.ServerCertificateSha256))
        {
            return false;
        }
        string actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        string expected = options.ServerCertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal);
        return errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateNameMismatch &&
               actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExchangeAsync(
        StreamWriter writer,
        StreamReader reader,
        string messageType,
        long generation,
        object payload,
        string expectedResponse,
        CancellationToken cancellationToken)
    {
        await SendAsync(writer, messageType, Guid.NewGuid().ToString("D"), generation, payload, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument response = JsonDocument.Parse(await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false));
        RequireType(response.RootElement, expectedResponse);
    }

    private async Task SendAsync(
        StreamWriter writer,
        string messageType,
        string messageId,
        long generation,
        object payload,
        CancellationToken cancellationToken)
    {
        object envelope = WireToGateProtocol.CreateEnvelope(
            messageType, messageId, options.AgvId, generation, payload);
        await writer.WriteLineAsync(JsonSerializer.Serialize(envelope, SerializerOptions).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken) =>
        await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new EndOfStreamException("ControlServer closed the connection during recovery handshake.");

    private static void RequireType(JsonElement root, string expected)
    {
        string? actual = root.GetProperty("messageType").GetString();
        int protocolVersion = root.GetProperty("protocolVersion").GetInt32();
        string? profileId = root.GetProperty("profileId").GetString();
        string? releaseVersion = root.GetProperty("protocolReleaseVersion").GetString();
        string? manifestSha256 = root.GetProperty("protocolReleaseManifestSha256").GetString();
        if (!string.Equals(actual, expected, StringComparison.Ordinal) ||
            protocolVersion != ProtocolCandidateIdentity.ProtocolVersion ||
            !string.Equals(profileId, ProtocolCandidateIdentity.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(releaseVersion, ProtocolCandidateIdentity.ReleaseVersion, StringComparison.Ordinal) ||
            !string.Equals(manifestSha256, ProtocolCandidateIdentity.ManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Expected exact candidate '{expected}', received '{actual}' with a different release identity.");
        }
    }

    private static object ToProtocolSlot(SlotIoState state) => new
    {
        slotNo = state.PhysicalSlotNumber,
        operability = state.Online ? "OPERABLE" : "INOPERABLE",
        administrativeAvailability = "ENABLED",
        physicalState = state.Occupancy.ToString().ToUpperInvariant(),
        lockState = state.DoorLock switch
        {
            SlotDoorLock.Locked => "LOCKED",
            SlotDoorLock.NotLocked => "UNLOCKED",
            _ => "UNKNOWN"
        },
        unlockOutputState = state.UnlockOutput.ToString().ToUpperInvariant(),
        reasonCodes = state.Online ? Array.Empty<string>() : new[] { "IO_OFFLINE" }
    };

    private static string ComputeJournalHash(IReadOnlyList<JournalAttempt> attempts)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(attempts.OrderBy(item => item.SlotOperationAttemptId));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host == "127.0.0.1" || host == "::1";

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65_535 ||
            string.IsNullOrWhiteSpace(options.AgvId) || string.IsNullOrWhiteSpace(options.OnboardInstanceId) ||
            string.IsNullOrWhiteSpace(options.CredentialEnvironmentVariable) || options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new InvalidDataException("ControlServer session options are invalid.");
        }
        if (options.UseTls && string.IsNullOrWhiteSpace(options.ServerCertificateSha256))
        {
            throw new InvalidDataException("TLS requires an explicit SHA-256 server certificate pin.");
        }
    }

    private async ValueTask DisposeConnectionAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
        _reader?.Dispose();
        _reader = null;
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }
        _client?.Dispose();
        _client = null;
    }
}

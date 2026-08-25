using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SQCD.Agv.Core;

int port = args.Length == 0 ? 58_015 : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
string credentialVariable = args.Length < 2 ? "CONTROL_SERVER_ONBOARD_CREDENTIAL" : args[1];
string expectedCredential = Environment.GetEnvironmentVariable(credentialVariable)
    ?? throw new InvalidOperationException($"Credential environment variable '{credentialVariable}' is not set.");
TcpListener listener = new(IPAddress.Loopback, port);
listener.Start();
try
{
    using TcpClient client = await listener.AcceptTcpClientAsync();
    await using NetworkStream stream = client.GetStream();
    using StreamReader reader = new(stream, Encoding.UTF8, false, leaveOpen: true);
    await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

    using JsonDocument hello = await ReadAsync(reader);
    Require(hello.RootElement, "SessionHello");
    JsonElement helloPayload = hello.RootElement.GetProperty("payload");
    if (helloPayload.GetProperty("credentialProof").GetString() != expectedCredential)
    {
        throw new InvalidDataException("Fake ControlServer received an invalid credential proof.");
    }
    string agvId = hello.RootElement.GetProperty("agvId").GetString() ?? throw new InvalidDataException("agvId missing");
    await SendAsync(writer, "SessionAccepted", agvId, 1, new
    {
        sessionGeneration = 1,
        serverInstanceId = "FAKE-CONTROL-SERVER",
        serverBuildCommit = "FAKE_BUILD",
        acceptedProtocolReleaseIdentity = WireToGateProtocol.ReleaseIdentity(),
        acceptedAt = DateTimeOffset.UtcNow
    });

    using JsonDocument capability = await ReadAsync(reader);
    Require(capability.RootElement, "CapabilitySnapshot");
    RequireEightSlots(capability.RootElement);
    await SendAsync(writer, "SnapshotAppliedAck", agvId, 1, new { applied = true });

    using JsonDocument safety = await ReadAsync(reader);
    Require(safety.RootElement, "SafetyStateSnapshot");
    RequireEightSlots(safety.RootElement);
    await SendAsync(writer, "SnapshotAppliedAck", agvId, 1, new { applied = true });

    using JsonDocument recovery = await ReadAsync(reader);
    Require(recovery.RootElement, "RecoveryStateReport");
    await SendAsync(writer, "DurableAck", agvId, 1, new { durable = true });
    await SendAsync(writer, "SessionReadiness", agvId, 1, new { readiness = "READY", reasonCode = "READY" });
    using JsonDocument heartbeat = await ReadAsync(reader);
    Require(heartbeat.RootElement, "Heartbeat");
    await SendAsync(writer, "HeartbeatAck", agvId, 1, new { accepted = true });
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "PASS",
        scenario = "FAKE_CONTROL_SERVER_FIVE_STEP_RECOVERY",
        agvId,
        protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
        manifestSha256 = ProtocolCandidateIdentity.ManifestSha256
    }));
}
finally
{
    listener.Stop();
}

static async Task<JsonDocument> ReadAsync(StreamReader reader) =>
    JsonDocument.Parse(await reader.ReadLineAsync() ?? throw new EndOfStreamException());

static async Task SendAsync(StreamWriter writer, string type, string agvId, long generation, object payload) =>
    await writer.WriteLineAsync(JsonSerializer.Serialize(WireToGateProtocol.CreateEnvelope(
        type, Guid.NewGuid().ToString("D"), agvId, generation, payload)));

static void Require(JsonElement root, string type)
{
    if (root.GetProperty("messageType").GetString() != type ||
        root.GetProperty("profileId").GetString() != ProtocolCandidateIdentity.ProfileId ||
        root.GetProperty("protocolReleaseManifestSha256").GetString() != ProtocolCandidateIdentity.ManifestSha256)
    {
        throw new InvalidDataException($"Expected exact candidate message '{type}'.");
    }
}

static void RequireEightSlots(JsonElement root)
{
    if (root.GetProperty("payload").GetProperty("slotStates").GetArrayLength() != 8)
    {
        throw new InvalidDataException("Snapshot must carry exactly eight slots.");
    }
}

using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

string serverHost = args.Length > 0 ? args[0] : "127.0.0.1";
int serverPort = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 58_015;
string ioUrl = args.Length > 2 ? args[2] : "http://127.0.0.1:58006";
string journalPath = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "8005-onboard-conformance", "journal.db");
bool seedPendingResult = args.Length > 4 && args[4].Equals("seed-pending-result", StringComparison.OrdinalIgnoreCase);
HttpClient httpClient = new() { BaseAddress = new Uri(ioUrl), Timeout = TimeSpan.FromSeconds(2) };
await using HttpSimulatorSlotIoProvider provider = new(httpClient);
await using SqliteOnboardExecutionJournal journal = new(journalPath);
await journal.InitializeAsync(CancellationToken.None);
if (seedPendingResult)
{
    await journal.PrepareAsync(new JournalAttempt(
        "00000000-0000-4000-8000-000000000301",
        "00000000-0000-4000-8000-000000000302",
        new string('e', 64),
        [1, 2],
        SlotOccupancy.Occupied,
        1,
        JournalAttemptStatus.ResultPendingAck,
        "{\"outcome\":\"COMPLETED\"}",
        DateTimeOffset.UtcNow), CancellationToken.None);
}
await using WireToGateSessionClient client = new(
    new WireToGateSessionOptions(
        serverHost, serverPort, "AGV-FAKE-001", "OBU-CONFORMANCE-001",
        "CONTROL_SERVER_ONBOARD_CREDENTIAL", false, null, TimeSpan.FromSeconds(3)),
    provider,
    journal);
WireToGateHandshakeResult result = await client.ConnectAndRecoverAsync(CancellationToken.None);
VehicleBusinessReadiness expectedReadiness = seedPendingResult
    ? VehicleBusinessReadiness.RecoveryRequired
    : VehicleBusinessReadiness.Ready;
if (result.Readiness != expectedReadiness || result.SlotStates.Count != 8)
{
    throw new InvalidOperationException(
        $"Onboard conformance recovery reached {result.Readiness}, expected {expectedReadiness}, with {result.SlotStates.Count} slots.");
}
await client.SendHeartbeatAsync(result.SessionGeneration, CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PASS",
    scenario = seedPendingResult
        ? "ONBOARD_PENDING_RESULT_RECOVERY_WITH_CONTROL_SERVER"
        : "ONBOARD_HTTP_IO_AND_CONTROL_SERVER",
    result.SessionGeneration,
    readiness = result.Readiness.ToString().ToUpperInvariant(),
    slotCount = result.SlotStates.Count,
    protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
    manifestSha256 = ProtocolCandidateIdentity.ManifestSha256
}));

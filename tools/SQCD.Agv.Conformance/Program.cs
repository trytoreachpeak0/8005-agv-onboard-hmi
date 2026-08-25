using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

string serverHost = args.Length > 0 ? args[0] : "127.0.0.1";
int serverPort = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 58_015;
string ioUrl = args.Length > 2 ? args[2] : "http://127.0.0.1:58006";
string journalPath = args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "8005-onboard-conformance", "journal.db");
HttpClient httpClient = new() { BaseAddress = new Uri(ioUrl), Timeout = TimeSpan.FromSeconds(2) };
await using HttpSimulatorSlotIoProvider provider = new(httpClient);
await using SqliteOnboardExecutionJournal journal = new(journalPath);
await journal.InitializeAsync(CancellationToken.None);
await using WireToGateSessionClient client = new(
    new WireToGateSessionOptions(
        serverHost, serverPort, "AGV-FAKE-001", "OBU-CONFORMANCE-001",
        "CONTROL_SERVER_ONBOARD_CREDENTIAL", false, null, TimeSpan.FromSeconds(3)),
    provider,
    journal);
WireToGateHandshakeResult result = await client.ConnectAndRecoverAsync(CancellationToken.None);
if (result.Readiness != VehicleBusinessReadiness.Ready || result.SlotStates.Count != 8)
{
    throw new InvalidOperationException("Onboard conformance recovery did not reach READY with eight slots.");
}
await client.SendHeartbeatAsync(result.SessionGeneration, CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "PASS",
    scenario = "ONBOARD_HTTP_IO_AND_FAKE_CONTROL_SERVER",
    result.SessionGeneration,
    readiness = result.Readiness.ToString().ToUpperInvariant(),
    slotCount = result.SlotStates.Count,
    protocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
    manifestSha256 = ProtocolCandidateIdentity.ManifestSha256
}));

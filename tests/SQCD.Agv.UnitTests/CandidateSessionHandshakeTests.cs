using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class CandidateSessionHandshakeTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void SessionHelloIsMaterializedFromTheCandidateContract()
    {
        WireToGateSessionPlanner planner = new("OBU-001", "test-credential", "test-build");

        ReadOnlyMemory<byte> line = planner.CreateSessionHello("AGV-8005-01", 1);

        Assert.False(line.IsEmpty);
        Assert.Equal((byte)'\n', line.Span[^1]);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(line.Span[..^1].ToArray());
        System.Text.Json.JsonElement root = document.RootElement;
        Assert.Equal("SessionHello", root.GetProperty("messageType").GetString());
        Assert.Equal(ProtocolCandidateIdentity.RepositoryCommit,
            root.GetProperty("payload").GetProperty("protocolReleaseIdentity").GetProperty("commit").GetString());
        Assert.Equal(ProtocolCandidateIdentity.ManifestSha256,
            root.GetProperty("protocolReleaseManifestSha256").GetString());
        Assert.False(root.TryGetProperty("sessionGeneration", out System.Text.Json.JsonElement generation) &&
                     generation.ValueKind != System.Text.Json.JsonValueKind.Null);
    }
}

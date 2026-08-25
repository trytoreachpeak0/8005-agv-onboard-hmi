using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

public sealed class CandidateSessionHandshakeTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public void SessionHelloIsMaterializedFromTheCandidateContract()
    {
        WireToGateSessionPlanner planner = new();

        ReadOnlyMemory<byte> line = planner.CreateSessionHello("AGV-8005-01", 1);

        Assert.False(line.IsEmpty);
        Assert.Equal((byte)'\n', line.Span[^1]);
    }
}

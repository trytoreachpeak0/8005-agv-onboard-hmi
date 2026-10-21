using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The record behind a reconnect handshake's safety version (onboard-hmi#394): which lines it counts, the version it
/// hands the handshake, and when it says a version went out twice with other content.
/// </summary>
/// <remarks>
/// End to end, over a real connection and the business service, in <c>SQCD.Agv.WireToGateG2Tests</c>
/// (<c>HandshakeSafetyRevisionReuse</c>, <c>RoamingSafetyRevisions</c>); this class holds the rule itself.
/// </remarks>
public sealed class WireToGateWrittenSafetyStatesTests
{
    [Fact]
    public void WithNothingWrittenTheHandshakeKeepsTheAcceptedVersion()
    {
        WireToGateWrittenSafetyStates written = new();

        Assert.Null(written.Highest);
        Assert.Equal(1, written.NextHandshakeVersion(1));
    }

    [Theory]
    [InlineData(5, 5, 6)]
    [InlineData(7, 5, 8)]
    [InlineData(5, 9, 10)]
    public void OnceAnythingIsWrittenTheHandshakeGoesAboveItAndAboveTheAcceptedVersion(
        long writtenVersion,
        long acceptedVersion,
        long expected)
    {
        WireToGateWrittenSafetyStates written = new();
        Assert.Null(written.Record(Line("SafetyStateSnapshot", writtenVersion, "a")));

        Assert.Equal(expected, written.NextHandshakeVersion(acceptedVersion));
    }

    [Theory]
    [InlineData("SafetyStateChanged")]
    [InlineData("SafetyStateSnapshot")]
    public void TheSameContentWrittenAgainIsNotAConflict(string messageType)
    {
        WireToGateWrittenSafetyStates written = new();

        Assert.Null(written.Record(Line(messageType, 3, "a")));
        Assert.Null(written.Record(Line(messageType, 3, "a", generation: 9)));
        Assert.Equal(3, written.Highest);
    }

    [Fact]
    public void OtherContentUnderAWrittenVersionIsReported()
    {
        WireToGateWrittenSafetyStates written = new();
        Assert.Null(written.Record(Line("SafetyStateChanged", 94, "locked")));

        string? conflict = written.Record(Line("SafetyStateSnapshot", 94, "unknown"));

        Assert.NotNull(conflict);
        Assert.Contains("94", conflict, StringComparison.Ordinal);
        Assert.Contains("locked", conflict, StringComparison.Ordinal);
        Assert.Contains("unknown", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void LinesThatOnlyMentionASafetyVersionAreNotCounted()
    {
        WireToGateWrittenSafetyStates written = new();

        Assert.Null(written.Record(Line("PreDepartureSafetyCheckResult", 50, "a")));
        Assert.Null(written.Record(Line("Heartbeat", 51, "a")));
        Assert.Null(written.Record(Line("SafetyStateSnapshotRequested", 52, "a")));

        Assert.Null(written.Highest);
    }

    [Fact]
    public void OnlyTheLatestVersionsKeepTheirContentButTheHighestStays()
    {
        WireToGateWrittenSafetyStates written = new();
        for (long version = 1; version <= WireToGateWrittenSafetyStates.RememberedCount + 1; version++)
        {
            Assert.Null(written.Record(Line("SafetyStateChanged", version, "a")));
        }

        // Version 2 is still remembered; version 1 has been forgotten, so other content under it is not reported. The
        // numbering still goes above all.
        Assert.NotNull(written.Record(Line("SafetyStateChanged", 2, "b")));
        Assert.Null(written.Record(Line("SafetyStateChanged", 1, "b")));
        Assert.Equal(WireToGateWrittenSafetyStates.RememberedCount + 2, written.NextHandshakeVersion(1));
    }

    private static string Line(string messageType, long version, string marker, long generation = 1) =>
        $"{{\"messageType\":\"{messageType}\",\"sessionGeneration\":{generation},"
        + $"\"payload\":{{\"safetyStateVersion\":{version},\"marker\":\"{marker}\"}}}}";
}

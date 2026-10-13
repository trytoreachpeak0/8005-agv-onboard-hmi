using SQCD.Agv.Contracts;
using SQCD.Agv.Infrastructure;
using Xunit;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// The register behind <c>WireToGateSessionClient.AcceptedSafety</c> (onboard-hmi#314): which record wins, and that a
/// version is never paired with another version's summary however the writers race.
/// </summary>
/// <remarks>
/// The three situations that made the research's plain "only up" rule wrong are run end to end in
/// <c>SQCD.Agv.WireToGateG2Tests.WireToGateG2Tests</c> (<c>AcceptedSafety*</c>); this class holds the rule itself.
/// </remarks>
public sealed class WireToGateAcceptedSafetyRegisterTests
{
    private static readonly WireToGateSafetySummaryPayload Safe = Summary(true);
    private static readonly WireToGateSafetySummaryPayload Unsafe = Summary(false, "LOCK_NOT_CLOSED");

    [Fact]
    public void ItStartsAtTheConfiguredVersionWithTheSummaryUnknown()
    {
        WireToGateAcceptedSafetyRegister register = new(7);

        Assert.Equal(new WireToGateAcceptedSafety(7, null, null, WireToGateAcceptedSafetySource.Configured), register.Current);
    }

    public static TheoryData<string, WireToGateAcceptedSafety, WireToGateAcceptedSafety, bool> Rules() => new()
    {
        { "higher version wins", Record(5, 1, Safe), Record(6, 1, Unsafe), true },
        { "higher version wins even with the summary unknown", Record(5, 1, Safe), Record(6, 1, null), true },
        { "lower version never wins", Record(6, 1, Safe), Record(5, 2, Unsafe, WireToGateAcceptedSafetySource.HandshakeSnapshot), false },
        { "same version: known replaces unknown", Record(5, null, null, WireToGateAcceptedSafetySource.Configured), Record(5, 1, Safe, WireToGateAcceptedSafetySource.HandshakeSnapshot), true },
        { "same version: known replaces unknown from readiness", Record(5, 1, null, WireToGateAcceptedSafetySource.SessionReadiness), Record(5, 1, Safe), true },
        { "same version: unknown never replaces known", Record(5, 1, Safe), Record(5, 2, null, WireToGateAcceptedSafetySource.SessionReadiness), false },
        { "same version: a newer generation's handshake replaces", Record(5, 1, Safe, WireToGateAcceptedSafetySource.HandshakeSnapshot), Record(5, 2, Unsafe, WireToGateAcceptedSafetySource.HandshakeSnapshot), true },
        { "same version: the same generation's handshake does not", Record(5, 2, Safe, WireToGateAcceptedSafetySource.HandshakeSnapshot), Record(5, 2, Unsafe, WireToGateAcceptedSafetySource.HandshakeSnapshot), false },
        { "same version: an older generation's handshake does not", Record(5, 3, Safe), Record(5, 2, Unsafe, WireToGateAcceptedSafetySource.HandshakeSnapshot), false },
        { "same version: a late acknowledgement does not", Record(5, 2, Safe, WireToGateAcceptedSafetySource.HandshakeSnapshot), Record(5, 1, Unsafe, WireToGateAcceptedSafetySource.LateSafetyStateChangedAck), false },
        { "same version: a change of a newer generation does not", Record(5, 1, Safe), Record(5, 2, Unsafe), false },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void WhichRecordWins(string rule, WireToGateAcceptedSafety current, WireToGateAcceptedSafety next, bool wins)
    {
        _ = rule;
        Assert.Equal(wins, WireToGateAcceptedSafetyRegister.Supersedes(next, current));

        WireToGateAcceptedSafetyRegister register = new(0);
        Assert.True(register.Register(current));
        Assert.Equal(wins, register.Register(next));
        Assert.Same(wins ? next : current, register.Current);
    }

    /// <summary>
    /// Many writers, versions out of order and repeated, as late and early acknowledgements arrive: the version only
    /// goes up, ends at the highest, and every record read on the way carries its own version's summary.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritersNeverPairAVersionWithAnotherVersionsSummaryAndNeverGoDown()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        WireToGateAcceptedSafetyRegister register = new(0);
        const int Writers = 8;
        const int Versions = 400;
        using CancellationTokenSource reading = new();
        List<string> offences = [];
        Task reader = Task.Run(() =>
        {
            long last = -1;
            while (!reading.IsCancellationRequested)
            {
                WireToGateAcceptedSafety seen = register.Current;
                if (seen.SafetyStateVersion < last)
                {
                    offences.Add($"went down from {last} to {seen.SafetyStateVersion}");
                }

                if (seen.Safety is { } safety && safety.ReasonCodes.Single() != VersionCode(seen.SafetyStateVersion))
                {
                    offences.Add($"version {seen.SafetyStateVersion} carried {safety.ReasonCodes.Single()}");
                }

                last = seen.SafetyStateVersion;
            }
        }, testToken);

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() =>
        {
            Random random = new(writer);
            foreach (int version in Enumerable.Range(1, Versions).OrderBy(_ => random.Next()))
            {
                register.Register(new WireToGateAcceptedSafety(
                    version,
                    writer,
                    random.Next(4) == 0 ? null : Summary(false, VersionCode(version)),
                    random.Next(2) == 0
                        ? WireToGateAcceptedSafetySource.HandshakeSnapshot
                        : WireToGateAcceptedSafetySource.SafetyStateChanged));
            }
        }, testToken)));
        await reading.CancelAsync();
        await reader;

        Assert.Empty(offences);
        Assert.Equal(Versions, register.Current.SafetyStateVersion);
        Assert.Equal(VersionCode(Versions), register.Current.Safety?.ReasonCodes.Single() ?? VersionCode(Versions));
    }

    [Fact]
    public void ASummaryIsRecalledByItsVersionAndTheNewestBuildOfAVersionWins()
    {
        WireToGateAcceptedSafetyRegister register = new(0);
        register.Remember(3, Safe);
        register.Remember(4, Unsafe);
        register.Remember(3, Unsafe);

        Assert.Same(Unsafe, register.Recall(3));
        Assert.Same(Unsafe, register.Recall(4));
        Assert.Null(register.Recall(5));
    }

    [Fact]
    public void OnlyTheNewestSummariesAreRemembered()
    {
        WireToGateAcceptedSafetyRegister register = new(0);
        int count = WireToGateAcceptedSafetyRegister.RememberedCount;
        for (int version = 1; version <= count + 1; version++)
        {
            register.Remember(version, Summary(false, VersionCode(version)));
        }

        Assert.Null(register.Recall(1));
        Assert.Equal(VersionCode(2), register.Recall(2)?.ReasonCodes.Single());
        Assert.Equal(VersionCode(count + 1), register.Recall(count + 1)?.ReasonCodes.Single());
    }

    private static WireToGateAcceptedSafety Record(
        long version,
        long? generation,
        WireToGateSafetySummaryPayload? safety,
        WireToGateAcceptedSafetySource source = WireToGateAcceptedSafetySource.SafetyStateChanged) =>
        new(version, generation, safety, source);

    private static WireToGateSafetySummaryPayload Summary(bool departureSafe, params string[] reasonCodes) =>
        new(departureSafe, true, departureSafe, true, false, reasonCodes);

    private static string VersionCode(long version) => $"V{version}";
}

using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A <c>LoadCancellationResult</c> the control server refuses for good (onboard-hmi#254), on the cancellation before any
/// sublot: the one vector whose leftover also keeps sublot entry shut, and the one entry a station operator has with the
/// maintenance switch off.
/// </summary>
public sealed partial class LoadCancellationBeforeSublotG2Tests
{
    private const string RefusalProofVariable = "W2G_G2_BEFORE_SUBLOT_PROOF";

    /// <summary>The factory default -- the maintenance switch off -- with a proof configured for the manual check.</summary>
    private static WireToGateRecoveryOptions MaintenanceSwitchOffWithProof()
    {
        Environment.SetEnvironmentVariable(RefusalProofVariable, "before-sublot-proof");
        return new WireToGateRecoveryOptions(
            ResumeAfterRepairEnabled: false,
            RefusalProofVariable,
            "MAINTENANCE_ADMINISTRATOR",
            "CONFIGURED_PROOF");
    }

    /// <summary>
    /// The vector stays and sublot entry stays shut -- the result is given up, not settled -- until a verified maintainer
    /// ends the recovery after the manual check, with the maintenance switch off. Ended, the vector and the unanswered
    /// cancellation go, entry is no longer held by them, and the result is never sent again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ARefusedCancellationBeforeAnySublotHoldsEntryUntilAMaintainerEndsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
            },
            recoveryOptions: MaintenanceSwitchOffWithProof());

        await harness.Business.RequestLoadCancellationAsync("到站后现场确认本站没有要装的货。", token);
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.Business.ConflictedRecoveryView is not null,
            "the refused cancellation to wait for a maintainer's manual check",
            token);

        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
        Assert.True(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.True(harness.Business.CanCloseConflictedRecoveryAfterReview);

        // While it waits for the check, cancelling again is not offered, and a press reports nothing again.
        Assert.False(harness.Business.CanRequestLoadCancellation);
        Assert.False(await harness.Business.RequestLoadCancellationAsync("到站后现场确认本站没有要装的货。", token));
        Assert.Single(harness.ResultsReceived());

        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.PendingLoadCancellation);
        Assert.False(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.Null(harness.Business.ConflictedRecoveryView);
        Assert.Single(harness.ResultsReceived());
        Assert.Equal(0, harness.Io.UnlockCount);
    }
}

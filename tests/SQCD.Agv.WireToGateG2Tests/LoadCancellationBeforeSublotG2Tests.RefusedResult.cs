using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A <c>LoadCancellationResult</c> the control server refuses for good (onboard-hmi#254), on the cancellation before any
/// sublot: the one vector whose leftover also keeps sublot entry shut.
/// </summary>
public sealed partial class LoadCancellationBeforeSublotG2Tests
{
    private const string RefusalProofVariable = "W2G_G2_BEFORE_SUBLOT_PROOF";

    private static WireToGateRecoveryOptions MaintenanceSwitchOn()
    {
        Environment.SetEnvironmentVariable(RefusalProofVariable, "before-sublot-proof");
        return new WireToGateRecoveryOptions(
            ResumeAfterRepairEnabled: true,
            RefusalProofVariable,
            "MAINTENANCE_ADMINISTRATOR",
            "CONFIGURED_PROOF");
    }

    /// <summary>
    /// The vector stays and sublot entry stays shut -- the result is given up, not settled -- until a verified maintainer
    /// closes the recovery after checking on site. Closed, the cancellation is settled as an acknowledged one is: the
    /// vector goes, entry is no longer held by it, and the result is never sent again.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task ARefusedCancellationBeforeAnySublotHoldsEntryUntilAMaintainerClosesIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
            },
            recoveryOptions: MaintenanceSwitchOn());

        await harness.Business.RequestLoadCancellationAsync("到站后现场确认本站没有要装的货。", token);
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.Business.ConflictedRecoveryView is not null,
            "the refused cancellation to wait for a maintainer's review",
            token);

        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
        Assert.True(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.False(harness.Business.CanSubmitSublot);
        Assert.True(harness.Business.CanCloseConflictedRecoveryAfterReview);

        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.PendingLoadCancellation);
        Assert.False(harness.Business.IsLoadCancellationBeforeSublotOpen);
        Assert.Null(harness.Business.ConflictedRecoveryView);
        Assert.Single(harness.ResultsReceived());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// With the factory default -- the maintenance switch off, as on every vehicle until it is turned on -- the close is
    /// not offered: it is a recovery entry, behind the same switch as the others. Pinned as the build behaves today; the
    /// coordinator was asked whether this case needs its exit without the switch (onboard-hmi#254).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public async Task WithTheMaintenanceSwitchOffTheCloseIsNotOffered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using BeforeSublotHarness harness = await BeforeSublotHarness.StartAsync(
            token,
            server => server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LoadCancellationResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
            });

        await harness.Business.RequestLoadCancellationAsync("到站后现场确认本站没有要装的货。", token);
        await BeforeSublotHarness.WaitUntilAsync(
            () => harness.Business.ConflictedRecoveryView is not null,
            "the refused cancellation to wait for a maintainer's review",
            token);

        Assert.False(harness.Business.CanCloseConflictedRecoveryAfterReview);
        Assert.False(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
    }
}

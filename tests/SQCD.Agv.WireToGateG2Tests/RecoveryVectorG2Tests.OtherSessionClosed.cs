using System.Text.Json;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A CLOSED snapshot for a session other than the one on file touches nothing, and is still
/// acknowledged (onboard-hmi#129 C-3).
/// </summary>
/// <remarks>
/// The CLOSED fallback forgets a session by its id. The server replays every unacknowledged CLOSED
/// on each handshake, so the vehicle routinely hears about sessions it no longer has on file while
/// it holds another: that CLOSED must leave the journal, the cached state behind the recovery
/// entries and the entries themselves exactly as they were -- and still be acknowledged, or the
/// server replays it for good.
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string OtherSessionId = "0e0e0e0e-0000-4000-8000-000000000129";
    private const string OtherSessionClosedMessageId = "abcdabcd-0000-4000-8000-000000001291";

    /// <summary>Session X holds a prepared compensation vector; session Y's CLOSED arrives.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnotherSessionsClosedSnapshotLeavesAPreparedVectorAloneAndIsAcknowledged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.SendRecoveryVectorCommandAfterRecoveryAction = false;
                server.RecoverySlotOperationAttemptId = AttemptId;
            },
            cargoInTargetSlots: true);
        WireToGateRecoveryState prepared = await PrepareCompensationAsync(harness, token);

        await AssertAnotherSessionsClosedChangesNothingAsync(harness, prepared, token);
    }

    /// <summary>Session X holds an accepted resume action; session Y's CLOSED arrives.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AnotherSessionsClosedSnapshotLeavesAnOpenResumeAloneAndIsAcknowledged()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId);
        WireToGateRecoveryState opened = await OpenResumeActionAsync(harness, token);

        await AssertAnotherSessionsClosedChangesNothingAsync(harness, opened, token);
    }

    private static async Task AssertAnotherSessionsClosedChangesNothingAsync(
        RecoveryVectorHarness harness,
        WireToGateRecoveryState onFile,
        CancellationToken token)
    {
        Assert.NotNull(onFile.ExceptionRecoverySessionId);
        Assert.NotEqual(OtherSessionId, onFile.ExceptionRecoverySessionId);
        Assert.Equal(AttemptId, onFile.UnsettledSlotOperationAttemptId);
        string journalBefore = JsonSerializer.Serialize(await harness.ReadRecoveryStateAsync(token));
        string entriesBefore = RecoveryEntries(harness.Business);

        // The acknowledgement alone does not prove the snapshot was handled: before onboard-hmi#129 it
        // went out ahead of the handling. The operator event is published once the handling is done.
        int closedHandled = 0;
        harness.Business.OperatorEventPublished += (_, args) =>
        {
            if (args.Value.Kind == "RECOVERY_SESSION_UPDATED"
                && args.Value.Message.Contains("已关闭", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref closedHandled);
            }
        };
        await SendClosedSnapshotAsync(
            harness,
            OtherSessionClosedMessageId,
            OtherSessionId,
            "COMPENSATE_LOAD_ALL_EMPTY",
            CompensationSlots);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => AcknowledgedRecoverySnapshots(harness).Contains(OtherSessionClosedMessageId),
            "the other session's CLOSED snapshot to be acknowledged",
            token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => Volatile.Read(ref closedHandled) > 0,
            "the other session's CLOSED snapshot to be handled",
            token);

        Assert.Equal(journalBefore, JsonSerializer.Serialize(await harness.ReadRecoveryStateAsync(token)));
        Assert.Equal(entriesBefore, RecoveryEntries(harness.Business));
    }

    /// <summary>
    /// What the operator can press, read off the business service: each gate reads the cached
    /// recovery state, so a cache rewritten by the CLOSED shows here.
    /// </summary>
    private static string RecoveryEntries(WireToGateBusinessService business) =>
        string.Join(
            ",",
            $"resume={business.CanRequestResumeAfterRepair}",
            $"compensation={business.CanRequestLoadCompensation}",
            $"faultCargo={business.CanRequestFaultCargoHandoff}",
            $"forced={business.CanRequestForcedMechanicalRecovery}",
            $"forcedConfirm={business.CanConfirmForcedMechanicalRecovery}",
            $"correction={business.CanRequestLoadCorrection}",
            $"operation={business.CurrentOperationSnapshot?.SlotOperationAttemptId}:{business.CurrentOperationSnapshot?.Stage}");

    /// <summary>The snapshot message ids the vehicle acknowledged as EXCEPTION_RECOVERY_SESSION.</summary>
    private static IReadOnlyList<string> AcknowledgedRecoverySnapshots(RecoveryVectorHarness harness) =>
    [
        .. harness.ResultsOfType("SnapshotAppliedAck")
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("payload").Clone())
            .Where(payload => payload.GetProperty("snapshotKind").GetString() == "EXCEPTION_RECOVERY_SESSION")
            .Select(payload => payload.GetProperty("snapshotMessageId").GetString()!)
    ];

    private static Task SendClosedSnapshotAsync(
        RecoveryVectorHarness harness,
        string messageId,
        string exceptionRecoverySessionId,
        string selectedAction,
        IReadOnlyList<int> slots) =>
        harness.Server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            messageId,
            new
            {
                exceptionRecoverySessionId,
                recoverySessionRevision = 3,
                state = "CLOSED",
                administratorId = "maintenance-001",
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = "0e0e0e0e-0000-4000-8000-00000000e129",
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                slots,
                selectedAction,
                allowedActions = Array.Empty<string>(),
                blockingFacts = Array.Empty<object>()
            });
}

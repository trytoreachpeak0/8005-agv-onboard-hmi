using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The entry that ends a recovery whose result the server refused for good, after a maintainer's manual check
/// (onboard-hmi#254 part 2): who may press it, what it writes, what it leaves, and what follows.
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    private const string DoorInDoubtNote =
        "a door the vector may have left open stays in the active unlock set (onboard-hmi#255)";

    /// <summary>
    /// The entry is on screen, pressed through the view model, and ends the recovery the way an acknowledged
    /// non-<c>COMPLETED</c> result does: the vector and the session go, the unsettled attempt and its context stay, and
    /// the active unlock set is left exactly as it was. Nothing goes on the wire and no door is touched.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedVectors))]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AMaintainerEndsARefusedRecoveryAfterTheManualCheck(RefusedVector refused)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResultTypeOf(refused)] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            loadAlreadySettled: refused == RefusedVector.LoadCorrection);
        await RunRefusedVectorAsync(harness, refused, token);
        WireToGateRecoveryVectorContext vector = await WaitForGivenUpResultAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.ConflictedRecoveryView is not null,
            "the refused recovery to wait for its manual check",
            token);

        // A door in doubt, put where an UNKNOWN result would have left it.
        await harness.RewriteRecoveryStateAsync(state => state with { ActiveUnlockSlots = [1] }, token);
        WireToGateRecoveryState before = await harness.ReadRecoveryStateAsync(token);

        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        // The same method App.xaml.cs calls, not a copy of its lines.
        ConflictedRecoveryWiring.Configure(viewModel, harness.Business);
        Assert.True(viewModel.HasConflictedRecovery);
        Assert.True(viewModel.CanCloseConflictedRecovery);
        Assert.Contains("人工核对后结束此恢复", viewModel.ConflictedRecoveryText, StringComparison.Ordinal);
        // Words, not codes: the codes are in the log.
        Assert.Contains("服务端对这次恢复已有另一份结论", viewModel.ConflictedRecoveryText, StringComparison.Ordinal);
        Assert.DoesNotContain("BUSINESS_ID_CONTENT_CONFLICT", viewModel.ConflictedRecoveryText, StringComparison.Ordinal);
        Assert.DoesNotContain(vector.VectorType, viewModel.ConflictedRecoveryText, StringComparison.Ordinal);
        Assert.Contains("需要维护人员的工号与凭据", viewModel.ConflictedRecoveryText, StringComparison.Ordinal);

        int sentBefore = harness.Server.ReceivedEnvelopes.Count;
        Assert.True(await viewModel.CloseConflictedRecoveryAsync(token));

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(after.RecoveryVector);
        Assert.Null(after.RecoveryActionId);
        Assert.Null(after.ExceptionRecoverySessionId);
        Assert.True(after.ActiveUnlockSlots.SequenceEqual([1]), DoorInDoubtNote);
        Assert.Equal(before.UnsettledSlotOperationAttemptId, after.UnsettledSlotOperationAttemptId);
        Assert.Equal(
            before.OperationContext?.SlotOperationAttemptId,
            after.OperationContext?.SlotOperationAttemptId);
        Assert.Equal(sentBefore, harness.Server.ReceivedEnvelopes.Count);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.False(viewModel.HasConflictedRecovery);
        Assert.False(viewModel.CanCloseConflictedRecovery);
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "CONFLICTED_RECOVERY_CLOSED");
        Assert.Contains(harness.Logger.Entries, entry =>
            entry.Message.StartsWith("维护人员现场核对后结束服务端拒收结果的恢复：", StringComparison.Ordinal)
            && entry.Message.Contains($"id={vector.PrimaryId}", StringComparison.Ordinal)
            && entry.Message.Contains("activeUnlockSlots=[1]", StringComparison.Ordinal));
        Assert.Single(harness.ResultsOfType(ResultTypeOf(refused)));
    }

    /// <summary>
    /// The maintenance switch off -- the factory default -- and the entry is still offered and still works: it starts no
    /// recovery and opens no door. Taken up again from the outbox after a restart, which is also where a vehicle whose
    /// switch was turned off meets it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task TheManualCheckEntryIsNotBehindTheMaintenanceSwitch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.RecoverySlotOperationAttemptId = AttemptId;
        server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FaultCargoRecoveryResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
        };
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath))
        {
            await RunRefusedVectorAsync(beforeRestart, RefusedVector.FaultCargoHandoff, token);
            await WaitForGivenUpResultAsync(beforeRestart, token);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            resumeAfterRepairEnabled: false);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.ConflictedRecoveryView is not null,
            "the refused recovery to be taken up again from the outbox after the restart",
            token);

        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        ConflictedRecoveryWiring.Configure(viewModel, afterRestart.Business);
        Assert.True(viewModel.CanCloseConflictedRecovery);
        // The switch is off: the entries that start a recovery are not offered.
        Assert.False(afterRestart.Business.CanRequestFaultCargoHandoff);
        Assert.False(afterRestart.Business.CanRequestForcedMechanicalRecovery);

        Assert.True(await viewModel.CloseConflictedRecoveryAsync(token));
        Assert.Null((await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector);
        Assert.Empty(afterRestart.ResultsOfType("ExceptionRecoverySessionRequested"));
        Assert.Empty(afterRestart.ResultsOfType("FaultCargoRecoveryResult"));
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// The entry asks for the recovery proof, as every maintenance entry does: without it, it is not offered and a press
    /// does nothing; the operator is told why.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task TheManualCheckEntryNeedsTheRecoveryProof()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["FaultCargoRecoveryResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            });
        await RunRefusedVectorAsync(harness, RefusedVector.FaultCargoHandoff, token);
        await WaitForGivenUpResultAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanCloseConflictedRecoveryAfterReview,
            "the manual check entry to be offered",
            token);

        string? proof = Environment.GetEnvironmentVariable(ProofVariable);
        Environment.SetEnvironmentVariable(ProofVariable, null);
        try
        {
            Assert.False(harness.Business.CanCloseConflictedRecoveryAfterReview);
            Assert.False(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProofVariable, proof);
        }

        Assert.NotNull((await harness.ReadRecoveryStateAsync(token)).RecoveryVector);
        Assert.NotNull(harness.Business.ConflictedRecoveryView);
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "RECOVERY_BLOCKED"
            && item.Message.Contains("凭据", StringComparison.Ordinal));
    }

    /// <summary>
    /// A press when nothing waits for a check -- another press, or the server's next command, got there first -- writes
    /// nothing and says so in words.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AManualCheckPressedTwiceEndsTheRecoveryOnce()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["FaultCargoRecoveryResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            });
        await RunRefusedVectorAsync(harness, RefusedVector.FaultCargoHandoff, token);
        await WaitForGivenUpResultAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanCloseConflictedRecoveryAfterReview,
            "the manual check entry to be offered",
            token);

        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        Assert.False(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        Assert.Single(harness.OperatorEvents, item => item.Kind == "CONFLICTED_RECOVERY_CLOSED");
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "RECOVERY_BLOCKED"
            && item.Message.Contains(
                OnboardCommandRejectionText.DescribeRecoveryBlocked("CONFLICTED_RECOVERY_NOT_PENDING"),
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The server sends the refused command again each round while the session is ready, under the same messageId. Every
    /// copy is refused before any door IO; the refusal is one outbox row, acknowledged and never replayed by a handshake;
    /// the operator is told once. After the manual check the same command runs.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ACommandRefusedWhileTheCheckIsPendingIsRefusedOnceOnFileAndRunsAfterTheCheck()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.RefusedRecoveryResultsWereReconciledByAnother = true;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCorrectionResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            loadAlreadySettled: true);
        await RunRefusedVectorAsync(harness, RefusedVector.LoadCorrection, token);
        await WaitForGivenUpResultAsync(harness, token);
        await harness.Session.Client.DisconnectAsync();
        Assert.Equal(
            WireToGateSessionReadiness.Ready,
            (await harness.Session.Client.ConnectAndRecoverAsync(token)).Readiness);

        const string newAttemptId = "9c9c9c9c-9c9c-4c9c-8c9c-9c9c9c9c9c9c";
        string commandMessageId = Guid.NewGuid().ToString("D");
        string rejectionKey = $"slot-operation-rejected:{newAttemptId}:ACTION_NOT_ALLOWED_IN_STATE";
        for (int copy = 1; copy <= 3; copy++)
        {
            await SendNewLoadCommandAsync(harness.Server, commandMessageId, newAttemptId);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => harness.Logger.Entries.Count(entry =>
                    entry.Message.StartsWith("拒收SlotOperationCommand：上一次恢复的结果被服务端拒收", StringComparison.Ordinal)
                    && entry.Message.Contains(newAttemptId, StringComparison.Ordinal)) >= copy,
                $"copy {copy} of the command to be refused",
                token);
        }

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadOutgoingAsync(rejectionKey, token).GetAwaiter().GetResult()?.Acknowledged == true,
            "the refusal's outbox row to be acknowledged",
            token);
        WireToGateDurableMessage refusal = (await harness.ReadOutgoingAsync(rejectionKey, token))!;
        using (JsonDocument line = JsonDocument.Parse(refusal.WireLine))
        {
            Assert.Equal(commandMessageId, line.RootElement.GetProperty("correlationId").GetString());
            Assert.Equal(
                "ACTION_NOT_ALLOWED_IN_STATE",
                line.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        }

        // One on the wire: the later copies find the row acknowledged and send nothing (the outbox's own rule for an
        // acknowledged key, WireToGateSessionClient.SendDurableCoreAsync).
        int refusalsOnTheWire = RefusalsOf(harness, newAttemptId);
        Assert.Equal(1, refusalsOnTheWire);
        Assert.Single(harness.OperatorEvents, item => item.Kind == "CONFLICTED_RECOVERY_PENDING"
            && item.Message.Contains("拒收这条命令", StringComparison.Ordinal));
        Assert.Equal(0, harness.Io.UnlockCount);

        // A handshake does not replay it: the row is acknowledged.
        await harness.Session.Client.DisconnectAsync();
        _ = await harness.Session.Client.ConnectAndRecoverAsync(token);
        Assert.Equal(refusalsOnTheWire, RefusalsOf(harness, newAttemptId));

        Assert.True(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        await SendNewLoadCommandAsync(harness.Server, commandMessageId, newAttemptId);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Server.ReceivedEnvelopes.Any(envelope => envelope.MessageType == "OperationProgress"
                && envelope.WireLine.Contains(newAttemptId, StringComparison.Ordinal)),
            "the same command to run once the recovery is ended",
            token);
        Assert.Equal(refusalsOnTheWire, RefusalsOf(harness, newAttemptId));
    }

    /// <summary>
    /// A vector on file under the key of a given-up row that did not produce it -- prepared afresh, its observation time
    /// cleared, as a second correction of the same load used to be before such a press was shut -- is not a recovery
    /// waiting for its check: the restore does not take it up, the entry is not shown even though one was shown for the
    /// first, and a press that gets past the screen is refused and writes nothing.
    /// </summary>
    /// <remarks>
    /// The journal is rewritten to that state rather than reached by pressing: the press is refused now
    /// (<see cref="ACorrectionRefusedAndEndedCannotBePressedAgain"/>), and the guards hold whatever brings it about.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AVectorUnderAGivenUpKeyThatDidNotProduceItIsNotAwaitingACheck()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LoadCorrectionResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            loadAlreadySettled: true);
        await RunRefusedVectorAsync(harness, RefusedVector.LoadCorrection, token);
        WireToGateRecoveryVectorContext vector = await WaitForGivenUpResultAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.ConflictedRecoveryView is not null,
            "the refused correction to wait for its manual check",
            token);
        int takenUpBefore = harness.OperatorEvents.Count(item => item.Kind == "CONFLICTED_RECOVERY_PENDING");

        // The same vector, prepared afresh: no result observed for it yet.
        await harness.RewriteRecoveryStateAsync(state => state with { RecoveryResultObservedAt = null }, token);
        await harness.Session.Client.DisconnectAsync();
        _ = await harness.Session.Client.ConnectAndRecoverAsync(token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == "OPERATION_PROGRESS"
                && item.Message.StartsWith($"恢复向量 {vector.VectorType} 尚未完成", StringComparison.Ordinal)),
            "the restore to show the correction as unfinished",
            token);

        Assert.Null(harness.Business.ConflictedRecoveryView);
        Assert.False(harness.Business.CanCloseConflictedRecoveryAfterReview);
        Assert.Equal(takenUpBefore, harness.OperatorEvents.Count(item => item.Kind == "CONFLICTED_RECOVERY_PENDING"));
        Assert.False(await harness.Business.CloseConflictedRecoveryAfterReviewAsync(token));
        Assert.Equal(vector.PrimaryId, (await harness.ReadRecoveryStateAsync(token)).RecoveryVector?.PrimaryId);
    }

    /// <summary>
    /// A correction refused for good and ended after the manual check cannot be pressed again for the same load: the
    /// server would take the second request as different content under the same correction, refuse it and command nothing,
    /// leaving a correction vector at the station that no command will ever come for. Not offered -- after a restart too --
    /// and a press that gets past the screen is refused before anything is written or sent.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task ACorrectionRefusedAndEndedCannotBePressedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.RecoveryVectorSlotOperationAttemptId = AttemptId;
        server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LoadCorrectionResult"] = "BUSINESS_ID_CONTENT_CONFLICT"
        };
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            await RunRefusedVectorAsync(beforeRestart, RefusedVector.LoadCorrection, token);
            await WaitForGivenUpResultAsync(beforeRestart, token);
            await RecoveryVectorHarness.WaitUntilAsync(
                () => beforeRestart.Business.CanCloseConflictedRecoveryAfterReview,
                "the manual check entry to be offered",
                token);
            Assert.True(await beforeRestart.Business.CloseConflictedRecoveryAfterReviewAsync(token));

            Assert.False(beforeRestart.Business.CanRequestLoadCorrection);
            Assert.False(await beforeRestart.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));
            Assert.Null((await beforeRestart.ReadRecoveryStateAsync(token)).RecoveryVector);
            Assert.Single(beforeRestart.ResultsOfType("LoadCorrectionRequested"));
            Assert.Contains(beforeRestart.OperatorEvents, item => item.Kind == "RECOVERY_BLOCKED"
                && item.Message.Contains(
                    OnboardCommandRejectionText.DescribeRecoveryBlocked("LOAD_CORRECTION_ALREADY_REFUSED"),
                    StringComparison.Ordinal));
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            nothingOnFile: true);
        // The restore reads the row back: until it has, nothing in this process knows the correction was refused.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Logger.Entries.Any(entry => entry.Message.StartsWith(
                "这次装货的修正结果已被服务端拒收并经人工核对结束，修正入口保持关闭", StringComparison.Ordinal)),
            "the restore to read the refused correction back after the restart",
            token);
        Assert.False(afterRestart.Business.CanRequestLoadCorrection);
        Assert.False(await afterRestart.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token));
        Assert.Empty(afterRestart.ResultsOfType("LoadCorrectionRequested"));
    }

    /// <summary>
    /// While a refused recovery waits for its manual check, the entry for the same action is shut, and a press that gets
    /// past the screen is refused before anything is written, sent or pulsed: pressed, it would ask again for a recovery
    /// the server has concluded, or run the vector again.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedVectors))]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task TheSameEntryIsShutWhileARefusedRecoveryAwaitsItsCheck(RefusedVector refused)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.RecoveryVectorSlotOperationAttemptId = AttemptId;
                server.ProtocolProblemByMessageType = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResultTypeOf(refused)] = "BUSINESS_ID_CONTENT_CONFLICT"
                };
            },
            loadAlreadySettled: refused == RefusedVector.LoadCorrection);
        await RunRefusedVectorAsync(harness, refused, token);
        WireToGateRecoveryVectorContext vector = await WaitForGivenUpResultAsync(harness, token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.ConflictedRecoveryView is not null,
            "the refused recovery to wait for its manual check",
            token);

        (Func<bool> offered, Func<Task<bool>> press) = refused switch
        {
            RefusedVector.FaultCargoHandoff => (
                (Func<bool>)(() => harness.Business.CanRequestFaultCargoHandoff),
                (Func<Task<bool>>)(() => harness.Business.RequestFaultCargoHandoffAsync("现场确认故障仓货物需要交接处理。", token))),
            RefusedVector.LoadCompensation => (
                () => harness.Business.CanRequestLoadCompensation,
                () => harness.Business.RequestLoadCompensationAsync("现场确认装货无法继续，申请补偿清空目标仓位。", token)),
            _ => (
                () => harness.Business.CanRequestLoadCorrection,
                () => harness.Business.RequestLoadCorrectionAsync("现场确认需要修正已完成的装货结果。", token))
        };
        Assert.False(offered());

        int sentBefore = harness.Server.ReceivedEnvelopes.Count;
        int unlocksBefore = harness.Io.UnlockCount;
        Assert.False(await press());
        Assert.Equal(sentBefore, harness.Server.ReceivedEnvelopes.Count);
        Assert.Equal(unlocksBefore, harness.Io.UnlockCount);
        AssertStillOnFile(vector, await harness.ReadRecoveryStateAsync(token));
        Assert.Contains(harness.OperatorEvents, item => item.Kind == "RECOVERY_BLOCKED"
            && item.Message.Contains(
                OnboardCommandRejectionText.DescribeRecoveryBlocked("RECOVERY_AWAITING_MANUAL_CHECK"),
                StringComparison.Ordinal));
    }

    private static int RefusalsOf(RecoveryVectorHarness harness, string attemptId) =>
        harness.ResultsOfType("SlotOperationCommandRejected")
            .Count(line => line.Contains(attemptId, StringComparison.Ordinal));

    private static Task SendNewLoadCommandAsync(FakeControlServer server, string messageId, string attemptId) =>
        server.SendCommandAsync(
            "SlotOperationCommand",
            messageId,
            new
            {
                demandId = "9d9d9d9d-9d9d-4d9d-8d9d-9d9d9d9d9d9d",
                operationSessionId = OperationSessionId,
                slotOperationAttemptId = attemptId,
                operationType = "LOAD",
                slots = NewLoadSlots,
                expectedBasketCount = 1,
                expectedFinalPhysicalState = "OCCUPIED",
                commandContentSha256 = new string('0', 64)
            },
            correlationId: "9e9e9e9e-9e9e-4e9e-8e9e-9e9e9e9e9e9e");
}

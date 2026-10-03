using System.Text.Json;
using SQCD.Agv.Application;
using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// Server recovery commands that open doors, replayed into a restarted vehicle, wait for someone at the vehicle
/// (onboard-hmi#239).
/// </summary>
/// <remarks>
/// <para>
/// The server replays every bound, unsettled recovery command into each new session (control-server
/// <c>ReplayPendingCommandsAsync</c>); the double does it for compensation after <c>RecoveryStateReport</c>, and these
/// tests send the other three kinds themselves where the server's replay would. Until #239 the vehicle checked only
/// that it stood still and the IO was fresh, and pulsed the slots: the press that earned the command could be
/// arbitrarily old, and nobody need be at the vehicle (hmi#236 review probe: afterRestartUnlocks=1).
/// </para>
/// <para>
/// <b>Every red test puts cargo in the target slots.</b> The clear short-circuits an already-empty slot to complete
/// with no pulse, so on an empty slot <c>UnlockCount == 0</c> is as true of a command carried out in full as of one
/// held -- and the base would look safe. With cargo an execution has to pulse; nobody empties the slot afterwards, so
/// the wait after the pulse times out, and the pulse is what counts.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string HeldRecoveryCommandKind = "RECOVERY_COMMAND_HELD";

    private static readonly string[] CorrectionSequence = ["EMPTY", "OCCUPIED"];

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationCommandReplayedAfterARestartOpensNothingUntilConfirmed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;

        await WaitForHeldOrExecutedAsync(afterRestart, "LoadCompensationResult", token);

        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.Empty(afterRestart.ResultsOfType("LoadCompensationResult"));
        WireToGateOperatorEvent shown = afterRestart.OperatorEvents.First(item => item.Kind == HeldRecoveryCommandKind);
        Assert.Contains("AGV-8005-01", shown.Message, StringComparison.Ordinal);
        Assert.Contains("补偿清空", shown.Message, StringComparison.Ordinal);
        Assert.Contains("车载端重启后", shown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("部分仓门已开过", shown.Message, StringComparison.Ordinal);
        string actionId = ActionIdFor(CompensateLoadAction);
        // One line per hold: the command type, its primaryId, and why it was held.
        Assert.Contains(afterRestart.Logger.Entries, entry =>
            entry.Message.StartsWith("服务端恢复命令已扣住，等待现场确认：", StringComparison.Ordinal)
            && entry.Message.Contains($"type={WireToGateRecoveryVectorTypes.LoadCompensation}", StringComparison.Ordinal)
            && entry.Message.Contains($"primaryId={actionId}", StringComparison.Ordinal)
            && entry.Message.Contains("原因=车载端重启后本进程没有人按过", StringComparison.Ordinal));
        Assert.Equal(actionId, afterRestart.Business.HeldRecoveryCommand?.PrimaryId);
        Assert.True(afterRestart.Business.CanConfirmHeldRecoveryCommand);
        Assert.True(afterRestart.Business.CanDeclineHeldRecoveryCommand);
        WireToGateRecoveryState state = await afterRestart.ReadRecoveryStateAsync(token);
        Assert.Null(state.RecoveryVector?.CommandContentSha256);
    }

    /// <summary>Confirmed at the vehicle, the held compensation runs down its own path, once.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AHeldCompensationIsCarriedOutOnceWhenTheOperatorConfirms()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);

        Assert.True(await afterRestart.Business.ConfirmHeldRecoveryCommandAsync(held, token));

        await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.True(afterRestart.Io.UnlockCount > 0, "the confirmed compensation has to pulse the slots holding cargo");
        Assert.Single(afterRestart.ResultsOfType("LoadCompensationResult"));
        Assert.Null(afterRestart.Business.HeldRecoveryCommand);
        Assert.False(afterRestart.Business.CanConfirmHeldRecoveryCommand);
        Assert.Contains(afterRestart.Logger.Entries, entry =>
            entry.Message.StartsWith("扣住的服务端恢复命令由操作员确认执行：", StringComparison.Ordinal)
            && entry.Message.Contains(
                $"操作员={Environment.GetEnvironmentVariable(OperatorVariable)}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Declined at the vehicle, the held compensation opens nothing and is answered <c>FAILED</c>, every slot
    /// <c>NOT_STARTED</c>: the server closes the session on it, so the held state has a governed end.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ADeclinedHeldCompensationIsAnsweredFailedAndOpensNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);

        Assert.True(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));

        JsonElement result = await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.All(result.GetProperty("slotResults").EnumerateArray(), slot =>
        {
            Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString());
            Assert.Contains(
                "VEHICLE_NOT_READY",
                slot.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()));
        });
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.OperatorEvents.Any(item => item.Kind == "RECOVERY_COMMAND_DECLINED"),
            "the operator to be told the compensation was not carried out",
            token);
        WireToGateRecoveryState state = await afterRestart.ReadRecoveryStateAsync(token);
        Assert.Null(state.RecoveryVector);
        Assert.Null(afterRestart.Business.HeldRecoveryCommand);
    }

    /// <summary>
    /// A compensation the process died in the middle of -- slot 1 done, slot 2 not reached -- is held too when its
    /// command is replayed, with the open slot named; declined, it is answered from the journal as <c>UNKNOWN</c> and
    /// slot 2 is never pulsed.
    /// </summary>
    /// <remarks>
    /// Carried on, the executor's resume path skips the completed slot and pulses the next one: the base pulsed slot 2
    /// with nobody asked.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task APartlyExecutedCompensationReplayedAfterARestartIsHeldAndDeclinedAsUnknown()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        WireToGateSlotExecutionResult slotOneDone = new(1, "COMPLETED", "EMPTY", "LOCKED", "RESET", []);
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(
            token,
            state => state with
            {
                ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
                ActiveUnlockSlots = [],
                CompletedSlots = [1],
                SlotResults = [slotOneDone]
            },
            replayInHandshake: false);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        // Slot 1 was emptied before the process went; slot 2 still holds its cargo. The command is sent here, where
        // the server's replay would come, so the slots can read that way when it arrives.
        afterRestart.Io.SetCargoPresent(0, false);
        await WaitForSessionToCarryCommandsAsync(afterRestart, token);
        WireToGateRecoveryVectorContext vector = (await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector!;
        await vehicle.After.SendCommandAsync(
            "LoadCompensationCommand",
            Guid.NewGuid().ToString("D"),
            new
            {
                recoveryActionId = vector.PrimaryId,
                exceptionRecoverySessionId = vector.ExceptionRecoverySessionId,
                demandId = vector.DemandId,
                slotOperationAttemptId = vector.SlotOperationAttemptId,
                slots = vector.Slots,
                expectedFinalPhysicalState = "EMPTY",
                commandContentSha256 = FakeControlServerIdentifiers.LoadCompensationContentSha256(
                    vector.PrimaryId, vector.DemandId, vector.SlotOperationAttemptId!, [.. vector.Slots])
            });

        await WaitForHeldOrExecutedAsync(afterRestart, "LoadCompensationResult", token);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        WireToGateHeldRecoveryCommandPrompt held = Assert.IsType<WireToGateHeldRecoveryCommandPrompt>(
            afterRestart.Business.HeldRecoveryCommand);
        Assert.Equal([1], held.OpenedSlots);
        Assert.Contains("部分仓门已开过", held.Text, StringComparison.Ordinal);
        Assert.Contains("已开过的仓=1", string.Join('\n', afterRestart.Logger.Entries.Select(entry => entry.Message)),
            StringComparison.Ordinal);

        Assert.True(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));

        JsonElement result = await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("UNKNOWN", result.GetProperty("overallOutcome").GetString());
        JsonElement[] slots = [.. result.GetProperty("slotResults").EnumerateArray()];
        Assert.Equal("COMPLETED", slots.Single(slot => slot.GetProperty("slotNo").GetInt32() == 1)
            .GetProperty("outcome").GetString());
        Assert.Equal("NOT_STARTED", slots.Single(slot => slot.GetProperty("slotNo").GetInt32() == 2)
            .GetProperty("outcome").GetString());
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        WireToGateOperatorEvent declined = await WaitForEventAsync(afterRestart, "RECOVERY_COMMAND_DECLINED", token);
        Assert.Contains("部分仓门已开过", declined.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The entry on the screen: the view model, wired the way the App wires it, shows both buttons and the notice for
    /// the held command, and its confirm runs the command.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task TheScreenOffersBothButtonsForTheHeldCommandAndItsConfirmRunsIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        await WaitForHeldAsync(afterRestart, token);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);

        // The same method App.xaml.cs calls, not a copy of its lines.
        HeldRecoveryCommandWiring.Configure(viewModel, afterRestart.Business);

        HeldRecoveryCommandDisplay shown = viewModel.HeldRecoveryCommand;
        Assert.True(shown.CanConfirm);
        Assert.True(shown.CanDecline);
        Assert.True(shown.HasNotice);
        Assert.Contains("补偿清空", shown.NoticeText, StringComparison.Ordinal);
        Assert.Contains("确认执行", shown.NoticeText, StringComparison.Ordinal);

        Assert.True(await viewModel.ConfirmHeldRecoveryCommandAsync(shown.Prompt!, token));

        await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.True(afterRestart.Io.UnlockCount > 0, "the confirmed compensation has to pulse the slots holding cargo");
        Assert.Equal(HeldRecoveryCommandDisplay.Empty, viewModel.HeldRecoveryCommand);
    }

    /// <summary>
    /// A latched severe safety fault hides 「确认执行」 -- it would open doors -- and leaves 「不执行」: it opens nothing, and
    /// is then the only way the server's session can end (onboard-hmi#239 review).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALatchHidesConfirmAndLeavesDeclineForTheHeldCommand()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        await WaitForHeldAsync(afterRestart, token);
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);
        HeldRecoveryCommandWiring.Configure(viewModel, afterRestart.Business);
        Assert.True(viewModel.HeldRecoveryCommand.CanConfirm);

        controller.EnterFatalFault("UI_COMMAND_FAILED", OnboardFatalFaultBanner.UiCommandFailed);
        viewModel.RefreshWireToGateInputState();

        HeldRecoveryCommandDisplay shown = viewModel.HeldRecoveryCommand;
        Assert.False(shown.CanConfirm);
        Assert.True(shown.CanDecline);
        Assert.True(shown.HasNotice);

        Assert.Equal(
            HeldRecoveryDeclineOutcome.Answered,
            await viewModel.DeclineHeldRecoveryCommandAsync(shown.Prompt!, token));
        JsonElement result = await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.Equal(HeldRecoveryCommandDisplay.Empty, viewModel.HeldRecoveryCommand);
    }

    /// <summary>
    /// A decline whose answer cannot be written to the outbox keeps the command held: the server is still waiting, so
    /// the buttons stay and the screen says why. Pressed again once the journal takes it, it is answered.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ADeclineWhoseAnswerCannotBeWrittenKeepsTheCommandHeld()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        FailingResultWriteJournal? journal = null;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(
            token,
            wrapJournal: inner => journal = new FailingResultWriteJournal(inner));
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);
        journal!.FailTheNextResultWrite();

        Assert.False(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));

        Assert.Equal(1, journal.FailedWrites);
        Assert.Equal(held, afterRestart.Business.HeldRecoveryCommand);
        Assert.True(afterRestart.Business.CanDeclineHeldRecoveryCommand);
        Assert.Contains(afterRestart.OperatorEvents, item =>
            item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains("没能写入发件箱", StringComparison.Ordinal));
        Assert.DoesNotContain(afterRestart.OperatorEvents, item => item.Kind == "RECOVERY_COMMAND_DECLINED");
        Assert.DoesNotContain(
            vehicle.After.ReceivedEnvelopes,
            envelope => envelope.MessageType == "LoadCompensationResult");

        Assert.True(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));
        JsonElement result = await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Null(afterRestart.Business.HeldRecoveryCommand);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>Confirming does not skip the motion check: a vehicle whose motion is unknown still opens nothing.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AConfirmedHeldCompensationStillRequiresAVehicleStandingStill()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);

        afterRestart.VehicleMotionUnknown();
        Assert.True(await afterRestart.Business.ConfirmHeldRecoveryCommandAsync(held, token));

        JsonElement result = await afterRestart.WaitForResultAsync("LoadCompensationResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// The session the held command belongs to closes: the command is void, both buttons go, and the screen says why.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AHeldCommandIsVoidedWhenItsSessionCloses()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCompensationCommandAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);
        WireToGateRecoveryState state = await afterRestart.ReadRecoveryStateAsync(token);

        await SendClosedSnapshotAsync(
            afterRestart,
            Guid.NewGuid().ToString("D"),
            state.RecoveryVector!.ExceptionRecoverySessionId!,
            CompensateLoadAction,
            state.RecoveryVector.Slots);

        WireToGateOperatorEvent voided = await WaitForEventAsync(afterRestart, "RECOVERY_COMMAND_HELD_VOIDED", token);
        Assert.Contains("服务端恢复会话已关闭", voided.Message, StringComparison.Ordinal);
        Assert.Null(afterRestart.Business.HeldRecoveryCommand);
        Assert.False(afterRestart.Business.CanConfirmHeldRecoveryCommand);
        Assert.False(afterRestart.Business.CanDeclineHeldRecoveryCommand);
        Assert.False(await afterRestart.Business.ConfirmHeldRecoveryCommandAsync(held, token));
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// Within one process too, a command replayed more than five minutes after the press is held: the same window
    /// hmi#236 put on asking again by itself.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task PastTheWindowACompensationCommandReplayedInTheSameProcessIsHeld()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        // Runs one window behind the real time and then catches up, rather than running ahead: the vehicle's safety
        // signal and IO readings are stamped with the real time, and a business clock left five minutes ahead of them
        // refuses the command as stale (VEHICLE_NOT_READY) whether it is held or not -- which made this test green on
        // the base for the wrong reason.
        TimeSpan lag = WireToGateBusinessService.AuthorizationResendWindow + TimeSpan.FromSeconds(1);
        RealTimeLaggingClock clock = new(lag);
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server =>
            {
                server.RecoverySlotOperationAttemptId = AttemptId;
                server.SendLoadCompensationCommandOnRequest = true;
                server.LoadCompensationCommandsToLose = 1;
            },
            cargoInTargetSlots: true,
            loadAlreadySettled: true,
            lockerWaitTimesOut: true,
            businessClock: clock);

        Assert.True(await harness.Business.RequestLoadCompensationAsync(
            "现场确认装货无法继续，申请补偿清空目标仓位。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => !harness.Session.Current.Connected,
            "the vehicle to see the connection the command went down with drop",
            token);
        clock.Advance(lag);
        await harness.Session.Client.ConnectAndRecoverAsync(token);

        await WaitForHeldOrExecutedAsync(harness, "LoadCompensationResult", token);
        Assert.Equal(0, harness.Io.UnlockCount);
        Assert.Contains("已超过 5 分钟", harness.Business.HeldRecoveryCommand!.Trigger, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ALoadCorrectionCommandReplayedAfterARestartOpensNothingUntilConfirmed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCorrectionRequestAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateRecoveryVectorContext vector = (await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector!;
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCorrection, vector.VectorType);

        // Where the server's replay of the correction command it authorized before the restart would come.
        await SendCorrectionCommandAsync(vehicle.After, vector, Guid.NewGuid().ToString("D"));

        await WaitForHeldOrExecutedAsync(afterRestart, "LoadCorrectionResult", token);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        WireToGateHeldRecoveryCommandPrompt held = Assert.IsType<WireToGateHeldRecoveryCommandPrompt>(
            afterRestart.Business.HeldRecoveryCommand);
        Assert.Equal(WireToGateRecoveryVectorTypes.LoadCorrection, held.VectorType);
        Assert.Contains("装货修正", held.Text, StringComparison.Ordinal);

        Assert.True(await afterRestart.Business.ConfirmHeldRecoveryCommandAsync(held, token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Io.UnlockCount > 0,
            "the confirmed correction to pulse its slots",
            token);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AFaultCargoCommandReplayedAfterARestartOpensNothingUntilConfirmed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        // The command the accepted action earns never reaches the vehicle before it restarts.
        server.SendRecoveryVectorCommandAfterRecoveryAction = false;
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true))
        {
            Assert.True(await beforeRestart.Business.RequestFaultCargoHandoffAsync(
                "现场确认故障仓货物需要交接处理。", token));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => beforeRestart.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector
                    is { VectorType: WireToGateRecoveryVectorTypes.FaultCargoHandoff },
                "the accepted fault cargo handoff to be prepared on disk",
                token);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);
        await WaitForSessionToCarryCommandsAsync(afterRestart, token);
        WireToGateRecoveryVectorContext vector = (await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector!;

        await serverAfterRestart.SendCommandAsync(
            "FaultCargoRecoveryCommand",
            Guid.NewGuid().ToString("D"),
            new
            {
                exceptionRecoverySessionId = vector.ExceptionRecoverySessionId,
                recoveryActionId = vector.PrimaryId,
                demandId = vector.DemandId,
                slots = vector.Slots,
                handoffId = vector.HandoffId,
                commandContentSha256 = FakeControlServerIdentifiers.RecoveryActionContentSha256(
                    vector.PrimaryId, vector.DemandId, AttemptId, [.. vector.Slots], 0)
            });

        await WaitForHeldOrExecutedAsync(afterRestart, "FaultCargoRecoveryResult", token);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        WireToGateHeldRecoveryCommandPrompt held = Assert.IsType<WireToGateHeldRecoveryCommandPrompt>(
            afterRestart.Business.HeldRecoveryCommand);
        Assert.Contains("故障仓货物交接", held.Text, StringComparison.Ordinal);

        Assert.True(await afterRestart.Business.ConfirmHeldRecoveryCommandAsync(held, token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Io.UnlockCount > 0,
            "the confirmed fault cargo handoff to pulse its slots",
            token);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeCommandReplayedAfterARestartOpensNothingUntilConfirmedOrDeclined()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        WireToGateRecoveryState opened;
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath))
        {
            opened = await OpenResumeActionAsync(beforeRestart, token);
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            lockerWaitTimesOut: true);
        await WaitForSessionToCarryCommandsAsync(afterRestart, token);
        WireToGateRecoveryState state = await afterRestart.ReadRecoveryStateAsync(token);
        Assert.Equal(opened.RecoveryActionId, state.RecoveryActionId);

        await serverAfterRestart.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));

        string resumeResultId = FakeControlServerIdentifiers.StableUuid(
            $"recovery-operation-result:{AttemptId}:{state.RecoveryActionId}");
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Io.UnlockCount > 0
                || Rejections(afterRestart).Count > 0
                || serverAfterRestart.ReceivedEnvelopes.Any(envelope => envelope.MessageId == resumeResultId)
                || afterRestart.OperatorEvents.Any(item => item.Kind == HeldRecoveryCommandKind),
            "the replayed resume to be held for confirmation, executed, or refused",
            token);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.Empty(Rejections(afterRestart));
        WireToGateHeldRecoveryCommandPrompt held = Assert.IsType<WireToGateHeldRecoveryCommandPrompt>(
            afterRestart.Business.HeldRecoveryCommand);
        Assert.Contains("修复后续行", held.Text, StringComparison.Ordinal);

        Assert.True(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));
        JsonElement rejection = await WaitForSingleRejectionAsync(afterRestart, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        Assert.Equal(
            "VEHICLE_NOT_READY",
            rejection.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// Declining a held correction costs more than the session -- the server keeps the demand blocked and never
    /// corrects that load again -- so it takes two presses: the first only shows the cost, the second answers
    /// <c>FAILED</c>, every slot <c>VEHICLE_NOT_READY</c>, and opens nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ADeclinedHeldCorrectionShowsWhatItCostsAndIsAnsweredFailedOnTheSecondPress()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCorrectionRequestAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateRecoveryVectorContext vector = (await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector!;
        await SendCorrectionCommandAsync(vehicle.After, vector, Guid.NewGuid().ToString("D"));
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);
        Assert.False(afterRestart.Business.HeldRecoveryCommandView.DeclineArmed);

        Assert.False(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));

        WireToGateOperatorEvent cost = await WaitForEventAsync(
            afterRestart, "RECOVERY_COMMAND_DECLINE_CONFIRMATION", token);
        Assert.Contains("这条装货的修正将作废，需求进入恢复，需要管理员开恢复会话处理", cost.Message, StringComparison.Ordinal);
        Assert.Contains("AGV-8005-01", cost.Message, StringComparison.Ordinal);
        Assert.Contains(vector.DemandId, cost.Message, StringComparison.Ordinal);
        Assert.Contains("1、2号仓", cost.Message, StringComparison.Ordinal);
        Assert.True(afterRestart.Business.HeldRecoveryCommandView.DeclineArmed);
        Assert.Empty(afterRestart.ResultsOfType("LoadCorrectionResult"));

        Assert.True(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));

        JsonElement result = await afterRestart.WaitForResultAsync("LoadCorrectionResult", token);
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.All(result.GetProperty("slotResults").EnumerateArray(), slot =>
        {
            Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString());
            Assert.Contains(
                "VEHICLE_NOT_READY",
                slot.GetProperty("reasonCodes").EnumerateArray().Select(code => code.GetString()));
        });
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        await WaitForEventAsync(afterRestart, "RECOVERY_COMMAND_DECLINED", token);
        Assert.Null((await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector);
    }

    /// <summary>
    /// The second press counts only for the command the first one showed the cost of: a newer copy held in between
    /// starts over, and the press the operator made for the old one answers nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AHeldCorrectionReplacedBetweenTheTwoDeclinePressesHasToBeConfirmedAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RestartedVehicle vehicle = await RestartAfterALostCorrectionRequestAsync(token);
        RecoveryVectorHarness afterRestart = vehicle.Harness;
        WireToGateRecoveryVectorContext vector = (await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector!;
        await SendCorrectionCommandAsync(vehicle.After, vector, Guid.NewGuid().ToString("D"));
        WireToGateHeldRecoveryCommandPrompt first = await WaitForHeldAsync(afterRestart, token);
        Assert.False(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(first, token));
        Assert.True(afterRestart.Business.HeldRecoveryCommandView.DeclineArmed);

        // The server sends the command again under a messageId of its own: held afresh, armed for nothing.
        await SendCorrectionCommandAsync(vehicle.After, vector, Guid.NewGuid().ToString("D"));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.HeldRecoveryCommand is { } now
                && now.CommandMessageId != first.CommandMessageId,
            "the newer copy of the correction command to be held",
            token);
        WireToGateHeldRecoveryCommandPrompt second = afterRestart.Business.HeldRecoveryCommand!;
        Assert.False(afterRestart.Business.HeldRecoveryCommandView.DeclineArmed);

        Assert.False(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(first, token));
        Assert.False(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(second, token));

        Assert.True(afterRestart.Business.HeldRecoveryCommandView.DeclineArmed);
        Assert.Empty(afterRestart.ResultsOfType("LoadCorrectionResult"));
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// The same for a resume: a decline whose rejection cannot be written to the outbox keeps the command held, and says
    /// so, instead of reporting an answer the server never got (onboard-hmi#239 incremental review S-1).
    /// </summary>
    /// <remarks>
    /// The rejection forgets the vehicle's recovery session before it writes the rejection, on purpose
    /// (<c>SendResumeRejectedAsync</c>), so the hold cannot be judged by the session still being on file: that change is
    /// the decline's own. The review's probe on the head before this: answered=True, rejectionsAtServer=0.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ADeclinedResumeWhoseRejectionCannotBeWrittenKeepsTheCommandHeld()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath))
        {
            await OpenResumeActionAsync(beforeRestart, token);
        }

        FailingResultWriteJournal? journal = null;
        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            lockerWaitTimesOut: true,
            wrapJournal: inner => journal = new FailingResultWriteJournal(inner, "slot-operation-resume-rejected:"));
        await WaitForSessionToCarryCommandsAsync(afterRestart, token);
        WireToGateRecoveryState state = await afterRestart.ReadRecoveryStateAsync(token);
        await serverAfterRestart.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);
        journal!.FailTheNextResultWrite();

        Assert.False(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));

        Assert.Equal(1, journal.FailedWrites);
        Assert.Empty(Rejections(afterRestart));
        Assert.Equal(held.CommandMessageId, afterRestart.Business.HeldRecoveryCommand?.CommandMessageId);
        Assert.True(afterRestart.Business.CanDeclineHeldRecoveryCommand);
        Assert.Contains(afterRestart.OperatorEvents, item =>
            item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains("没能写入发件箱", StringComparison.Ordinal));
        Assert.DoesNotContain(afterRestart.OperatorEvents, item => item.Kind == "RECOVERY_COMMAND_DECLINED");
        Assert.DoesNotContain(afterRestart.OperatorEvents, item => item.Kind == "RECOVERY_COMMAND_HELD_VOIDED");

        Assert.True(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(
            afterRestart.Business.HeldRecoveryCommand!, token));
        JsonElement rejection = await WaitForSingleRejectionAsync(afterRestart, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        Assert.Equal(
            "VEHICLE_NOT_READY",
            rejection.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Null(afterRestart.Business.HeldRecoveryCommand);
        Assert.Equal(0, afterRestart.Io.UnlockCount);
    }

    /// <summary>
    /// Confirm pressed after a decline whose rejection could not be written still answers as the decline did: the
    /// rejection reads <c>VEHICLE_NOT_READY</c>, and nothing opens (onboard-hmi#239, coordinator after S-1).
    /// </summary>
    /// <remarks>
    /// The decline has already forgotten the vehicle's recovery session, so carried out as a confirm the resume fails the
    /// gate as <c>RECOVERY_AUTHENTICATION_FAILED</c> -- which the server's audit would keep as an authentication failure
    /// nobody had. Probe on <c>bc49f45</c>: reason=RECOVERY_AUTHENTICATION_FAILED unlocks=0 held=False.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AConfirmAfterADeclinedResumeWhoseRejectionFailedStillAnswersAsTheDecline()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = NewRestartJournalPath();
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath))
        {
            await OpenResumeActionAsync(beforeRestart, token);
        }

        FailingResultWriteJournal? journal = null;
        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            lockerWaitTimesOut: true,
            wrapJournal: inner => journal = new FailingResultWriteJournal(inner, "slot-operation-resume-rejected:"));
        await WaitForSessionToCarryCommandsAsync(afterRestart, token);
        WireToGateRecoveryState state = await afterRestart.ReadRecoveryStateAsync(token);
        await serverAfterRestart.SendCommandAsync("SlotOperationResumeCommand", ResumeMessageId, ResumePayload(state));
        WireToGateHeldRecoveryCommandPrompt held = await WaitForHeldAsync(afterRestart, token);
        journal!.FailTheNextResultWrite();
        Assert.False(await afterRestart.Business.DeclineHeldRecoveryCommandAsync(held, token));
        Assert.True(afterRestart.Business.CanConfirmHeldRecoveryCommand);

        Assert.True(await afterRestart.Business.ConfirmHeldRecoveryCommandAsync(
            afterRestart.Business.HeldRecoveryCommand!, token));

        JsonElement rejection = await WaitForSingleRejectionAsync(afterRestart, token);
        Assert.Equal(ResumeMessageId, rejection.GetProperty("correlationId").GetString());
        Assert.Equal(
            "VEHICLE_NOT_READY",
            rejection.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(0, afterRestart.Io.UnlockCount);
        Assert.Null(afterRestart.Business.HeldRecoveryCommand);
        Assert.Contains(afterRestart.OperatorEvents, item =>
            item.Kind == "RECOVERY_BLOCKED" && item.Message.Contains("之前已经选择了不执行", StringComparison.Ordinal));
        Assert.Contains(afterRestart.OperatorEvents, item => item.Kind == "RECOVERY_COMMAND_DECLINED");
    }

    private static async Task WaitForHeldOrExecutedAsync(
        RecoveryVectorHarness harness,
        string resultType,
        CancellationToken token) =>
        // Either way the command ends this wait: held and shown, or executed -- which shows as pulses or a result.
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Io.UnlockCount > 0
                || harness.ResultsOfType(resultType).Count > 0
                || harness.OperatorEvents.Any(item => item.Kind == HeldRecoveryCommandKind),
            "the replayed command to be held for confirmation or executed",
            token);

    private static async Task<WireToGateHeldRecoveryCommandPrompt> WaitForHeldAsync(
        RecoveryVectorHarness harness,
        CancellationToken token)
    {
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.HeldRecoveryCommand is not null,
            "the replayed command to be held for confirmation",
            token);
        return harness.Business.HeldRecoveryCommand!;
    }

    private static async Task<WireToGateOperatorEvent> WaitForEventAsync(
        RecoveryVectorHarness harness,
        string kind,
        CancellationToken token)
    {
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.OperatorEvents.Any(item => item.Kind == kind),
            $"the operator to be shown {kind}",
            token);
        return harness.OperatorEvents.First(item => item.Kind == kind);
    }

    private static Task WaitForSessionToCarryCommandsAsync(RecoveryVectorHarness harness, CancellationToken token) =>
        RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Session.Current is
            {
                Connected: true,
                Readiness: WireToGateSessionReadiness.Ready or WireToGateSessionReadiness.RecoveryRequired
            },
            "the restarted vehicle's session to be able to carry a command",
            token);

    private static string NewRestartJournalPath() =>
        Path.Combine(Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");

    /// <summary>
    /// A load correction pressed, its request lost with the link, and the vehicle restarted: the restarted vehicle has
    /// the correction prepared, no command bound, and cargo in the slots -- a correction's precheck refuses empty ones
    /// before any pulse. The test sends the command itself, where the server's replay would come.
    /// </summary>
    private static async Task<RestartedVehicle> RestartAfterALostCorrectionRequestAsync(CancellationToken token)
    {
        string journalPath = NewRestartJournalPath();
        FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.LoadCorrectionRequestsToLose = 1;
        await using (RecoveryVectorHarness first = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            await RecoveryVectorHarness.WaitUntilAsync(
                () => first.Business.CanRequestLoadCorrection,
                "the load correction entry to be offered",
                token);
            Assert.True(await first.Business.RequestLoadCorrectionAsync(
                "现场确认需要修正已完成的装货结果。", token));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => !first.Session.Current.Connected,
                "the vehicle to see the connection the correction request went down with drop",
                token);
        }

        FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true);
        await WaitForSessionToCarryCommandsAsync(afterRestart, token);
        return new RestartedVehicle(server, serverAfterRestart, afterRestart);
    }

    private static Task SendCorrectionCommandAsync(
        FakeControlServer server,
        WireToGateRecoveryVectorContext vector,
        string messageId) =>
        server.SendCommandAsync(
            "LoadCorrectionCommand",
            messageId,
            new
            {
                correctionId = vector.PrimaryId,
                demandId = vector.DemandId,
                slotOperationAttemptId = vector.SlotOperationAttemptId,
                slots = vector.Slots,
                expectedSequence = CorrectionSequence,
                commandContentSha256 = WireToGateRecoveryCommandHash.ForLoadCorrection(
                    vector.PrimaryId, vector.DemandId, vector.SlotOperationAttemptId!, vector.Slots)
            });

    /// <summary>
    /// A compensation pressed and authorized, its command lost on the way back, and the vehicle restarted before the
    /// link came back: the restarted vehicle meets the same server, which replays the command in the handshake.
    /// </summary>
    /// <param name="beforeRestart">What the journal is left holding when the process goes, if not the prepared vector.</param>
    /// <param name="replayInHandshake">
    /// Off, the restarted server does not replay the command by itself, and the test sends it where the replay would
    /// come.
    /// </param>
    /// <param name="wrapJournal">Wraps the restarted vehicle's journal.</param>
    private static async Task<RestartedVehicle> RestartAfterALostCompensationCommandAsync(
        CancellationToken token,
        Func<WireToGateRecoveryState, WireToGateRecoveryState>? beforeRestart = null,
        bool replayInHandshake = true,
        Func<IWireToGateJournal, IWireToGateJournal>? wrapJournal = null)
    {
        string journalPath = NewRestartJournalPath();
        FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.SendLoadCompensationCommandOnRequest = true;
        server.LoadCompensationCommandsToLose = 1;
        await using (RecoveryVectorHarness first = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            loadAlreadySettled: true))
        {
            Assert.True(await first.Business.RequestLoadCompensationAsync(
                "现场确认装货无法继续，申请补偿清空目标仓位。", token));
            await RecoveryVectorHarness.WaitUntilAsync(
                () => !first.Session.Current.Connected,
                "the vehicle to see the connection the command went down with drop",
                token);
            Assert.Equal(0, first.Io.UnlockCount);
            if (beforeRestart is not null)
            {
                await first.RewriteRecoveryStateAsync(beforeRestart, token);
            }
        }

        FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        serverAfterRestart.SendLoadCompensationCommandOnRequest = replayInHandshake;
        RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            restart: true,
            cargoInTargetSlots: true,
            lockerWaitTimesOut: true,
            wrapJournal: wrapJournal);
        return new RestartedVehicle(server, serverAfterRestart, afterRestart);
    }

    /// <summary>
    /// Fails the next outbox write whose deduplication key starts with <paramref name="keyPrefix"/>, once armed, with an
    /// <see cref="IOException"/>, and lets every other call through.
    /// </summary>
    private sealed class FailingResultWriteJournal(IWireToGateJournal inner, string keyPrefix = "recovery-vector-result:")
        : IWireToGateJournal
    {
        private int _armed;
        private int _failed;

        public int FailedWrites => Volatile.Read(ref _failed);

        public void FailTheNextResultWrite() => Volatile.Write(ref _armed, 1);

        public Task<WireToGateDurableMessage> SaveOutgoingBeforeSendAsync(
            WireToGateDurableMessage message,
            CancellationToken cancellationToken = default)
        {
            if (message.DeduplicationKey.StartsWith(keyPrefix, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Interlocked.Increment(ref _failed);
                throw new IOException("injected: the outbox could not be written");
            }

            return inner.SaveOutgoingBeforeSendAsync(message, cancellationToken);
        }

        public Task<WireToGateRecoveryState> ReadRecoveryStateAsync(CancellationToken cancellationToken = default) =>
            inner.ReadRecoveryStateAsync(cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, cancellationToken);

        public Task<WireToGateRecoveryState?> UpdateRecoveryStateAsync(
            Func<WireToGateRecoveryState, WireToGateRecoveryState?> change,
            Action<WireToGateRecoveryState> settled,
            CancellationToken cancellationToken = default) =>
            inner.UpdateRecoveryStateAsync(change, settled, cancellationToken);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task<string> ReadJournalEpochAsync(CancellationToken cancellationToken = default) =>
            inner.ReadJournalEpochAsync(cancellationToken);

        public Task<WireToGateDurableMessage> ReplaceOutgoingForReplayAsync(
            WireToGateDurableMessage expected,
            WireToGateDurableMessage replacement,
            CancellationToken cancellationToken = default) =>
            inner.ReplaceOutgoingForReplayAsync(expected, replacement, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByDeduplicationKeyAsync(
            string deduplicationKey,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByDeduplicationKeyAsync(deduplicationKey, cancellationToken);

        public Task<WireToGateDurableMessage?> ReadOutgoingByMessageIdAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ReadOutgoingByMessageIdAsync(messageId, cancellationToken);

        public Task MarkOutgoingAcknowledgedAsync(
            string messageId,
            string acceptedContentSha256,
            CancellationToken cancellationToken = default) =>
            inner.MarkOutgoingAcknowledgedAsync(messageId, acceptedContentSha256, cancellationToken);

        public Task<IReadOnlyList<WireToGateDurableMessage>> ReadUnacknowledgedOutgoingAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadUnacknowledgedOutgoingAsync(cancellationToken);

        public Task<IReadOnlyList<WireToGateAppliedJourneySnapshot>> ReadAppliedJourneySnapshotsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadAppliedJourneySnapshotsAsync(cancellationToken);

        public Task<WireToGateAppliedJourneySnapshot> SaveAppliedJourneySnapshotAsync(
            WireToGateAppliedJourneySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            inner.SaveAppliedJourneySnapshotAsync(snapshot, cancellationToken);

        public Task<string> ComputeContentSha256Async(CancellationToken cancellationToken = default) =>
            inner.ComputeContentSha256Async(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed record RestartedVehicle(
        FakeControlServer Before,
        FakeControlServer After,
        RecoveryVectorHarness Harness) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Harness.DisposeAsync();
            await After.DisposeAsync();
            await Before.DisposeAsync();
        }
    }
}

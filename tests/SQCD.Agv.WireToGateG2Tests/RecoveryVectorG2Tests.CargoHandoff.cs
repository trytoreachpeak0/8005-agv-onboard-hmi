using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The cargo handoff record a forced mechanical recovery on a demand reports (protocol 3.0.0
/// <c>cargoHandoff</c>, REQ-0242, 8005-agv-onboard-hmi#216): taken at the confirmation, written to the
/// journal before the result is built, sent only from the journal, checked against the demand's SUBLOT
/// first, and replayed unchanged after a restart.
/// </summary>
public sealed partial class RecoveryVectorG2Tests
{
    private const string ForcedClosedSnapshotMessageId = "abcdabcd-0000-4000-8000-000000000216";

    /// <summary>
    /// REPORT_CARGO_HANDOFF_RECORD_IN_RESULT: the record on the wire is the record the journal holds --
    /// every field, the handover time included -- not a constant and not what the screen holds now.
    /// </summary>
    /// <remarks>
    /// The acknowledgement is withheld so the journal still holds the authorized vector, and with it the
    /// record, when the wire is compared with it; an acknowledged isolation settles the vector away.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task TheHandoffRecordOnTheWireIsTheOneTheJournalHolds()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.WithholdForcedMechanicalRecoveryResultAck = true,
            cargoInTargetSlots: true);
        await AuthorizeForcedRecoveryAsync(harness, token);
        await harness.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-ON-FILE-7", token);

        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-ON-FILE-7", "收货员李二", token));
        JsonElement result = await harness.WaitForResultAsync("ForcedMechanicalRecoveryResult", token);

        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        WireToGateForcedCargoHandoff onFile = state.RecoveryVector!.CargoHandoff!;
        JsonElement wire = result.GetProperty("cargoHandoff");
        Assert.Equal(onFile.Sublot, wire.GetProperty("sublot").GetString());
        Assert.Equal(onFile.ReceiverName, wire.GetProperty("receiverName").GetString());
        Assert.Equal(onFile.HandedOverAt, wire.GetProperty("handedOverAt").GetDateTimeOffset());
        Assert.Equal("SUBLOT-ON-FILE-7", onFile.Sublot);
        Assert.Equal("收货员李二", onFile.ReceiverName);
        Assert.Equal(DemandId, result.GetProperty("demandId").GetString());
        Assert.Equal(state.RecoveryResultObservedAt, result.GetProperty("observedAt").GetDateTimeOffset());
        // The record proves neither (REQ-0242): a named receiver does not make a slot empty or the vehicle ready.
        Assert.False(result.GetProperty("electronicEmptyProven").GetBoolean());
        Assert.False(result.GetProperty("vehicleReadyProven").GetBoolean());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A confirmation missing either half of the record is refused before anything is written: no stamp,
    /// no record, nothing on the wire, and the operator told what is missing.
    /// </summary>
    [Theory]
    [InlineData("", "收货员李二")]
    [InlineData("SUBLOT-001", "  ")]
    [InlineData(null, null)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AnIncompleteHandoffRecordIsRefusedAndNothingIsWritten(string? sublot, string? receiver)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await AuthorizeForcedRecoveryAsync(harness, token);

        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(sublot, receiver, token));

        await harness.WaitForRecoveryBlockedAsync("FORCED_RECOVERY_HANDOFF_RECORD_REQUIRED", token);
        await AssertNoForcedResultForAWhileAsync(harness, token);
        WireToGateRecoveryState state = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(state.RecoveryResultObservedAt);
        Assert.Null(state.RecoveryVector!.CargoHandoff);
        Assert.True(harness.Business.ForcedConfirmationNeedsCargoHandoff);
    }

    /// <summary>
    /// CONFIRM_SUBLOT_AGAINST_DEMAND_BEFORE_SENDING: a SUBLOT that is not the demand's is not sent on the
    /// first press -- the operator is warned, nothing is written -- and is sent on the second press with the
    /// same SUBLOT, because the slots are already isolated by hand and the result must not be held for good.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ASublotThatIsNotTheDemandsIsSentOnlyWhenTheOperatorConfirmsItAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await AuthorizeForcedRecoveryAsync(harness, token);
        await harness.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-DEMAND-1", token);

        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-OTHER-9", "收货员李二", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CurrentOperationSnapshot?.Guidance.Contains(
                "子批号 SUBLOT-OTHER-9 与需求的子批号 SUBLOT-DEMAND-1 不一致", StringComparison.Ordinal) == true,
            "the operation line to name both SUBLOTs",
            token);
        await AssertNoForcedResultForAWhileAsync(harness, token);
        Assert.Null((await harness.ReadRecoveryStateAsync(token)).RecoveryVector!.CargoHandoff);

        // The second press counts only for the same SUBLOT: changing it in between -- even back to the one
        // already warned about -- starts from the warning again.
        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-OTHER-8", "收货员李二", token));
        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-OTHER-9", "收货员李二", token));
        await AssertNoForcedResultForAWhileAsync(harness, token);

        Assert.True(await harness.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-OTHER-9", "收货员李二", token));
        JsonElement result = await harness.WaitForResultAsync("ForcedMechanicalRecoveryResult", token);
        Assert.Equal("SUBLOT-OTHER-9", result.GetProperty("cargoHandoff").GetProperty("sublot").GetString());
        Assert.Single(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
    }

    /// <summary>
    /// The same check when the SUBLOT cannot be checked -- the worklist does not name the demand, as after a
    /// reconnect clears the journey projection -- and a changed SUBLOT on the second press is a new warning,
    /// not the confirmation of the first.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ASublotThatCannotBeCheckedIsWarnedAboutAndAChangedOneIsWarnedAboutAgain()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await AuthorizeForcedRecoveryAsync(harness, token);
        Assert.Null(harness.Session.CurrentJourney.CurrentStopWorklist);

        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync("SUBLOT-A", "收货员李二", token));
        Assert.Contains("无法核对子批号", harness.Business.CurrentOperationSnapshot!.Guidance, StringComparison.Ordinal);
        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync("SUBLOT-B", "收货员李二", token));
        await AssertNoForcedResultForAWhileAsync(harness, token);

        Assert.True(await harness.Business.ConfirmForcedMechanicalRecoveryAsync("SUBLOT-B", "收货员李二", token));
        JsonElement result = await harness.WaitForResultAsync("ForcedMechanicalRecoveryResult", token);
        Assert.Equal("SUBLOT-B", result.GetProperty("cargoHandoff").GetProperty("sublot").GetString());
    }

    /// <summary>
    /// REPLAY_SAME_HANDOFF_RECORD_AFTER_RESTART, at the named crash point: the record is on disk and the
    /// result sent, the acknowledgement never came, the vehicle restarts. The next session carries the same
    /// result -- same messageId, same record -- and a press after the restart with other text on the screen
    /// still sends the record on file.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AHandoffRecordOnFileIsReplayedUnchangedAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.WithholdForcedMechanicalRecoveryResultAck = true;
        JsonElement before;
        string beforeMessageId;

        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true))
        {
            await AuthorizeForcedRecoveryAsync(beforeRestart, token);
            await beforeRestart.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-001", token);
            Assert.False(await beforeRestart.Business.ConfirmForcedMechanicalRecoveryAsync(
                "SUBLOT-001", "收货员李二", token));
            await beforeRestart.WaitForInboundAsync("ForcedMechanicalRecoveryResult", token);
            using JsonDocument sent = JsonDocument.Parse(beforeRestart.ResultsOfType("ForcedMechanicalRecoveryResult")[0]);
            before = sent.RootElement.GetProperty("payload").GetProperty("cargoHandoff").Clone();
            beforeMessageId = sent.RootElement.GetProperty("messageId").GetString()!;
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            cargoInTargetSlots: true);
        await afterRestart.WaitForInboundAsync("ForcedMechanicalRecoveryResult", token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.CanConfirmForcedMechanicalRecovery,
            "the confirmation to be on offer again after the restart",
            token);
        Assert.False(afterRestart.Business.ForcedConfirmationNeedsCargoHandoff);
        Assert.Equal("SUBLOT-001", afterRestart.Business.ForcedCargoHandoffOnFile!.Sublot);

        // Other text on the screen now: the press sends the record on file, not this.
        Assert.True(await afterRestart.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-TYPED-LATER", "别人", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.PhysicallyUnknownSlots.Count > 0,
            "the acknowledged isolation to leave its slots physically unknown",
            token);

        IReadOnlyList<string> replayed = afterRestart.ResultsOfType("ForcedMechanicalRecoveryResult");
        Assert.NotEmpty(replayed);
        Assert.All(replayed, line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(beforeMessageId, document.RootElement.GetProperty("messageId").GetString());
            Assert.Equal(
                before.GetRawText(),
                document.RootElement.GetProperty("payload").GetProperty("cargoHandoff").GetRawText());
        });
    }

    /// <summary>
    /// The confirmation a build before this one held unreported (8005-agv-onboard-hmi#214: stamped, no
    /// record) is reported once the record is entered after the upgrade, under the observation time that
    /// build stamped -- the moment the people at the vehicle confirmed, not the moment the screen caught up.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AConfirmationTheEarlierBuildHeldIsReportedOnceItsHandoffRecordIsEntered()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        DateTimeOffset heldAt = new(2026, 9, 29, 8, 15, 0, TimeSpan.Zero);

        await using (RecoveryVectorHarness heldByTheEarlierBuild = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true))
        {
            await AuthorizeForcedRecoveryAsync(heldByTheEarlierBuild, token);
            await heldByTheEarlierBuild.RewriteRecoveryStateAsync(
                state => state with { RecoveryResultObservedAt = heldAt },
                token);
        }

        await using FakeControlServer serverAfterUpgrade = RecoveryVectorHarness.NewServer();
        serverAfterUpgrade.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterUpgrade = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterUpgrade,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            cargoInTargetSlots: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterUpgrade.Business.CurrentOperationSnapshot?.Guidance.Contains(
                "请填写货物交接记录", StringComparison.Ordinal) == true,
            "the restored line to ask for the handoff record, not for a second extraction",
            token);
        Assert.DoesNotContain("请先断电", afterUpgrade.Business.CurrentOperationSnapshot!.Guidance, StringComparison.Ordinal);
        Assert.True(afterUpgrade.Business.ForcedConfirmationNeedsCargoHandoff);

        await afterUpgrade.ConfirmForcedMechanicalRecoveryAsync(token);
        JsonElement result = await afterUpgrade.WaitForResultAsync("ForcedMechanicalRecoveryResult", token);
        Assert.Equal("MECHANICALLY_ISOLATED", result.GetProperty("outcome").GetString());
        Assert.Equal(heldAt, result.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(
            RecoveryVectorHarness.HandoffSublot,
            result.GetProperty("cargoHandoff").GetProperty("sublot").GetString());
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterUpgrade.Business.PhysicallyUnknownSlots.Count > 0,
            "the acknowledged isolation to leave its slots physically unknown",
            token);
    }

    /// <summary>
    /// KEEP_RECOVERY_CONTEXT_UNLESS_ISOLATION_ACKNOWLEDGED, against the session's CLOSED snapshot: a
    /// confirmed forced recovery -- people have opened the slots by hand -- is not a vector that "did
    /// nothing", so the CLOSED fallback keeps it, stamp and record included (hmi#224 review, #216).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task TheClosedSnapshotKeepsAConfirmedForcedRecoveryOnFile()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.WithholdForcedMechanicalRecoveryResultAck = true,
            cargoInTargetSlots: true);
        await AuthorizeForcedRecoveryAsync(harness, token);
        await harness.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-001", token);
        Assert.False(await harness.Business.ConfirmForcedMechanicalRecoveryAsync("SUBLOT-001", "收货员李二", token));
        WireToGateRecoveryState confirmed = await harness.ReadRecoveryStateAsync(token);
        Assert.NotNull(confirmed.RecoveryVector!.CargoHandoff);

        await SendForcedClosedSnapshotAsync(harness, confirmed);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => AcknowledgedRecoverySnapshots(harness).Contains(ForcedClosedSnapshotMessageId),
            "the CLOSED snapshot to be acknowledged",
            token);

        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(JsonSerializer.Serialize(confirmed.RecoveryVector), JsonSerializer.Serialize(after.RecoveryVector));
        Assert.Equal(confirmed.RecoveryResultObservedAt, after.RecoveryResultObservedAt);
        Assert.Equal(confirmed.ExceptionRecoverySessionId, after.ExceptionRecoverySessionId);
    }

    /// <summary>
    /// The other side of the same line, so the fix above cannot be a blanket "never forget a forced
    /// recovery": one that was authorized but never confirmed did nothing, and the CLOSED snapshot still
    /// forgets it and its session.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task TheClosedSnapshotStillForgetsAForcedRecoveryNobodyConfirmed()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            cargoInTargetSlots: true);
        await AuthorizeForcedRecoveryAsync(harness, token);
        WireToGateRecoveryState authorized = await harness.ReadRecoveryStateAsync(token);
        Assert.Null(authorized.RecoveryResultObservedAt);

        await SendForcedClosedSnapshotAsync(harness, authorized);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.ReadRecoveryStateAsync(token).GetAwaiter().GetResult().RecoveryVector is null,
            "the CLOSED snapshot to forget the unconfirmed forced recovery",
            token);
        Assert.Null((await harness.ReadRecoveryStateAsync(token)).ExceptionRecoverySessionId);
    }

    /// <summary>
    /// A warning given before a restart is not carried over: after the restart the same SUBLOT is warned
    /// about again rather than sent on the first press. Losing it costs one more press and never skips the
    /// check (coordinator on 8005-agv-onboard-hmi#216).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AWarningLostToARestartIsGivenAgainAndNeverSkipped()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();

        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true))
        {
            await AuthorizeForcedRecoveryAsync(beforeRestart, token);
            await beforeRestart.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-DEMAND-1", token);
            Assert.False(await beforeRestart.Business.ConfirmForcedMechanicalRecoveryAsync(
                "SUBLOT-OTHER-9", "收货员李二", token));
            Assert.Empty(beforeRestart.ResultsOfType("ForcedMechanicalRecoveryResult"));
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            cargoInTargetSlots: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.CanConfirmForcedMechanicalRecovery,
            "the confirmation to be on offer again after the restart",
            token);
        await afterRestart.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-DEMAND-1", token);

        Assert.False(await afterRestart.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-OTHER-9", "收货员李二", token));
        Assert.Contains("不一致", afterRestart.Business.CurrentOperationSnapshot!.Guidance, StringComparison.Ordinal);
        await AssertNoForcedResultForAWhileAsync(afterRestart, token);
        Assert.Null((await afterRestart.ReadRecoveryStateAsync(token)).RecoveryVector!.CargoHandoff);

        Assert.True(await afterRestart.Business.ConfirmForcedMechanicalRecoveryAsync(
            "SUBLOT-OTHER-9", "收货员李二", token));
        JsonElement result = await afterRestart.WaitForResultAsync("ForcedMechanicalRecoveryResult", token);
        Assert.Equal("SUBLOT-OTHER-9", result.GetProperty("cargoHandoff").GetProperty("sublot").GetString());
        Assert.DoesNotContain(server.ReceivedEnvelopes, envelope => envelope.MessageType == "ForcedMechanicalRecoveryResult");
    }

    /// <summary>
    /// <c>handedOverAt</c> is the moment the record was first written, and a resend after a restart keeps
    /// it: read back from the journal, not reset to the resend's now.
    /// </summary>
    /// <remarks>
    /// The clock is made to move first: the harness runs on the system clock, and the resend is pressed
    /// only once it reads at least two seconds past the stored time. Without that gap a reset to "now"
    /// could land on the same instant and this assertion would hold whatever the code did.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task TheHandoverTimeOnFileIsNotResetByAResendAfterARestart()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string journalPath = Path.Combine(
            Path.GetTempPath(), "w2g-vector", Guid.NewGuid().ToString("N"), "journal.db");
        await using FakeControlServer server = RecoveryVectorHarness.NewServer();
        server.WithholdForcedMechanicalRecoveryResultAck = true;
        DateTimeOffset handedOverAt;

        await using (RecoveryVectorHarness beforeRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: server,
            journalPath: journalPath,
            cargoInTargetSlots: true))
        {
            await AuthorizeForcedRecoveryAsync(beforeRestart, token);
            await beforeRestart.NameTheDemandOnTheWorklistAsync(DemandId, "SUBLOT-001", token);
            Assert.False(await beforeRestart.Business.ConfirmForcedMechanicalRecoveryAsync(
                "SUBLOT-001", "收货员李二", token));
            handedOverAt = (await beforeRestart.ReadRecoveryStateAsync(token)).RecoveryVector!.CargoHandoff!.HandedOverAt;
        }

        await using FakeControlServer serverAfterRestart = RecoveryVectorHarness.NewServer();
        serverAfterRestart.AdoptDurableRecoveryMemoryFrom(server);
        await using RecoveryVectorHarness afterRestart = await RecoveryVectorHarness.StartAsync(
            token,
            existingServer: serverAfterRestart,
            journalPath: journalPath,
            baselineRevision: 2,
            restart: true,
            cargoInTargetSlots: true);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => afterRestart.Business.CanConfirmForcedMechanicalRecovery,
            "the confirmation to be on offer again after the restart",
            token);
        await RecoveryVectorHarness.WaitUntilAsync(
            () => DateTimeOffset.UtcNow - handedOverAt >= TimeSpan.FromSeconds(2),
            "the clock to move at least two seconds past the stored handover time",
            token);

        Assert.Equal(handedOverAt, afterRestart.Business.ForcedCargoHandoffOnFile!.HandedOverAt);
        Assert.True(await afterRestart.Business.ConfirmForcedMechanicalRecoveryAsync(token));
        DateTimeOffset pressedAt = DateTimeOffset.UtcNow;

        IReadOnlyList<string> resent = afterRestart.ResultsOfType("ForcedMechanicalRecoveryResult");
        Assert.NotEmpty(resent);
        Assert.All(resent, line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            DateTimeOffset onTheWire = document.RootElement
                .GetProperty("payload").GetProperty("cargoHandoff").GetProperty("handedOverAt").GetDateTimeOffset();
            Assert.Equal(handedOverAt, onTheWire);
            Assert.True(pressedAt - onTheWire >= TimeSpan.FromSeconds(2), $"{onTheWire:o} vs press {pressedAt:o}");
        });
    }

    private static async Task AuthorizeForcedRecoveryAsync(RecoveryVectorHarness harness, CancellationToken token)
    {
        Assert.True(await harness.Business.RequestForcedMechanicalRecoveryAsync(
            "现场确认仓门无法电动解锁，申请强制机械恢复。", token));
        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.CanConfirmForcedMechanicalRecovery,
            "the authorized forced recovery to wait for the operator's confirmation",
            token);
    }

    /// <summary>Not one read: a result would reach the server a moment after the press returned.</summary>
    private static async Task AssertNoForcedResultForAWhileAsync(RecoveryVectorHarness harness, CancellationToken token)
    {
        for (int check = 0; check < 5; check++)
        {
            Assert.Empty(harness.ResultsOfType("ForcedMechanicalRecoveryResult"));
            await Task.Delay(100, token);
        }
    }

    private static Task SendForcedClosedSnapshotAsync(RecoveryVectorHarness harness, WireToGateRecoveryState state) =>
        harness.Server.SendCommandAsync(
            "ExceptionRecoverySessionSnapshot",
            ForcedClosedSnapshotMessageId,
            new
            {
                exceptionRecoverySessionId = state.ExceptionRecoverySessionId,
                recoverySessionRevision = 3,
                state = "CLOSED",
                administratorId = "maintenance-001",
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                eventId = state.RecoverySessionRequestId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                slots = state.RecoveryVector!.Slots,
                selectedAction = "FORCED_MECHANICAL_RECOVERY",
                allowedActions = Array.Empty<string>(),
                closedReason = (string?)null,
                blockingFacts = Array.Empty<object>()
            });
}

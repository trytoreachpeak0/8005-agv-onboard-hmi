using System.Text.Json;
using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A recovery command this end cannot bind is answered with a <c>FAILED</c> result, not with silence
/// (onboard-hmi#145 (b)).
/// </summary>
/// <remarks>
/// <para>
/// Until #145 the three bind refusals -- no vector prepared at all, a command naming a scope this end
/// did not prepare, and a content hash that disagrees with the one stamped at first bind -- produced a
/// log line and a <c>RECOVERY_BLOCKED</c> operator event and nothing on the wire. The server's recovery
/// workflow then sat in <c>AwaitingResult</c> and its session in <c>EXECUTING</c> until a reconnect or
/// a person intervened. #123 left it that way on purpose, on the grounds that answering a command this
/// end never prepared would put a result on record for an action the two ends disagree about.
/// </para>
/// <para>
/// What changed is the reading of what the answer says. A <c>FAILED</c> with every slot
/// <c>NOT_STARTED</c> claims nothing about the slots -- it is precisely the statement "this vehicle did
/// not do this" -- and that is the statement the server is waiting for. It closes the session on it
/// (control-server#169) and leaves the demand blocked for recovery, which is the same outcome the
/// disagreement deserved all along, reached without a reconnect.
/// </para>
/// <para>
/// <b>The result is built from the command, never from this end's journal.</b> The server matches a
/// result to its workflow by <c>recoveryActionId</c>, <c>demandId</c>, <c>slotOperationAttemptId</c>,
/// <c>exceptionRecoverySessionId</c> and <c>handoffId</c> (<c>OnboardRecoveryCoordinator.ValidateResultIdentity</c>),
/// and a bind refusal is by definition a case where this end's own values differ. Echoing the command's
/// is what makes the answer land on the workflow that is waiting for it.
/// </para>
/// <para>
/// The log line and the <c>RECOVERY_BLOCKED</c> event stay exactly as they were: the operator's account
/// of the refusal is unchanged, and the reason there is the specific one, which the wire cannot carry.
/// </para>
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    private const string UnpreparedActionId = "7d7d7d7d-7d7d-4d7d-8d7d-7d7d7d7d7d7d";
    private const string SecondCommandMessageId = "abcdabcd-0000-4000-8000-000000001231";
    private static readonly int[] OutOfScopeSlots = [1, 2, 3];

    /// <summary>
    /// Nothing was ever prepared on this end: <c>RECOVERY_VECTOR_CONTEXT_MISSING</c>. The server still
    /// gets its answer, because the workflow waiting for it is the server's own.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationCommandWithNoVectorPreparedIsAnsweredWithFailed()
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

        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand",
            CompensationCommandMessageId,
            UnboundCompensationCommand(UnpreparedActionId, CompensationSlots));

        // The guard first: waiting on the result before the refusal means a red says only "no result
        // came", which is also what an unread command looks like. This order makes the red read
        // "the guard refused with this code, and then nothing was sent".
        await harness.WaitForRecoveryBlockedAsync("RECOVERY_VECTOR_CONTEXT_MISSING", token);
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        AssertRefusedResult(result, UnpreparedActionId, CompensationSlots);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A vector is prepared and the command names a different slot set: <c>RECOVERY_SCOPE_MISMATCH</c>.
    /// The prepared vector is untouched -- the answer is about the command, not about it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationCommandNamingAnotherScopeIsAnsweredWithFailed()
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
        string actionId = prepared.RecoveryVector!.PrimaryId;

        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand",
            CompensationCommandMessageId,
            UnboundCompensationCommand(actionId, OutOfScopeSlots));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_SCOPE_MISMATCH", token);
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        AssertRefusedResult(result, actionId, OutOfScopeSlots);
        Assert.Equal(0, harness.Io.UnlockCount);

        // Answering a command is not settling the vector the operator is still waiting on.
        WireToGateRecoveryState after = await harness.ReadRecoveryStateAsync(token);
        Assert.Equal(actionId, after.RecoveryVector?.PrimaryId);
    }

    /// <summary>
    /// Every field matches the prepared vector and the content hash does not:
    /// <c>RECOVERY_COMMAND_HASH_MISMATCH</c>. The one refusal a scope comparison cannot catch.
    /// </summary>
    /// <remarks>
    /// The hash is stamped on the vector at its first bind, not when it is prepared, so this case only
    /// exists for a command that is not the first: the journal below is what one bound command leaves
    /// behind, and the command sent after it carries content the server changed underneath. The
    /// comparison is between that stamp and the hash <c>BindRecoveryVectorCommandAsync</c> computes
    /// from the command's own fields -- the command's <c>commandContentSha256</c> is never read here --
    /// so the disagreement has to be planted on the stamp.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationCommandWithAnotherContentHashIsAnsweredWithFailed()
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
        string actionId = prepared.RecoveryVector!.PrimaryId;
        Assert.Null(prepared.RecoveryVector.CommandContentSha256);
        await harness.RewriteRecoveryStateAsync(
            state => state with
            {
                RecoveryVector = state.RecoveryVector! with
                {
                    CommandContentSha256 = new string('a', 64)
                }
            },
            token);

        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand",
            CompensationCommandMessageId,
            UnboundCompensationCommand(actionId, CompensationSlots));

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_COMMAND_HASH_MISMATCH", token);
        JsonElement result = await harness.WaitForResultAsync("LoadCompensationResult", token);
        AssertRefusedResult(result, actionId, CompensationSlots);
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// The same unbindable command arriving twice is answered once, under the same messageId: the
    /// answer is keyed by the vector it names, the way every recovery vector result is, so the second
    /// arrival finds it on file and is a replay.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AnUnbindableCompensationCommandArrivingTwiceIsAnsweredOnce()
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

        object command = UnboundCompensationCommand(UnpreparedActionId, CompensationSlots);
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", CompensationCommandMessageId, command);
        await harness.WaitForResultAsync("LoadCompensationResult", token);

        // The replay path publishes OPERATION_REPLAY and sends nothing, so that event is the only
        // proof the second copy was read at all -- without it "still one result" would hold just as
        // well for a copy the vehicle never got to.
        List<string> replays = [];
        harness.Business.OperatorEventPublished += (_, args) =>
        {
            if (args.Value.Kind == "OPERATION_REPLAY")
            {
                lock (replays)
                {
                    replays.Add(args.Value.Message);
                }
            }
        };
        await harness.Server.SendCommandAsync(
            "LoadCompensationCommand", SecondCommandMessageId, command);
        await RecoveryVectorHarness.WaitUntilAsync(
            () =>
            {
                lock (replays)
                {
                    return replays.Count > 0;
                }
            },
            "the vehicle to answer the second copy of the command as a replay",
            token);

        Assert.Single(harness.ResultsOfType("LoadCompensationResult"));
        using JsonDocument only = JsonDocument.Parse(harness.ResultsOfType("LoadCompensationResult")[0]);
        Assert.Equal(
            FakeControlServerIdentifiers.StableUuid(CompensationResultKey(UnpreparedActionId)),
            only.RootElement.GetProperty("messageId").GetString());
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A fault cargo command that binds to nothing stays silent, because the protocol requires its
    /// result to name the operator who authorized the handoff and this end has no such person on file.
    /// </summary>
    /// <remarks>
    /// <c>FaultCargoRecoveryResult</c> and <c>ForcedMechanicalRecoveryResult</c> are the two recovery
    /// results whose schema makes <c>operator</c> required, and the only place an operator is ever
    /// recorded on this end is the prepared vector. So the one refusal that has no vector --
    /// <c>RECOVERY_VECTOR_CONTEXT_MISSING</c> -- cannot be answered for these two without signing the
    /// answer with whoever happens to be at the vehicle now, which would be a worse record than none.
    /// The other two refusals do have a vector and are answered; see
    /// <see cref="FaultCargoCommandNamingADifferentHandoffIsRefusedWithAFailedResult"/>.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task AFaultCargoCommandWithNoVectorPreparedStaysSilent()
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

        await harness.Server.SendCommandAsync(
            "FaultCargoRecoveryCommand",
            CompensationCommandMessageId,
            new
            {
                recoveryActionId = UnpreparedActionId,
                exceptionRecoverySessionId = RecoverySessionId,
                demandId = DemandId,
                handoffId = ExpectedHandoffId,
                slots = CompensationSlots,
                commandContentSha256 = new string('b', 64)
            });

        await harness.WaitForRecoveryBlockedAsync("RECOVERY_VECTOR_CONTEXT_MISSING", token);
        Assert.Empty(harness.ResultsOfType("FaultCargoRecoveryResult"));
        Assert.Equal(0, harness.Io.UnlockCount);
    }

    /// <summary>
    /// A <c>LoadCompensationCommand</c> the way the server sends it, with every identity taken from the
    /// command rather than from this end -- which is the whole point of the cases above.
    /// </summary>
    private static object UnboundCompensationCommand(
        string recoveryActionId,
        IReadOnlyList<int> slots,
        string? contentSha256 = null) =>
        new
        {
            recoveryActionId,
            exceptionRecoverySessionId = RecoverySessionId,
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            slots,
            expectedFinalPhysicalState = "EMPTY",
            commandContentSha256 = contentSha256
                ?? FakeControlServerIdentifiers.LoadCompensationContentSha256(
                    recoveryActionId, DemandId, AttemptId, slots)
        };

    /// <summary>
    /// <c>FAILED</c>, the command's own identity, and one <c>NOT_STARTED</c> slot result per slot the
    /// command named, each carrying the one recovery scope code the protocol defines.
    /// </summary>
    private static void AssertRefusedResult(
        JsonElement result,
        string recoveryActionId,
        IReadOnlyList<int> slots)
    {
        Assert.Equal("FAILED", result.GetProperty("overallOutcome").GetString());
        Assert.Equal(recoveryActionId, result.GetProperty("recoveryActionId").GetString());
        Assert.Equal(DemandId, result.GetProperty("demandId").GetString());
        Assert.Equal(AttemptId, result.GetProperty("slotOperationAttemptId").GetString());

        JsonElement[] slotResults = [.. result.GetProperty("slotResults").EnumerateArray()];
        Assert.Equal(
            slots.Order().ToArray(),
            slotResults.Select(slot => slot.GetProperty("slotNo").GetInt32()).ToArray());
        foreach (JsonElement slot in slotResults)
        {
            Assert.Equal("NOT_STARTED", slot.GetProperty("outcome").GetString());
            Assert.Equal("UNKNOWN", slot.GetProperty("finalPhysicalState").GetString());
            Assert.Equal(
                ["RECOVERY_SCOPE_MISMATCH"],
                slot.GetProperty("reasonCodes").EnumerateArray()
                    .Select(code => code.GetString()!)
                    .ToArray());
        }
    }
}

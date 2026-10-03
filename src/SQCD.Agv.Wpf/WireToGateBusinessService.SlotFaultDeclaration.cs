using System.IO;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The vehicle's half of an administrator's slot fault declaration (REQ-0359, CP-0005, <c>FP-IS-07</c>,
/// 8005-agv-onboard-hmi#215): check that the declaration names the attempt and the slot the executor is waiting on
/// the operator for, and either apply it -- journal it, answer <c>APPLIED</c>, stop the executor, report the attempt
/// UNKNOWN -- or refuse it with <c>NOT_APPLICABLE</c> and change nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The decision is the executor's.</b> Everything checked here first is read off the journal and the outbox,
/// and only spares the executor a question it would answer the same way; the one check that can race the
/// operator -- is the slot still waiting, or has it just closed its loop -- is made in
/// <see cref="WireToGateSlotOperationExecutor.DeclareSlotFaultAsync"/>, under the gate every pulse and every
/// closing goes through.
/// </para>
/// <para>
/// <b>The OperationResult goes out the usual way.</b> The executor run the declaration stops returns an UNKNOWN
/// result to <see cref="HandleSlotOperationAsync"/>, which reports it under <c>operation-result:{attempt}</c> as it
/// reports every other: the load cancellation's silent abort is not this path. The <c>APPLIED</c> answer is sent
/// before the run is stopped, so it is ahead of that result on the wire and in the outbox.
/// </para>
/// <para>
/// <b>Not checked: whether the slot's expected-action-overdue alarm is up.</b> CP-0005 item 4.2 lists three
/// conditions for the vehicle -- same attempt, same slot still waiting, not yet closed or UNKNOWN -- and the
/// threshold of REQ-0358 is the server's to enforce: the declaration form only exists once the overdue row does.
/// The vehicle's wait clock is not journaled either, so after a restart it could not be asked.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string SlotFaultDeclarationApplied = "APPLIED";
    private const string SlotFaultDeclarationNotApplicable = "NOT_APPLICABLE";

    /// <summary>
    /// One declaration at a time, so a resend arriving while the first copy is still being applied finds the
    /// first answer on file instead of judging the declaration a second time.
    /// </summary>
    private readonly SemaphoreSlim _slotFaultDeclarationGate = new(1, 1);

    private async Task HandleSlotFaultDeclarationAsync(
        WireToGateSlotFaultDeclarationCommand command,
        CancellationToken cancellationToken)
    {
        await _slotFaultDeclarationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await HandleSlotFaultDeclarationCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slotFaultDeclarationGate.Release();
        }
    }

    private async Task HandleSlotFaultDeclarationCoreAsync(
        WireToGateSlotFaultDeclarationCommand command,
        CancellationToken cancellationToken)
    {
        // The same declaration again -- the server's outbox resending it, or replaying it after a reconnect: the
        // answer already given goes out again, byte for byte, and nothing is stopped or settled a second time.
        if (await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(
                    WireToGateSessionClient.SlotFaultDeclarationResultKey(command.DeclarationId),
                    cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"收到重复的SlotFaultDeclarationCommand：declarationId={command.DeclarationId}，重发原应答，未再次中止或结算。");
            await SendSlotFaultDeclarationAnswerAsync(
                command.DeclarationId,
                () => _session.ResendSlotFaultDeclarationResultAsync(command.DeclarationId, cancellationToken))
                .ConfigureAwait(false);
            return;
        }

        WireToGateRecoveryState state = await _session.Journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.SlotFaultDeclaration is { } journaled
            && string.Equals(journaled.SlotOperationAttemptId, command.SlotOperationAttemptId, StringComparison.Ordinal))
        {
            if (string.Equals(journaled.DeclarationId, command.DeclarationId, StringComparison.Ordinal))
            {
                // Applied and journaled, but its answer never reached the outbox -- the process went away in
                // between. The answer is rebuilt from the journal: same key, same messageId, same payload.
                await AnswerSlotFaultDeclarationAsync(command, SlotFaultDeclarationApplied, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await RefuseSlotFaultDeclarationAsync(
                command,
                "payload.slotNo",
                $"本次仓位操作已按另一项判定（{journaled.DeclarationId}）把{journaled.SlotNo}号仓报为UNKNOWN。",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        (string FieldPath, string Message)? refusal = await PrecheckSlotFaultDeclarationAsync(
            command,
            state,
            cancellationToken).ConfigureAwait(false);
        if (refusal is null)
        {
            WireToGateSlotFaultDeclaration declaration = new(
                command.DeclarationId,
                command.SlotOperationAttemptId,
                command.SlotNo,
                command.FaultCategory);
            WireToGateSlotFaultDeclarationOutcome outcome = await _executor
                .DeclareSlotFaultAsync(
                    declaration,
                    () => AnswerSlotFaultDeclarationAsync(
                        command,
                        SlotFaultDeclarationApplied,
                        null,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome == WireToGateSlotFaultDeclarationOutcome.Applied)
            {
                _logger.Write(
                    LogSeverity.Warning,
                    nameof(WireToGateBusinessService),
                    $"已执行服务端的人工判故障：declarationId={command.DeclarationId}，attempt={command.SlotOperationAttemptId}，slot={command.SlotNo}，category={command.FaultCategory}，administrator={command.AdministratorId}。执行器已中止，不再开锁。");
                PublishOperatorEvent(
                    $"slot-fault-declared:{command.DeclarationId}",
                    "SLOT_FAULT_DECLARED",
                    WireToGateSlotFaultDeclarationText.Applied(command.SlotNo, command.FaultCategory));
                return;
            }

            refusal = await DescribeExecutorRefusalAsync(command, outcome, cancellationToken).ConfigureAwait(false);
        }

        await RefuseSlotFaultDeclarationAsync(
            command,
            refusal.Value.FieldPath,
            refusal.Value.Message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The refusals the journal and the outbox can answer without asking the executor. Returns null when none
    /// applies, otherwise the field the refusal is about and why.
    /// </summary>
    private async Task<(string FieldPath, string Message)?> PrecheckSlotFaultDeclarationAsync(
        WireToGateSlotFaultDeclarationCommand command,
        WireToGateRecoveryState state,
        CancellationToken cancellationToken)
    {
        // Settled, or its result already on file: the attempt has its conclusion (CP-0005 item 5).
        if (await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(
                    $"operation-result:{command.SlotOperationAttemptId}",
                    cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            return ("payload.slotOperationAttemptId", "本次仓位操作已结算，结果已上报。");
        }

        // A load cancellation took the slots over, or the operator's cancellation of this attempt is waiting
        // for its answer: the attempt's conclusion is that cancellation's. Read here rather than through
        // IsAttemptTakenOverAsync, which also counts the attempt still running -- the very one a declaration is for.
        if (string.Equals(
                state.RecoveryVector?.SlotOperationAttemptId,
                command.SlotOperationAttemptId,
                StringComparison.Ordinal)
            || string.Equals(
                state.PendingLoadCancellation?.SlotOperationAttemptId,
                command.SlotOperationAttemptId,
                StringComparison.Ordinal)
            || await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(
                    $"recovery-vector-result:{WireToGateRecoveryVectorTypes.LoadCancellation}:"
                    + InFlightLoadCancellationId(command.DemandId, command.SlotOperationAttemptId),
                    cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            return ("payload.slotOperationAttemptId", "本次装货已由装货取消接手。");
        }

        if (string.Equals(
                state.UnsettledSlotOperationAttemptId,
                command.SlotOperationAttemptId,
                StringComparison.Ordinal))
        {
            if (state.OperationContext is { } context
                && !string.Equals(context.DemandId, command.DemandId, StringComparison.Ordinal))
            {
                return ("payload.demandId", "判定所指的需求与本车这次仓位操作的需求不一致。");
            }

            if (state.SlotResults.Any(result =>
                    result.SlotNo == command.SlotNo
                    && string.Equals(result.Outcome, "UNKNOWN", StringComparison.Ordinal)))
            {
                return ("payload.slotNo", $"{command.SlotNo}号仓已判为UNKNOWN。");
            }
        }

        return null;
    }

    private async Task<(string FieldPath, string Message)> DescribeExecutorRefusalAsync(
        WireToGateSlotFaultDeclarationCommand command,
        WireToGateSlotFaultDeclarationOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome == WireToGateSlotFaultDeclarationOutcome.SlotNotAwaiting)
        {
            return ("payload.slotNo", $"{command.SlotNo}号仓此刻不在等待操作员：已闭环、已判为UNKNOWN，或本次操作正在别的仓。");
        }

        if (outcome == WireToGateSlotFaultDeclarationOutcome.TakenOverByLoadCancellation)
        {
            return ("payload.slotOperationAttemptId", "本次装货已由装货取消接手。");
        }

        if (outcome == WireToGateSlotFaultDeclarationOutcome.AlreadyDeclared)
        {
            return ("payload.slotNo", "本次仓位操作已有一项判定生效。");
        }

        // Not running here. Said as precisely as the journal allows: the run may have ended between the checks
        // above and the executor's, with its result now on file.
        WireToGateRecoveryState state = await _session.Journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await PrecheckSlotFaultDeclarationAsync(command, state, cancellationToken).ConfigureAwait(false) is { } now)
        {
            return now;
        }

        return string.Equals(
                state.UnsettledSlotOperationAttemptId,
                command.SlotOperationAttemptId,
                StringComparison.Ordinal)
            ? ("payload.slotOperationAttemptId", "本次仓位操作已停止执行，等待结算或恢复。")
            : ("payload.slotOperationAttemptId", "本车没有正在执行的这次仓位操作：尝试已换代，或本车从未收到它。");
    }

    private Task RefuseSlotFaultDeclarationAsync(
        WireToGateSlotFaultDeclarationCommand command,
        string fieldPath,
        string displayMessage,
        CancellationToken cancellationToken)
    {
        _logger.Write(
            LogSeverity.Information,
            nameof(WireToGateBusinessService),
            $"拒绝服务端的人工判故障：declarationId={command.DeclarationId}，attempt={command.SlotOperationAttemptId}，slot={command.SlotNo}，field={fieldPath}，原因：{displayMessage}未改变任何业务状态。");
        return AnswerSlotFaultDeclarationAsync(
            command,
            SlotFaultDeclarationNotApplicable,
            new WireToGateProblemPayload("ACTION_NOT_ALLOWED_IN_STATE", fieldPath, displayMessage),
            cancellationToken);
    }

    /// <summary>
    /// Puts the answer on file and on the wire. Never throws for a send that failed: the answer is in the outbox
    /// whichever way it failed, and the next handshake replays it (durableBeforeSend).
    /// </summary>
    private Task AnswerSlotFaultDeclarationAsync(
        WireToGateSlotFaultDeclarationCommand command,
        string outcome,
        WireToGateProblemPayload? problem,
        CancellationToken cancellationToken) =>
        SendSlotFaultDeclarationAnswerAsync(
            command.DeclarationId,
            () => _session.SendSlotFaultDeclarationResultAsync(
                new SlotFaultDeclarationResultPayload(
                    command.DeclarationId,
                    command.SlotOperationAttemptId,
                    outcome,
                    problem),
                cancellationToken));

    private async Task SendSlotFaultDeclarationAnswerAsync(string declarationId, Func<Task<string>> send)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException
            or OperationCanceledException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"SlotFaultDeclarationResult暂未收到DurableAck：declarationId={declarationId}。",
                exception);
        }
    }

    /// <summary>
    /// After a restart, sends the <c>APPLIED</c> answer of a declaration the journal holds and the outbox does not,
    /// ahead of the interrupted settlement's result, which reports the declared slot UNKNOWN from the same journal
    /// entry (8005-agv-onboard-hmi#215). Returns that declaration, or null when the journal holds none for the attempt.
    /// </summary>
    /// <remarks>
    /// The operator is told again, as when the declaration first arrived: the process that told them is gone, and the
    /// screen after a restart otherwise says only that the interrupted operation needs recovery (review N2 of PR #247).
    /// </remarks>
    private async Task<WireToGateSlotFaultDeclaration?> ReplayJournaledSlotFaultDeclarationAsync(
        WireToGateRecoveryOperationContext context,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await _session.Journal
            .ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state.SlotFaultDeclaration is not { } declaration
            || !string.Equals(
                declaration.SlotOperationAttemptId,
                context.SlotOperationAttemptId,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(
                    WireToGateSessionClient.SlotFaultDeclarationResultKey(declaration.DeclarationId),
                    cancellationToken)
                .ConfigureAwait(false) is null)
        {
            await SendSlotFaultDeclarationAnswerAsync(
                declaration.DeclarationId,
                () => _session.SendSlotFaultDeclarationResultAsync(
                    new SlotFaultDeclarationResultPayload(
                        declaration.DeclarationId,
                        declaration.SlotOperationAttemptId,
                        SlotFaultDeclarationApplied,
                        null),
                    cancellationToken)).ConfigureAwait(false);
        }

        PublishOperatorEvent(
            $"slot-fault-declared:{declaration.DeclarationId}",
            "SLOT_FAULT_DECLARED",
            WireToGateSlotFaultDeclarationText.Applied(declaration.SlotNo, declaration.FaultCategory));
        return declaration;
    }
}

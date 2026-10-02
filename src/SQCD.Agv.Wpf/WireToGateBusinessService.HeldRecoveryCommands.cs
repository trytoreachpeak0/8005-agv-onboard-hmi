using System.IO;
using SQCD.Agv.Application;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// One server recovery command held for someone at the vehicle to confirm, as the operator is shown it
/// (onboard-hmi#239). Handed back unchanged when they press, and checked against the one still held.
/// </summary>
/// <param name="CommandMessageId">The held command's messageId: a replay of the same command keeps it.</param>
/// <param name="VectorType">The recovery vector type, or <c>RESUME_AFTER_REPAIR</c> for a resume.</param>
/// <param name="PrimaryId">The recoveryActionId or correctionId the command names.</param>
/// <param name="Slots">The slots the command would open.</param>
/// <param name="OpenedSlots">The slots the journal shows were already opened, or may have been, before the restart.</param>
/// <param name="Trigger">Why it was held: the restart, or the press being past the window.</param>
/// <param name="Text">What the screen says.</param>
/// <param name="CanDecline">Whether "do not execute" is offered for it.</param>
/// <param name="DemandId">The demand the command is about.</param>
/// <param name="DeclineConsequence">
/// For a decline that costs more than the session -- a load correction's -- what it costs, in the words the second
/// press is asked under; empty when one press decides.
/// </param>
public sealed record WireToGateHeldRecoveryCommandPrompt(
    string CommandMessageId,
    string VectorType,
    string PrimaryId,
    string? DemandId,
    IReadOnlyList<int> Slots,
    IReadOnlyList<int> OpenedSlots,
    string Trigger,
    string Text,
    bool CanDecline,
    string DeclineConsequence);

/// <summary>
/// The held-command entry as the screen reads it, all from one moment (onboard-hmi#239): what is held, and which of
/// the two buttons is offered.
/// </summary>
/// <param name="DeclineArmed">
/// "Do not execute" was pressed once for this very command and its consequence shown: the next press decides.
/// </param>
public sealed record WireToGateHeldRecoveryCommandView(
    WireToGateHeldRecoveryCommandPrompt? Prompt,
    bool CanConfirm,
    bool CanDecline,
    bool DeclineArmed);

/// <summary>
/// The server recovery commands that open doors, held after a restart until someone at the vehicle confirms them
/// (onboard-hmi#239).
/// </summary>
/// <remarks>
/// <para>
/// The server replays every bound, unsettled recovery command into each new session
/// (control-server <c>ReplayPendingCommandsAsync</c>), and a compensation, correction, fault cargo handoff or resume
/// pulses the slots' unlock outputs as soon as it arrives -- the vehicle checks only that it stands still. After a
/// restart the press that earned the command can be arbitrarily old and nobody need be at the vehicle; hmi#236 stopped
/// the vehicle asking again by itself for exactly that reason, and the server's replay still opened the doors (its
/// review probe: afterRestartUnlocks=1).
/// </para>
/// <para>
/// <b>The rule is hmi#236's.</b> A command this process holds a person's press for, within
/// <see cref="AuthorizationResendWindow"/>, carries on as it always did -- the same-process self-heal its premise table
/// rests on. Any other is held: not bound, not executed, not answered, kept in memory only, and shown. Holding costs
/// the server nothing: the vehicle never acknowledges a command, a command is settled only by its result, so the
/// server replays it into every later session and re-sends it on every request -- a restart while one is held simply
/// holds it again.
/// </para>
/// <para>
/// <b>Two ways out, both a person's.</b> "Execute" records a press by the operator now and runs the command down its
/// own path, motion checks and all. "Do not execute" answers the server: <c>FAILED</c> with every slot
/// <c>NOT_STARTED</c> for a vector that did nothing, <c>UNKNOWN</c> from the journal for one that had opened some
/// doors, a rejection for a resume. The server closes the session on any of them (control-server#169). There is no
/// timeout: both exits stay on the screen, and nothing about the vehicle moves on without a person. The forced
/// mechanical recovery is not held -- it opens nothing and already waits for the person who opens the slots.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string ResumeAfterRepairKind = "RESUME_AFTER_REPAIR";

    private HeldCommand? _heldRecoveryCommand;

    /// <summary>
    /// The held command whose "do not execute" was pressed once and its consequence shown, as
    /// <see cref="DeclineKey"/>: the second press decides only for that.
    /// </summary>
    private string? _declineArmedFor;

    /// <summary>The command held for confirmation, while it still is; <c>null</c> otherwise.</summary>
    public WireToGateHeldRecoveryCommandPrompt? HeldRecoveryCommand =>
        Volatile.Read(ref _heldRecoveryCommand) is { } held
        && HeldCommandVoidedBecause(held, Volatile.Read(ref _lastRecoveryState)) is null
            ? held.Prompt
            : null;

    /// <summary>
    /// Whether the held command can be carried out from here: an operator who could have pressed for it in the first
    /// place, at a vehicle whose session can carry its result.
    /// </summary>
    public bool CanConfirmHeldRecoveryCommand =>
        HeldRecoveryCommand is { } prompt
        && CanUseRecoveryOperator(requireProof: prompt.VectorType != WireToGateRecoveryVectorTypes.LoadCorrection);

    /// <summary>The entry as the screen reads it, read once.</summary>
    public WireToGateHeldRecoveryCommandView HeldRecoveryCommandView =>
        HeldRecoveryCommand is not { } prompt
            ? new(null, false, false, false)
            : new(
                prompt,
                CanConfirmHeldRecoveryCommand,
                CanDeclineHeldRecoveryCommand,
                Volatile.Read(ref _declineArmedFor) == DeclineKey(prompt));

    /// <summary>Whether "do not execute" is offered for the held command.</summary>
    public bool CanDeclineHeldRecoveryCommand =>
        HeldRecoveryCommand is { CanDecline: true } prompt
        && CanUseRecoveryOperator(requireProof: prompt.VectorType != WireToGateRecoveryVectorTypes.LoadCorrection);

    /// <summary>
    /// The operator at the vehicle has looked at the slots and confirms the held command: it runs now, down its own
    /// path. <paramref name="shown"/> is the prompt they read; anything else held by now is not what they confirmed.
    /// </summary>
    public Task<bool> ConfirmHeldRecoveryCommandAsync(
        WireToGateHeldRecoveryCommandPrompt shown,
        CancellationToken cancellationToken = default) =>
        DecideHeldRecoveryCommandAsync(shown, HeldRecoveryDecision.Confirmed, cancellationToken);

    /// <summary>
    /// The operator chooses not to carry the held command out: the server is answered without a door being opened,
    /// and closes the session.
    /// </summary>
    public Task<bool> DeclineHeldRecoveryCommandAsync(
        WireToGateHeldRecoveryCommandPrompt shown,
        CancellationToken cancellationToken = default) =>
        DecideHeldRecoveryCommandAsync(shown, HeldRecoveryDecision.Declined, cancellationToken);

    /// <summary>A person's press for <paramref name="primaryId"/>, now. Only presses move it.</summary>
    private void MarkOperatorPress(string primaryId) =>
        _authorizationRequestedInThisProcess[primaryId] = _clock.Now;

    /// <summary>Whether this process holds a person's press for <paramref name="primaryId"/> within the window.</summary>
    private bool PressedWithinWindow(string primaryId) =>
        _authorizationRequestedInThisProcess.TryGetValue(primaryId, out DateTimeOffset pressedAt)
        && _clock.Now - pressedAt <= AuthorizationResendWindow;

    /// <summary>Why a command for <paramref name="primaryId"/> needs a person now, in the words the screen and the log use.</summary>
    private string HoldTrigger(string primaryId) =>
        _authorizationRequestedInThisProcess.TryGetValue(primaryId, out DateTimeOffset pressedAt)
            ? $"离上次按下（{pressedAt:yyyy-MM-dd HH:mm:ss}）已超过 {AuthorizationResendWindow.TotalMinutes:0} 分钟"
            : "车载端重启后本进程没有人按过";

    /// <summary>
    /// Holds <paramref name="command"/> for confirmation and shows it. A different command held before is voided:
    /// what the operator would be confirming is the newer one.
    /// </summary>
    private void HoldRecoveryCommand(
        WireToGateServerCommand command,
        string vectorType,
        string primaryId,
        string? exceptionRecoverySessionId,
        string? demandId,
        IReadOnlyList<int> slots,
        IReadOnlyList<int> openedSlots,
        bool canDecline)
    {
        string trigger = HoldTrigger(primaryId);
        string subject = HeldCommandSubject(vectorType);
        string opened = openedSlots.Count > 0
            ? $"注意：部分仓门已开过：{FormatSlots(openedSlots)}。"
            : string.Empty;
        string decline = canDecline
            ? "如果不再执行，按「不执行」，服务端将结束本次恢复。"
            : string.Empty;
        string text = $"服务端下发了车辆 {_session.Client.AgvId} 的{subject}（{vectorType}）{FormatSlots(slots)}命令。"
            + $"{trigger}，车辆不会自动开锁。{opened}"
            + $"请到车旁确认仓门附近安全后按「确认执行」。{decline} ";
        // A correction declined is not just a session closed: the server keeps the demand blocked on the FAILED result
        // and never authorizes that load's correction again (control-server ApplyCurrentResultAsync,
        // KeepDemandAndJourneyBlockedAsync), so an administrator has to open a recovery session. Asked twice, with the
        // cost spelled out (decided by the coordinator on onboard-hmi#239).
        string consequence = canDecline && vectorType == WireToGateRecoveryVectorTypes.LoadCorrection
            ? $"车辆 {_session.Client.AgvId} 需求 {demandId} 的装货修正{FormatSlots(slots)}选择不执行后，"
                + "这条装货的修正将作废，需求进入恢复，需要管理员开恢复会话处理。"
            : string.Empty;
        WireToGateHeldRecoveryCommandPrompt prompt = new(
            command.MessageId,
            vectorType,
            primaryId,
            demandId,
            [.. slots],
            [.. openedSlots],
            trigger,
            text,
            canDecline,
            consequence);
        // A decline armed for anything before is not one for this: the second press is bound to what the first showed.
        Volatile.Write(ref _declineArmedFor, null);
        HeldCommand? previous = Interlocked.Exchange(
            ref _heldRecoveryCommand,
            new HeldCommand(command, prompt, exceptionRecoverySessionId));
        if (previous is not null && previous.Prompt.PrimaryId != primaryId)
        {
            PublishHeldCommandVoided(previous, "收到了更新的服务端恢复命令");
        }

        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"服务端恢复命令已扣住，等待现场确认：type={vectorType}，primaryId={primaryId}，message={command.MessageId}，"
            + $"原因={trigger}，已开过的仓={FormatSlotList(openedSlots)}。");
        PublishOperatorEvent(
            $"recovery-command-held:{primaryId}:{command.MessageId}:{_session.Current.SessionGeneration}",
            "RECOVERY_COMMAND_HELD",
            text);
    }

    /// <summary>
    /// A copy of the held command carries on by itself -- a press within the window has earned it again -- so the
    /// held one is no longer the operator's to decide.
    /// </summary>
    private void ReleaseHeldCommandFor(string primaryId) =>
        ForgetHeldCommandFor(primaryId, "已由本进程内的新按键接手执行");

    /// <summary>Ends the hold on <paramref name="primaryId"/>'s command without showing anything: the caller does.</summary>
    private void ForgetHeldCommandFor(string primaryId, string why)
    {
        if (Volatile.Read(ref _heldRecoveryCommand) is { } held
            && held.Prompt.PrimaryId == primaryId
            && Interlocked.CompareExchange(ref _heldRecoveryCommand, null, held) == held)
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"扣住的服务端恢复命令结束扣住：type={held.Prompt.VectorType}，primaryId={primaryId}，原因={why}。");
        }
    }

    /// <summary>
    /// Voids the held command once what it was about is gone -- its vector cleared or settled -- and says so on the
    /// screen. Run whenever the cached recovery state moves; a CLOSED recovery session snapshot voids it where the
    /// snapshot is handled.
    /// </summary>
    private void ReviewHeldRecoveryCommand(WireToGateRecoveryState state)
    {
        if (Volatile.Read(ref _heldRecoveryCommand) is not { } held
            || HeldCommandVoidedBecause(held, state) is not { } reason
            || Interlocked.CompareExchange(ref _heldRecoveryCommand, null, held) != held)
        {
            return;
        }

        // Off the journal's step: this runs from inside it (CacheRecoveryState), and the operator event's handlers are
        // nothing the journal's lock should wait on.
        TrackTask(Task.Run(() => PublishHeldCommandVoided(held, reason)));
    }

    private static string? HeldCommandVoidedBecause(HeldCommand held, WireToGateRecoveryState state)
    {
        if (held.Prompt.VectorType == ResumeAfterRepairKind)
        {
            return held.SessionReleasedByDecline
                || state.RecoveryActionId == held.Prompt.PrimaryId
                && state.ExceptionRecoverySessionId == held.ExceptionRecoverySessionId
                    ? null
                    : "本车的恢复会话记录已清除";
        }

        if (state.RecoveryVector is { } vector
            && vector.VectorType == held.Prompt.VectorType
            && vector.PrimaryId == held.Prompt.PrimaryId)
        {
            return null;
        }

        // A CLOSED snapshot reaches the journal before it reaches the business service: the session layer forgets the
        // closed session and its vector first (onboard-hmi#129), so the vector going with its session is how a close
        // shows here.
        return held.ExceptionRecoverySessionId is not null
            && state.ExceptionRecoverySessionId != held.ExceptionRecoverySessionId
                ? "服务端恢复会话已关闭，本车已清除该会话与恢复向量"
                : "本车的恢复向量已清除（已执行完毕、已被拒绝或已结算）";
    }

    private void PublishHeldCommandVoided(HeldCommand held, string reason)
    {
        _logger.Write(
            LogSeverity.Information,
            nameof(WireToGateBusinessService),
            $"扣住的服务端恢复命令已作废：type={held.Prompt.VectorType}，primaryId={held.Prompt.PrimaryId}，"
            + $"message={held.Prompt.CommandMessageId}，原因={reason}。");
        PublishOperatorEvent(
            $"recovery-command-held-voided:{held.Prompt.PrimaryId}:{held.Prompt.CommandMessageId}:{reason}",
            "RECOVERY_COMMAND_HELD_VOIDED",
            $"等待现场确认的{HeldCommandSubject(held.Prompt.VectorType)}{FormatSlots(held.Prompt.Slots)}命令已作废：{reason}。"
            + "「确认执行」「不执行」已收起，车辆没有因此开锁。 ");
    }

    private async Task<bool> DecideHeldRecoveryCommandAsync(
        WireToGateHeldRecoveryCommandPrompt shown,
        HeldRecoveryDecision decision,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(shown);
        HeldCommand? held = Volatile.Read(ref _heldRecoveryCommand);
        if (held is null
            || held.Prompt.CommandMessageId != shown.CommandMessageId
            || held.Prompt.PrimaryId != shown.PrimaryId)
        {
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                "这条服务端恢复命令已不再等待确认，未执行任何操作。 ");
            return false;
        }

        if (decision == HeldRecoveryDecision.Confirmed && held.SessionReleasedByDecline)
        {
            // A decline already began on this resume and forgot its session; only its rejection failed to be written.
            // Carried out now, the gate would refuse it as RECOVERY_AUTHENTICATION_FAILED, and the server's audit would
            // record an authentication failure nobody had. The operator's earlier choice stands, and is answered as such.
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"扣住的修复后续行命令此前已选择不执行，本次「确认执行」按不执行回复：primaryId={held.Prompt.PrimaryId}，"
                + $"message={held.Prompt.CommandMessageId}。");
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                "之前已经选择了不执行，恢复会话已经释放，这次按照不执行回复服务端；未开任何仓门。 ");
            decision = HeldRecoveryDecision.Declined;
        }

        if (decision == HeldRecoveryDecision.Declined && !held.Prompt.CanDecline)
        {
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"{HeldCommandSubject(held.Prompt.VectorType)}命令不提供「不执行」，未执行任何操作。 ");
            return false;
        }

        if (decision == HeldRecoveryDecision.Declined
            && held.Prompt.DeclineConsequence.Length > 0
            && Interlocked.Exchange(ref _declineArmedFor, DeclineKey(held.Prompt)) != DeclineKey(held.Prompt))
        {
            // The first press only shows what declining costs. The key holds every fact the sentence names, so a second
            // press counts only for the command the operator read it about.
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"扣住的服务端恢复命令第一次按「不执行」，等待第二次确认：type={held.Prompt.VectorType}，"
                + $"primaryId={held.Prompt.PrimaryId}，message={held.Prompt.CommandMessageId}。");
            PublishOperatorEvent(
                $"recovery-command-decline-armed:{DeclineKey(held.Prompt)}:{_clock.Now:O}",
                "RECOVERY_COMMAND_DECLINE_CONFIRMATION",
                held.Prompt.DeclineConsequence + "确定不执行请再按一次「不执行」。 ");
            return false;
        }

        WireToGateOperatorContextPayload pressing;
        WireToGateRecoveryState state;
        try
        {
            pressing = ReadOperatorContext();
            state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or InvalidDataException)
        {
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                OnboardCommandRejectionText.DescribeRecoveryBlocked(exception.Message));
            return false;
        }

        if (HeldCommandVoidedBecause(held, state) is { } reason)
        {
            if (Interlocked.CompareExchange(ref _heldRecoveryCommand, null, held) == held)
            {
                PublishHeldCommandVoided(held, reason);
            }

            return false;
        }

        // A confirmed command is no longer the operator's to decide the moment it goes. A declined one stays held until
        // its answer is in the outbox: should the answer not be written, the server is still waiting, and the two buttons
        // are the only way on from here (onboard-hmi#239 review).
        bool stillHeld = decision == HeldRecoveryDecision.Confirmed
            ? Interlocked.CompareExchange(ref _heldRecoveryCommand, null, held) == held
            : Volatile.Read(ref _heldRecoveryCommand) == held;
        if (!stillHeld)
        {
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                "这条服务端恢复命令已不再等待确认，未执行任何操作。 ");
            return false;
        }

        Volatile.Write(ref _declineArmedFor, null);

        _logger.Write(
            LogSeverity.Information,
            nameof(WireToGateBusinessService),
            $"扣住的服务端恢复命令由操作员{(decision == HeldRecoveryDecision.Confirmed ? "确认执行" : "选择不执行")}："
            + $"type={held.Prompt.VectorType}，primaryId={held.Prompt.PrimaryId}，message={held.Prompt.CommandMessageId}，"
            + $"操作员={pressing.OperatorId}，按下时间={_clock.Now:O}。");
        if (decision == HeldRecoveryDecision.Confirmed)
        {
            MarkOperatorPress(held.Prompt.PrimaryId);
        }

        await (held.Command switch
        {
            WireToGateLoadCompensationCommand compensation =>
                HandleLoadCompensationCommandAsync(compensation, cancellationToken, decision),
            WireToGateLoadCorrectionCommand correction =>
                HandleLoadCorrectionCommandAsync(correction, cancellationToken, decision),
            WireToGateFaultCargoRecoveryCommand faultCargo =>
                HandleFaultCargoRecoveryCommandAsync(faultCargo, cancellationToken, decision),
            WireToGateSlotOperationResumeCommand resume =>
                HandleBlockedResumeAsync(resume, cancellationToken, decision),
            _ => throw new InvalidOperationException("RECOVERY_COMMAND_INVALID")
        }).ConfigureAwait(false);
        // A decline has answered the server exactly when it ended the hold: an answer that could not be written leaves the
        // command held, and that press answered nothing. Compared by the command, not the record: a resume's decline
        // replaces the record to mark its session released.
        return decision == HeldRecoveryDecision.Confirmed
            || Volatile.Read(ref _heldRecoveryCommand) is not { } still
            || still.Prompt.CommandMessageId != held.Prompt.CommandMessageId
            || still.Prompt.PrimaryId != held.Prompt.PrimaryId;
    }

    /// <summary>
    /// Answers a held recovery vector the operator chose not to carry out, opening nothing: <c>FAILED</c> for one that
    /// did nothing, the journal's account as <c>UNKNOWN</c> for one that had opened doors before the restart.
    /// </summary>
    /// <remarks>
    /// Bound first, by the caller, exactly as an execution would be: the answer names the vector the command
    /// authorized. The forgetting afterwards is the refusal's for a vector that did nothing -- the server closes the
    /// session on the result, and nothing else would clear the entry -- and the settled failure's for one that acted.
    /// </remarks>
    private async Task DeclineHeldRecoveryVectorAsync(
        WireToGateRecoveryVectorContext context,
        string resultKey,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryVectorExecutionResult? refused = await _vectorExecutor
            .RefuseBeforeUnlockAsync(context, "VEHICLE_NOT_READY", cancellationToken)
            .ConfigureAwait(false);
        WireToGateRecoveryState before = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
            .ConfigureAwait(false);
        int[] opened = refused is null ? OpenedSlotsOf(before, context.VectorType, context.PrimaryId) : [];
        WireToGateRecoveryVectorExecutionResult result = refused
            ?? await _vectorExecutor.SettleWithoutUnlockAsync(context, cancellationToken).ConfigureAwait(false);
        bool acknowledged = true;
        try
        {
            await SendRecoveryVectorResultAsync(context, resultKey, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException)
        {
            acknowledged = false;
            bool onFile = await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(resultKey, cancellationToken)
                .ConfigureAwait(false) is not null;
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                onFile
                    ? $"操作员选择不执行的恢复向量结果已写入发件箱，暂未收到DurableAck：type={context.VectorType}，id={context.PrimaryId}。"
                    : $"操作员选择不执行的恢复向量结果未能写入发件箱：type={context.VectorType}，id={context.PrimaryId}。",
                exception);
            if (!onFile)
            {
                // The hold stays: the server is still waiting, and pressing again is the way on.
                PublishOperatorResponse(
                    "RECOVERY_BLOCKED",
                    $"「不执行」的结果没能写入发件箱（{exception.Message}），服务端仍在等待，扣住的命令保留；"
                    + "未开任何仓门，可以再按一次「不执行」。 ");
                return;
            }

            PublishOperatorEvent(
                $"recovery-vector-result-pending:{context.VectorType}:{context.PrimaryId}",
                "RESULT_ACK_PENDING",
                "恢复结果已持久化，等待服务端确认；不会重复执行仓门IO。 ");
        }

        // On file now, so the server will hear it: the operator has nothing left to decide. Ended before the vector is
        // forgotten, so the forgetting does not read as the hold being voided.
        ForgetHeldCommandFor(context.PrimaryId, "操作员选择不执行，结果已写入发件箱");
        if (refused is not null)
        {
            if (context.ExceptionRecoverySessionId is null)
            {
                await UpdateRecoveryStateCachedAsync(
                        current => current.RecoveryVector is { } vector
                            && vector.VectorType == context.VectorType
                            && vector.PrimaryId == context.PrimaryId
                                ? ForgetRefusedVector(current, vector)
                                : null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ReleaseRefusedVectorAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (acknowledged)
        {
            await ForgetSettledRecoveryVectorAsync(context, cancellationToken).ConfigureAwait(false);
        }

        string subject = HeldCommandSubject(context.VectorType);
        PublishOperatorEvent(
            $"recovery-command-declined:{context.VectorType}:{context.PrimaryId}",
            "RECOVERY_COMMAND_DECLINED",
            refused is not null
                ? $"已按操作员选择不执行{subject}{FormatSlots(context.Slots)}，未开任何仓门；已向服务端报告未执行（FAILED），"
                    + "服务端将结束本次恢复，如仍需处理请重新发起。 "
                : result.OverallOutcome == "COMPLETED"
                    ? $"已按操作员选择不再继续{subject}；按日志这条恢复在重启前已执行完毕"
                        + $"（{FormatSlots(opened)}），已如实向服务端报告完成，车辆没有再开任何仓门。 "
                    : $"已按操作员选择不再继续{subject}；部分仓门已开过：{FormatSlots(opened)}。"
                        + "已按日志如实向服务端报告结果未知（UNKNOWN），服务端将结束本次恢复，请现场核对这些仓位后重新发起。 ");
    }

    /// <summary>
    /// Rejects a held resume the operator chose not to carry out, opening nothing, and ends the hold only once the
    /// rejection is in the outbox (onboard-hmi#239 incremental review S-1, the resume's half of the vector decline).
    /// </summary>
    /// <remarks>
    /// The rejection forgets the vehicle's recovery session first and then writes itself
    /// (<see cref="SendResumeRejectedAsync"/>, for its own reasons), and a failed write is only logged there. So the hold
    /// is marked before -- that forgetting must not void it -- and judged after, by the rejection being on file. Not on
    /// file, the server is still waiting: the hold stays, the screen says why, and the decline can be pressed again.
    /// </remarks>
    private async Task DeclineHeldResumeAsync(
        WireToGateSlotOperationResumeCommand command,
        CancellationToken cancellationToken)
    {
        MarkHeldResumeSessionReleased(command.RecoveryActionId);
        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"操作员选择不执行修复后续行命令：attempt={command.SlotOperationAttemptId}，action={command.RecoveryActionId}，"
            + "以VEHICLE_NOT_READY拒绝。");
        await SendResumeRejectedAsync(command, "VEHICLE_NOT_READY", cancellationToken).ConfigureAwait(false);
        if (await _session.Journal
                .ReadOutgoingByDeduplicationKeyAsync(ResumeRejectedKey(command), cancellationToken)
                .ConfigureAwait(false) is null)
        {
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                "「不执行」的拒绝没能写入发件箱，服务端仍在等待，扣住的命令保留；未开任何仓门，可以再按一次「不执行」。 ");
            return;
        }

        ForgetHeldCommandFor(command.RecoveryActionId, "操作员选择不执行，拒绝已写入发件箱");
        PublishOperatorEvent(
            $"recovery-command-declined:{ResumeAfterRepairKind}:{command.RecoveryActionId}",
            "RECOVERY_COMMAND_DECLINED",
            $"已按操作员选择不执行修复后续行原仓位操作{FormatSlots(command.Slots)}，未开任何仓门；已向服务端拒绝该命令，"
            + "服务端将结束本次恢复，如仍需处理请重新发起。 ");
    }

    private void MarkHeldResumeSessionReleased(string primaryId)
    {
        while (Volatile.Read(ref _heldRecoveryCommand) is { SessionReleasedByDecline: false } held
            && held.Prompt.PrimaryId == primaryId
            && Interlocked.CompareExchange(
                ref _heldRecoveryCommand,
                held with { SessionReleasedByDecline = true },
                held) != held)
        {
        }
    }

    /// <summary>The slots the journal shows <paramref name="primaryId"/>'s vector opened, or may have.</summary>
    private static int[] OpenedSlotsOf(WireToGateRecoveryState state, string vectorType, string primaryId) =>
        state.RecoveryVector is { } vector && vector.VectorType == vectorType && vector.PrimaryId == primaryId
            ? [.. state.ActiveUnlockSlots.Concat(state.CompletedSlots).Where(vector.Slots.Contains).Distinct().Order()]
            : [];

    /// <summary>Everything a decline's consequence names, and the command it is about.</summary>
    private string DeclineKey(WireToGateHeldRecoveryCommandPrompt prompt) =>
        $"{_session.Client.AgvId}|{prompt.VectorType}|{prompt.PrimaryId}|{prompt.CommandMessageId}|{prompt.DemandId}|"
        + string.Join(',', prompt.Slots);

    private static string HeldCommandSubject(string vectorType) => vectorType switch
    {
        WireToGateRecoveryVectorTypes.LoadCompensation => "补偿清空",
        WireToGateRecoveryVectorTypes.LoadCorrection => "装货修正",
        WireToGateRecoveryVectorTypes.FaultCargoHandoff => "故障仓货物交接",
        ResumeAfterRepairKind => "修复后续行原仓位操作",
        _ => vectorType
    };

    private static string FormatSlotList(IReadOnlyList<int> slots) =>
        slots.Count == 0 ? "无" : string.Join(",", slots);

    /// <param name="SessionReleasedByDecline">
    /// A resume's decline has started: its rejection forgets the vehicle's recovery session before it is written, so the
    /// session going is the decline's own doing, not a reason to void the hold.
    /// </param>
    private sealed record HeldCommand(
        WireToGateServerCommand Command,
        WireToGateHeldRecoveryCommandPrompt Prompt,
        string? ExceptionRecoverySessionId,
        bool SessionReleasedByDecline = false);
}

/// <summary>How a recovery command reaches its handler: arriving from the server, or a held one a person decided on.</summary>
internal enum HeldRecoveryDecision
{
    None,
    Confirmed,
    Declined
}

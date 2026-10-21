using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// Recovery the server has not opened for this vehicle (8005-agv-onboard-hmi#391, the onboard half of control-server#797
/// option C): what the last recovery refusal means, and whether recovery is not open for the recovery on file.
/// </summary>
/// <remarks>
/// <para>
/// With option C the recovery entries are always offered, and maintenance grants a time-limited authorization per vehicle
/// and journey on the server. Without one, the server refuses the session with <c>RECOVERY_AUTHENTICATION_FAILED</c>;
/// inside a session it refuses an action outside what it granted with the same code; and it closes a session whose
/// authorization has ended with that code as its <c>closedReason</c>, even when the action got done first (CP-0022,
/// ADR-cross-0066 decision 5, control-server#797 rework). The server uses the one code for a wrong credential and for no
/// authorization, and this end cannot tell them apart, so both read as "not open, call maintenance".
/// </para>
/// <para>
/// <b>Every refusal replaces the last one</b> (review M1 of PR #405): a refusal for another reason clears what an earlier
/// one said, so neither the dialog nor the line beside the entries describes a refusal that is not the latest. A refusal
/// inside an open session is said as outside what was opened and does not light the not-open line, which would sit beside
/// 「会话已开」 (review M2).
/// </para>
/// <para>
/// <b>Bound to the recovery it was said about</b> (review L2): the vehicle has no journey id, so the unsettled attempt and
/// its demand stand for it. Once the recovery on file is another, or none -- the action completed and settled it -- the
/// not-open line is gone.
/// </para>
/// <para>
/// <b>Display only.</b> Nothing here gates an entry or asks the server anything: the next press is the operator's, and a
/// refusal is never sent again by the vehicle.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    internal const string RecoveryNotOpenReasonCode = "RECOVERY_AUTHENTICATION_FAILED";

    /// <summary>An action refused for want of authorization inside the session the server opened.</summary>
    internal const string RecoveryActionNotOpenText = "这项恢复操作不在本次开放的范围内，请联系系统维护人员。";

    private RecoveryRefusal? _lastRecoveryRefusal;

    /// <summary>Numbers each recovery press, so the dialog after a press reads only that press's refusal.</summary>
    private long _recoveryPress;

    /// <summary>The session this process last saw the server open, until its CLOSED. Read to place a refusal.</summary>
    private string? _openRecoverySessionId;

    /// <summary>
    /// The server last refused a recovery, or closed its session, for want of authorization, for the recovery on file now.
    /// </summary>
    public bool IsRecoveryNotOpen =>
        Volatile.Read(ref _lastRecoveryRefusal) is { NotOpen: true, Scope.Attempt: not null } refusal
        && refusal.Scope == RecoveryScope(Volatile.Read(ref _lastRecoveryState));

    /// <summary>
    /// What the refusal dialog of a recovery entry says for the refusal of the last press, when it has words of its own;
    /// <c>null</c> keeps the entry's own sentence -- also when that press was turned away before reaching the server.
    /// </summary>
    public string? LastRecoveryRefusalText =>
        Volatile.Read(ref _lastRecoveryRefusal) is { } refusal && refusal.Press == Interlocked.Read(ref _recoveryPress)
            ? refusal switch
            {
                { NotOpen: true } when IsRecoveryNotOpen => OnboardCommandRejectionText.Describe(RecoveryNotOpenReasonCode),
                { InsideSession: true } => RecoveryActionNotOpenText,
                _ => null,
            }
            : null;

    /// <summary>A recovery press begins: refusals recorded from here on are this press's.</summary>
    private void BeginRecoveryPress() => Interlocked.Increment(ref _recoveryPress);

    internal int OperatorPressCountForTest => _authorizationRequestedInThisProcess.Count;

    /// <summary>
    /// Records a recovery refusal before it is published -- so the screen refreshed by that event reads it -- and returns
    /// the operator's line for it. Any reason replaces the last refusal.
    /// </summary>
    private string NoteRecoveryRefusal(string reasonCode)
    {
        bool authorization = string.Equals(reasonCode, RecoveryNotOpenReasonCode, StringComparison.Ordinal);
        bool insideSession = authorization && Volatile.Read(ref _openRecoverySessionId) is not null;
        Volatile.Write(
            ref _lastRecoveryRefusal,
            new RecoveryRefusal(
                authorization && !insideSession,
                insideSession,
                RecoveryScope(Volatile.Read(ref _lastRecoveryState)),
                Interlocked.Read(ref _recoveryPress)));
        return insideSession
            ? $"{RecoveryActionNotOpenText}（{reasonCode}）{OnboardCommandRejectionText.RecoveryBlockedGuidance}"
            : OnboardCommandRejectionText.DescribeRecoveryBlocked(reasonCode);
    }

    /// <summary>The server opened, or reports open, a recovery session: the authorization exists.</summary>
    private void NoteRecoverySessionOpened(string exceptionRecoverySessionId)
    {
        Volatile.Write(ref _openRecoverySessionId, exceptionRecoverySessionId);
        Volatile.Write(ref _lastRecoveryRefusal, null);
    }

    /// <summary>
    /// A CLOSED said for the first time in this process. Only the close of the session this process saw open counts: a
    /// replay of an earlier session's close must not light the line again (review L3).
    /// </summary>
    private void NoteRecoverySessionClosed(string exceptionRecoverySessionId, string? closedReason)
    {
        if (Interlocked.CompareExchange(ref _openRecoverySessionId, null, exceptionRecoverySessionId)
                != exceptionRecoverySessionId
            || !string.Equals(closedReason, RecoveryNotOpenReasonCode, StringComparison.Ordinal))
        {
            return;
        }

        Volatile.Write(
            ref _lastRecoveryRefusal,
            new RecoveryRefusal(true, false, RecoveryScope(Volatile.Read(ref _lastRecoveryState)), Press: -1));
    }

    /// <summary>The recovery a refusal is about: the unsettled attempt and its demand, or none.</summary>
    private static (string? Attempt, string? Demand) RecoveryScope(WireToGateRecoveryState? state) =>
        state is null || state.UnsettledSlotOperationAttemptId is null
            ? (null, null)
            : (state.UnsettledSlotOperationAttemptId, state.OperationContext?.DemandId);

    /// <param name="Press">The press it answered; -1 for a close, which answers none.</param>
    private sealed record RecoveryRefusal(
        bool NotOpen,
        bool InsideSession,
        (string? Attempt, string? Demand) Scope,
        long Press);
}

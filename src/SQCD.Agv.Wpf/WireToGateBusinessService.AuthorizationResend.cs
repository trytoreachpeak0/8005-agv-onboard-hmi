using System.Collections.Concurrent;
using System.IO;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The two recovery authorization requests nothing replays -- <c>LoadCompensationRequested</c> and
/// <c>LoadCorrectionRequested</c> -- asked for again when a new session comes up and their command never came
/// (onboard-hmi#236).
/// </summary>
/// <remarks>
/// <para>
/// Both are REQUESTs with <c>durableBeforeSend=false</c>: written to the socket, answered by nothing, kept by no
/// outbox. The server authorizes only on them, and nothing on the server moves a compensation out of
/// AwaitingAuthorization or opens a correction by itself. One lost with the link left both ends waiting for each
/// other for good, with nothing on the screen saying so (hmi#222 rig run 36909685924).
/// </para>
/// <para>
/// Asking again is safe on the server: it keys both by their business id -- <c>recoveryActionId</c>,
/// <c>correctionId</c> -- authorizes one it has not authorized, and re-sends the persisted command for one it has.
/// The request carries a messageId of its own each time and the first press's content from the journal, which is
/// what the inbox and the correction's content check need.
/// </para>
/// <para>
/// <b>Only within the process that asked.</b> The command this earns pulses the slots' unlock outputs as soon as it
/// arrives -- the executor checks that the vehicle stands still, and asks nobody. Within one process the operator
/// pressed moments ago, a link drop away. After a restart the press can be arbitrarily old and nobody need be at the
/// vehicle, so then the vehicle only says the authorization may be lost and leaves the press to a person who has
/// looked at the slots (decided by the coordinator on onboard-hmi#236).
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    /// <summary>
    /// How long after the press this process still asks again by itself. Past it the vehicle treats the press as one
    /// a restart would have lost: it shows the operator, and asks nobody (onboard-hmi#236 review S1).
    /// </summary>
    /// <remarks>
    /// The same reason as the restart rule: the command this earns opens doors with nobody asked. A link that comes
    /// back within minutes of the press finds the operator still at the vehicle; one that comes back much later may
    /// not, and then a person who has looked at the slots presses again.
    /// </remarks>
    internal static readonly TimeSpan AuthorizationResendWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The vectors whose authorization request this process got as far as sending, with when. In memory on purpose:
    /// a restart is exactly what must forget them.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _authorizationRequestedInThisProcess =
        new(StringComparer.Ordinal);

    private long _authorizationReviewedGeneration = long.MinValue;

    /// <summary>
    /// Recorded before the send, so a send that fails on a link already down is still asked again. Only a press moves
    /// the time on; a resend does not. The window runs from the last time a person asked: a resend moving it would let
    /// a link that keeps dropping carry the press forward indefinitely, and a compensation pressed long ago would open
    /// doors with nobody shown anything (onboard-hmi#236 review S-A, probe: 8 minutes after the press, three requests,
    /// the compensation ran, no prompt).
    /// </summary>
    private void MarkAuthorizationRequested(WireToGateRecoveryVectorContext vector) =>
        MarkOperatorPress(vector.PrimaryId);

    /// <summary>Once per session generation, when it can carry a request.</summary>
    private void ReviewUnauthorizedRecoveryVector(WireToGateSessionSnapshot session)
    {
        if (!session.Connected
            || session.SessionGeneration is not { } generation
            || session.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired)
            || Interlocked.Exchange(ref _authorizationReviewedGeneration, generation) == generation)
        {
            return;
        }

        TrackTask(ReviewUnauthorizedRecoveryVectorAsync(generation, _stopping.Token));
    }

    private async Task ReviewUnauthorizedRecoveryVectorAsync(long generation, CancellationToken cancellationToken)
    {
        WireToGateRecoveryVectorContext? reviewed = null;
        try
        {
            // Read past the cache on purpose: this runs on every new session, and refreshing the cache here would
            // move what every entry gate shows at a moment nothing else chose. Only a vector waiting on its
            // authorization goes on, and the resend below reads the way a press does.
            WireToGateRecoveryState state = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (AwaitingAuthorization(state) is not { } vector)
            {
                LogAuthorizationReview(generation, state.RecoveryVector, "无待授权的补偿或修正");
                return;
            }

            reviewed = vector;
            if (!_authorizationRequestedInThisProcess.TryGetValue(vector.PrimaryId, out DateTimeOffset requestedAt))
            {
                PublishAuthorizationUnknown(vector, "授权请求可能已在断线或重启时丢失，服务端尚未下发对应命令");
                LogAuthorizationReview(generation, vector, "本进程未申请过，已提示操作员");
                return;
            }

            if (PastResendWindow(vector, requestedAt))
            {
                LogAuthorizationReview(generation, vector, $"上次按下于 {requestedAt:O}，已超时限，已提示操作员");
                return;
            }

            // RunRecoveryRequestAsync answers a failure with RECOVERY_BLOCKED and false; the resend itself answers true
            // whenever there was nothing to do, so false here is a failure and is shown the same way as below.
            if (!await RunRecoveryRequestAsync(
                    vector.VectorType,
                    () => ResendAuthorizationRequestAsync(vector.PrimaryId, generation, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                PublishAuthorizationUnknown(vector, "重连后自动重新申请没有成功");
                LogAuthorizationReview(generation, vector, "重新申请失败，已提示操作员");
                return;
            }

            LogAuthorizationReview(generation, vector, "已重新申请或已无需申请");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException)
        {
            // The review itself failed -- the journal could not be read, say. A log line alone would leave the operator
            // where this ticket started, so the screen says it too (onboard-hmi#236 review N4).
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"重连后检查授权请求未完成：generation={generation}，reason={exception.Message}。",
                exception);
            PublishAuthorizationUnknown(reviewed, $"重连后检查授权状态时出错（{exception.Message}）");
        }
    }

    /// <summary>
    /// Whether the last press is more than <see cref="AuthorizationResendWindow"/> ago, and if so, shows the operator.
    /// </summary>
    private bool PastResendWindow(WireToGateRecoveryVectorContext vector, DateTimeOffset requestedAt)
    {
        if (_clock.Now - requestedAt <= AuthorizationResendWindow)
        {
            return false;
        }

        PublishAuthorizationUnknown(
            vector,
            $"授权请求可能已在断线时丢失，而离上次按下已超过 {AuthorizationResendWindow.TotalMinutes:0} 分钟，车辆不再自动重新申请");
        return true;
    }

    /// <summary>
    /// One line per session generation saying what the review concluded, so a field log answers "did it ask again,
    /// and why not" without anyone reading the code.
    /// </summary>
    private void LogAuthorizationReview(long generation, WireToGateRecoveryVectorContext? vector, string outcome) =>
        _logger.Write(
            LogSeverity.Information,
            nameof(WireToGateBusinessService),
            $"重连后检查授权请求：generation={generation}，vector={vector?.VectorType ?? "none"}/{vector?.PrimaryId ?? "none"}，"
            + $"commandBound={vector?.CommandContentSha256 is not null}，结论={outcome}。");

    /// <summary>
    /// What the operator is shown when the vehicle will not ask again by itself. It says what the vehicle does not
    /// know, and both ways it can turn out: if the server did authorize, its command may still arrive on its own --
    /// the server replays a bound command into every new session -- and is held for the operator to confirm, since
    /// this process holds no press within the window for it (onboard-hmi#239); if it did not, nothing happens until
    /// someone presses (onboard-hmi#236 review S1).
    /// </summary>
    private void PublishAuthorizationUnknown(WireToGateRecoveryVectorContext? vector, string why)
    {
        string subject = vector is null
            ? "补偿清空或装货修正"
            : $"{AuthorizationSubject(vector)}（{vector.VectorType}）{FormatSlots(vector.Slots)}";
        PublishOperatorEvent(
            $"recovery-authorization-unknown:{vector?.PrimaryId ?? "review"}:{why}",
            "RECOVERY_AUTHORIZATION_UNKNOWN",
            $"车辆 {_session.Client.AgvId} 的{subject}授权状态无法确认：{why}。"
            + "如果服务端此前已经授权，命令到车后会先扣住、不会自动开锁，屏上会出现「确认执行」「不执行」，"
            + "请先确认仓门附近安全再决定；如果一直没有出现，请确认现场后再按一次。 ");
    }

    /// <summary>
    /// Asks again under the recovery request gate, against the journal as it stands then: a press or the command
    /// itself may have got there first.
    /// </summary>
    private async Task<bool> ResendAuthorizationRequestAsync(
        string primaryId,
        long generation,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken)
            .ConfigureAwait(false);
        if (AwaitingAuthorization(state) is not { } vector
            || vector.PrimaryId != primaryId
            || _session.Current.SessionGeneration != generation)
        {
            // Answered meanwhile, or the session moved on and its own review asks.
            return true;
        }

        // Asked again here, just before the send: the wait for the gate can itself run past the window, and the
        // premise the review checked has to hold when the request leaves, not when it queued (review S-A).
        if (!_authorizationRequestedInThisProcess.TryGetValue(vector.PrimaryId, out DateTimeOffset requestedAt)
            || PastResendWindow(vector, requestedAt))
        {
            return true;
        }

        if (vector.VectorType == WireToGateRecoveryVectorTypes.LoadCompensation)
        {
            // A session the server already closed takes no authorization; it would be refused, and the journal
            // keeps the vector until the closing snapshot settles it.
            if (Volatile.Read(ref _recoverySessionSnapshot) is { State: "CLOSED" } closed
                && closed.ExceptionRecoverySessionId == vector.ExceptionRecoverySessionId)
            {
                return true;
            }

            await SendLoadCompensationRequestAsync(vector, pressedByOperator: false, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (state.RecoveryReason is not { } reason)
            {
                // The server compares the whole request with the one it may have accepted; without the first
                // press's reason this one cannot be that request.
                throw new InvalidDataException("RECOVERY_REASON_REQUIRED");
            }

            await SendLoadCorrectionRequestAsync(vector, reason, pressedByOperator: false, cancellationToken)
                .ConfigureAwait(false);
        }

        PublishOperatorEvent(
            $"recovery-authorization-resent:{vector.PrimaryId}",
            "RECOVERY_VECTOR_REQUESTED",
            $"车辆 {_session.Client.AgvId} 的{AuthorizationSubject(vector)}（{vector.VectorType}）{FormatSlots(vector.Slots)}授权请求可能随断线丢失，已在重连后按首次内容重新申请，等待服务端下发对应命令。 ");
        return true;
    }

    /// <summary>
    /// The vector waiting on an authorization only a request earns: prepared, no command bound, nothing opened.
    /// </summary>
    private static WireToGateRecoveryVectorContext? AwaitingAuthorization(WireToGateRecoveryState state) =>
        state.RecoveryVector is { CommandContentSha256: null } vector
        && vector.VectorType is WireToGateRecoveryVectorTypes.LoadCompensation
            or WireToGateRecoveryVectorTypes.LoadCorrection
        && state.ProvenRecoveryCheckpoint == WireToGateRecoveryCheckpoint.Prepared
        && state.ActiveUnlockSlots.Count == 0
            ? vector
            : null;

    private static string AuthorizationSubject(WireToGateRecoveryVectorContext vector) =>
        vector.VectorType == WireToGateRecoveryVectorTypes.LoadCompensation ? "补偿清空" : "装货修正";
}

using System.Collections.Concurrent;
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
    /// The vectors whose authorization request this process got as far as sending. In memory on purpose: a restart
    /// is exactly what must forget them.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _authorizationRequestedInThisProcess = new(StringComparer.Ordinal);

    private long _authorizationReviewedGeneration = long.MinValue;

    /// <summary>Recorded before the send, so a send that fails on a link already down is still asked again.</summary>
    private void MarkAuthorizationRequested(WireToGateRecoveryVectorContext vector) =>
        _authorizationRequestedInThisProcess[vector.PrimaryId] = 0;

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
        try
        {
            // Read past the cache on purpose: this runs on every new session, and refreshing the cache here would
            // move what every entry gate shows at a moment nothing else chose. Only a vector waiting on its
            // authorization goes on, and the resend below reads the way a press does.
            WireToGateRecoveryState state = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (AwaitingAuthorization(state) is not { } vector)
            {
                return;
            }

            if (!_authorizationRequestedInThisProcess.ContainsKey(vector.PrimaryId))
            {
                PublishOperatorEvent(
                    $"recovery-authorization-unknown:{vector.PrimaryId}",
                    "RECOVERY_AUTHORIZATION_UNKNOWN",
                    $"车辆 {_session.Client.AgvId} 的{AuthorizationSubject(vector)}（{vector.VectorType}）{FormatSlots(vector.Slots)}授权请求可能已在断线或重启时丢失，服务端尚未下发对应命令。"
                    + "车辆不会自动重新申请；如果仓门没有开始动作，请确认现场后再按一次。 ");
                return;
            }

            await RunRecoveryRequestAsync(
                    vector.VectorType,
                    () => ResendAuthorizationRequestAsync(vector.PrimaryId, generation, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
            return false;
        }

        if (vector.VectorType == WireToGateRecoveryVectorTypes.LoadCompensation)
        {
            // A session the server already closed takes no authorization; it would be refused, and the journal
            // keeps the vector until the closing snapshot settles it.
            if (Volatile.Read(ref _recoverySessionSnapshot) is { State: "CLOSED" } closed
                && closed.ExceptionRecoverySessionId == vector.ExceptionRecoverySessionId)
            {
                return false;
            }

            await SendLoadCompensationRequestAsync(vector, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (state.RecoveryReason is not { } reason)
            {
                // The server compares the whole request with the one it may have accepted; without the first
                // press's reason this one cannot be that request.
                return false;
            }

            await SendLoadCorrectionRequestAsync(vector, reason, cancellationToken).ConfigureAwait(false);
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

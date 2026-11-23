using System.IO;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// Closes out a load cancellation the server authorized and this vehicle did not carry out, once a session can carry its
/// result (8005-agv-onboard-hmi#409, split from onboard-hmi#392 item 5).
/// </summary>
/// <remarks>
/// <para>
/// The authorization puts the cancellation's vector on file before anything runs. What runs next can stop short of a
/// result: the motion check refuses it <c>VEHICLE_NOT_READY</c> while the vehicle's motion cannot be read as stopped -- an
/// emergency stop, agv02 @217 on 2026-10-21 -- or the process goes away in between. Nothing ran it after that: the vector
/// stayed on file, the resent <c>SlotOperationCommand</c> was ignored as taken over by the cancellation, and the server's
/// journey stayed in <c>AwaitingLoadResult</c> until someone pressed the entry again.
/// </para>
/// <para>
/// Under protocol 6.0.0 the cancellation opens no door (CV-LOAD-CANCELLATION-ALL-EMPTY): its vector reads the slots, waits
/// on a door the load left open, and reports <c>ALL_EMPTY</c> or <c>ALL_EMPTY_DOOR_UNPROVEN</c>. So unlike a compensation's
/// command (onboard-hmi#239) it needs no person at the vehicle, and the vehicle runs it by itself, on the path a second
/// press takes. Once only: a result on file -- acknowledged, owed or given up -- is the handshake's to replay or a
/// maintainer's to check, and executing again would put a second result under the same key.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private string? _authorizedLoadCancellationReviewKey;

    /// <summary>Once per session generation and readiness that can carry a recovery result.</summary>
    private void CloseOutAuthorizedLoadCancellation(WireToGateSessionSnapshot session)
    {
        if (!session.Connected
            || session.SessionGeneration is not { } generation
            || session.Readiness is not (WireToGateSessionReadiness.Ready
                or WireToGateSessionReadiness.RecoveryRequired))
        {
            return;
        }

        string key = $"{generation}|{session.Readiness}";
        if (string.Equals(
                Interlocked.Exchange(ref _authorizedLoadCancellationReviewKey, key),
                key,
                StringComparison.Ordinal))
        {
            return;
        }

        TrackTask(CloseOutAuthorizedLoadCancellationAsync(generation, _stopping.Token));
    }

    private async Task CloseOutAuthorizedLoadCancellationAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            // Read past the cache, as the authorization review does: only a cancellation vector goes on to the gate.
            WireToGateRecoveryState state = await _session.Journal.ReadRecoveryStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (state.RecoveryVector is not { VectorType: WireToGateRecoveryVectorTypes.LoadCancellation })
            {
                return;
            }

            await RunRecoveryRequestAsync(
                    WireToGateRecoveryVectorTypes.LoadCancellation,
                    () => CloseOutAuthorizedLoadCancellationCoreAsync(generation, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"会话就绪后检查已授权未执行的装货取消未完成：generation={generation}，reason={exception.Message}。",
                exception);
        }
    }

    /// <summary>
    /// Under the recovery request gate, against the journal as it stands then: a press may have run the cancellation while
    /// this waited for the gate.
    /// </summary>
    private async Task<bool> CloseOutAuthorizedLoadCancellationCoreAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        WireToGateRecoveryState state = await ReadRecoveryStateCachedAsync(cancellationToken).ConfigureAwait(false);
        if (state.RecoveryVector is not { VectorType: WireToGateRecoveryVectorTypes.LoadCancellation } vector
            || _session.Current.SessionGeneration != generation)
        {
            return true;
        }

        if (await LoadCancellationResultOnFileAsync(LoadCancellationSubjectKeyOf(vector), cancellationToken)
                .ConfigureAwait(false) is { } onFile)
        {
            _logger.Write(
                LogSeverity.Information,
                nameof(WireToGateBusinessService),
                $"会话就绪：装货取消的结果已在发件箱，不再执行：cancellation={vector.PrimaryId}，"
                + $"resultKey={onFile.DeduplicationKey}，acknowledged={onFile.Acknowledged}，generation={generation}。");
            return true;
        }

        await RefuseWhileAwaitingManualCheckAsync(vector, state, cancellationToken).ConfigureAwait(false);
        if (!WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector))
        {
            // As a second press over this vector is (onboard-hmi#174): not run while a severe safety fault is latched. The
            // entry stays offered, and a press after the reset runs it.
            RefuseDoorOpeningCancellationWhileLatched();
        }

        _logger.Write(
            LogSeverity.Warning,
            nameof(WireToGateBusinessService),
            $"会话就绪：装货取消已获授权但未执行，现在收口（不开仓门，按实时IO判空上报）：cancellation={vector.PrimaryId}，"
            + $"attempt={vector.SlotOperationAttemptId ?? "null"}，generation={generation}。");
        if (WireToGateRecoveryVectorTypes.IsLoadCancellationBeforeSublot(vector))
        {
            return await ReportLoadCancellationBeforeSublotAsync(vector, cancellationToken).ConfigureAwait(false);
        }

        PublishOperatorResponse(
            "RECOVERY_VECTOR_AUTHORIZED",
            $"装货取消此前已获服务端授权、尚未执行，现在继续：正在核对{FormatSlots(vector.Slots)}为空，不开仓门。 ");
        return await ExecuteRecoveryVectorAndReportAsync(
                vector,
                correction: false,
                cancellationToken,
                result => SendRecoveryVectorResultAsync(
                    vector,
                    LoadCancellationResultKey(vector),
                    result,
                    cancellationToken),
                releaseSettledFailure: () => ForgetSettledRecoveryVectorAsync(vector, cancellationToken))
            .ConfigureAwait(false);
    }
}

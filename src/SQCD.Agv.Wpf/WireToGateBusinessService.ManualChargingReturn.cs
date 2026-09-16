using System.IO;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator's entry for <c>CV-MANUAL-CHARGING-RETURN</c>: after manual charging, an administrator
/// asks the control server to put the vehicle back into eligibility evaluation.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-14 only <c>WireToGateSessionClient.RequestManualChargingReturnToServiceAsync</c> could
/// send this request, and nothing an operator could reach called it, so the vector had no path from
/// the HMI at all.
/// </para>
/// <para>
/// The hold itself is never touched here (<c>NEVER_CLEAR_HOLD_LOCALLY</c>): whatever the server
/// decides, <see cref="WireToGateJourneySnapshot.VehicleBusinessState"/> changes only when the server
/// publishes a new vehicle business state. The request needs the same verified administrator the
/// recovery entries need (<c>REQUIRE_VERIFIED_ADMINISTRATOR</c>); the proof is checked to be configured
/// but not sent, because the message has no field for it.
/// </para>
/// </remarks>
public sealed partial class WireToGateBusinessService
{
    private const string ManualChargingReturnAccepted = "RETURNED_TO_ELIGIBILITY_EVALUATION";

    public bool CanRequestManualChargingReturnToService =>
        CanUseRecoveryOperator(requireProof: true);

    public Task<bool> RequestManualChargingReturnToServiceAsync(
        string reason = "手动充电结束，申请恢复业务资格评估。",
        double? observedBatteryPercent = null,
        CancellationToken cancellationToken = default) =>
        RunManualChargingReturnAsync(reason, observedBatteryPercent, cancellationToken);

    private async Task<bool> RunManualChargingReturnAsync(
        string reason,
        double? observedBatteryPercent,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!CanRequestManualChargingReturnToService)
        {
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                "返回服务需要已验证的管理员和在线会话，本次未发送请求。 ");
            return false;
        }

        await _recoveryRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireToGateOperatorContextPayload administrator = ReadOperatorContext();
            _ = ReadRecoveryProof();
            string requestId = Guid.NewGuid().ToString("D");
            ManualChargingReturnToServiceResultPayload result = await _session
                .RequestManualChargingReturnToServiceAsync(
                    requestId,
                    new ManualChargingReturnToServiceRequestedPayload(
                        requestId,
                        administrator,
                        _recoveryOptions.AdministratorRole,
                        RequireReason(reason),
                        observedBatteryPercent),
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(result.Outcome, ManualChargingReturnAccepted, StringComparison.Ordinal))
            {
                PublishOperatorResponse(
                    "MANUAL_CHARGING_RETURN_ACCEPTED",
                    "服务端已受理返回服务请求，正在重新评估车辆业务资格；手动充电保持以服务端下发的状态为准。 ");
                return true;
            }

            string reasonCode = result.Problem?.ReasonCode ?? result.Outcome;
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"返回服务请求被服务端拒绝：requestId={requestId}，reason={reasonCode}。");
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"服务端未受理返回服务请求：{reasonCode}。车辆保持原状态。 ");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException)
        {
            _logger.Write(
                LogSeverity.Warning,
                nameof(WireToGateBusinessService),
                $"返回服务请求未完成：reason={exception.Message}。",
                exception);
            PublishOperatorResponse(
                "RECOVERY_BLOCKED",
                $"返回服务请求未完成：{exception.Message}。车辆保持原状态。 ");
            return false;
        }
        finally
        {
            _recoveryRequestGate.Release();
        }
    }
}

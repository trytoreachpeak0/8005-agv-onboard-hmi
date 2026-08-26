using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// Prevents the legacy rule protocol from becoming a second business authority
/// when WIRE_TO_GATE is enabled.  The formal session/business bridge owns that
/// mode; this adapter keeps the existing controller safely stopped until the HMI
/// entry flow is connected to SublotSubmitted.
/// </summary>
public sealed class DisabledRuleGateway : IRuleGateway
{
    public DisabledRuleGateway()
    {
        _ = ConnectionChanged;
        _ = VisitChanged;
    }

    public bool IsConnected => false;

    public VisitContext? CurrentVisit => null;

    public event EventHandler<ValueChangedEventArgs<bool>>? ConnectionChanged;

    public event EventHandler<ValueChangedEventArgs<VisitContext?>>? VisitChanged;

    public Task StartAsync(CancellationToken applicationStopping) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ScanAuthorization> VerifyScanAsync(
        ScanVerificationRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScanAuthorization.Rejected(
            request.Sublot,
            "WIRE_TO_GATE_ACTIVE",
            "已启用 WIRE_TO_GATE，扫码必须通过服务端旅程流程。"));

    public Task<bool> ReportOperationAsync(
        OperationResult result,
        CancellationToken cancellationToken) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

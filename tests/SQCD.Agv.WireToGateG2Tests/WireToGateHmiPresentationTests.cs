using SQCD.Agv.Core;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

public sealed class WireToGateHmiPresentationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
    public void ReadyWireToGateSessionDoesNotRemainInLegacyConnectingState()
    {
        WireToGateSessionSnapshot session = Session(WireToGateSessionReadiness.Ready);

        WireToGateHmiBanner banner = WireToGateHmiPresentation.Create(
            session,
            operation: null,
            canSubmit: false);

        Assert.Equal("就绪", banner.StateText);
        Assert.Contains("上层会话", banner.Guidance, StringComparison.Ordinal);
        Assert.False(banner.HasWarning);
        Assert.False(banner.HasError);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
    public void RecoveryRequiredIsVisibleWithTheServerReason()
    {
        WireToGateSessionSnapshot session = Session(
            WireToGateSessionReadiness.RecoveryRequired,
            "DEPARTURE_SAFETY_NOT_READY");

        WireToGateHmiBanner banner = WireToGateHmiPresentation.Create(
            session,
            operation: null,
            canSubmit: false);

        Assert.Equal("需要恢复", banner.StateText);
        Assert.Contains("DEPARTURE_SAFETY_NOT_READY", banner.Guidance, StringComparison.Ordinal);
        Assert.True(banner.HasWarning);
    }

    /// <summary>
    /// 扫码前取消未结时，提示区说明正在取消、不邀请扫码（onboard-hmi#76 审查）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-BEFORE-LOAD")]
    public void APendingCancellationBeforeAnySublotReplacesTheScanPrompt()
    {
        WireToGateHmiBanner banner = WireToGateHmiPresentation.Create(
            Session(WireToGateSessionReadiness.Ready),
            operation: null,
            canSubmit: false,
            loadCancellationPending: true);

        Assert.Equal("取消中", banner.StateText);
        Assert.Contains("正在取消", banner.Guidance, StringComparison.Ordinal);
        Assert.Contains("暂停扫码", banner.Guidance, StringComparison.Ordinal);
        Assert.True(banner.HasWarning);
    }

    [Fact]
    public void OperationProjectionOverridesReadyBannerAndTargetsEveryPhysicalSlot()
    {
        WireToGateHmiOperationSnapshot operation = new(
            "11111111-1111-4111-8111-111111111111",
            OperationType.Load,
            [1, 2],
            WireToGateHmiOperationStage.WaitingOperator,
            "请向1、2号仓放入货物并关门。",
            Now);
        WireToGateHmiBanner banner = WireToGateHmiPresentation.Create(
            Session(WireToGateSessionReadiness.Ready),
            operation,
            canSubmit: false);
        LockerCardViewModel first = new(0);
        LockerCardViewModel second = new(1);
        LockerCardViewModel third = new(2);
        IoSnapshot io = KnownIo();

        first.Update(io.GetLocker(0), activeOperation: null, operation);
        second.Update(io.GetLocker(1), activeOperation: null, operation);
        third.Update(io.GetLocker(2), activeOperation: null, operation);

        Assert.Equal("仓位操作中", banner.StateText);
        Assert.True(first.IsTarget);
        Assert.True(second.IsTarget);
        Assert.False(third.IsTarget);
        Assert.Equal("请放入货物并关门", first.TargetText);
    }

    [Fact]
    public void FailedOperationRemainsVisibleAsRecoveryRequired()
    {
        WireToGateHmiOperationSnapshot operation = new(
            "22222222-2222-4222-8222-222222222222",
            OperationType.Unload,
            [4],
            WireToGateHmiOperationStage.RecoveryRequired,
            "4号仓操作未完成，需要管理员恢复。",
            Now);

        WireToGateHmiBanner banner = WireToGateHmiPresentation.Create(
            Session(WireToGateSessionReadiness.Ready),
            operation,
            canSubmit: false);

        Assert.Equal("需要恢复", banner.StateText);
        Assert.True(banner.HasWarning);
        Assert.Contains("管理员恢复", banner.Guidance, StringComparison.Ordinal);
    }

    private static WireToGateSessionSnapshot Session(
        WireToGateSessionReadiness readiness,
        params string[] reasons) =>
        new(
            Connected: readiness != WireToGateSessionReadiness.Disconnected,
            SessionGeneration: readiness == WireToGateSessionReadiness.Disconnected ? null : 1,
            Readiness: readiness,
            ReasonCodes: reasons,
            CapabilityVersion: 1,
            SafetyStateVersion: 1,
            UpdatedAt: Now);

    private static IoSnapshot KnownIo() => new(
        true,
        Enumerable.Range(0, 8)
            .Select(index => new LockerSnapshot(index, index + 1, false, true, true, Now))
            .ToArray(),
        Now);
}

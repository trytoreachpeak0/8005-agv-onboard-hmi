using SQCD.Agv.Core;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 「车上没有出口」那一行的卸货分支（trytoreachpeak0/8005-agv-onboard-hmi#375，PR #371 审查 S3）：结果交服务端的是一次卸货时，
/// 说明里写「卸货」。
/// </summary>
/// <remarks>
/// 卸货在门口执行到一半车载程序退出，重启后中断结算按实时 IO 结算：3 号仓还有货，卸货以 UNKNOWN 收尾、结果待服务端答。恢复开关
/// 关着（出厂就是这样），交接与强制恢复都在开关后面，车上一个入口都没有。把卸货写成装货文案，这一条红。
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task AnUnknownUnloadWithTheSwitchOffSaysItIsTheUnloadThatHasNoExit()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            armedUnloadOverSettledLoad: true,
            resumeAfterRepairEnabled: false);

        await RecoveryVectorHarness.WaitUntilAsync(
            () => harness.Business.NoOnboardExitNotice is not null,
            "the notice over the unload's result waiting for the server",
            token);

        Assert.Equal(WireToGateSessionReadiness.RecoveryRequired, harness.Session.Current.Readiness);
        Assert.False(harness.Business.CanRequestFaultCargoHandoff);
        Assert.False(harness.Business.CanRequestForcedMechanicalRecovery);
        Assert.Equal(
            "这次卸货的结果未知，已交服务端处理。车上没有可以处理它的按钮，请联系系统维护人员在服务端处理。",
            harness.Business.NoOnboardExitNotice);
    }
}

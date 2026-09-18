using System.Text.Json;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 异常处置会话的原因（CP-0005 第五节、第八节实现票 1，<c>trytoreachpeak0/8005-agv-onboard-hmi#109</c>）：管理员填写的
/// 判定人、故障类别与现场说明写进 <c>ExceptionRecoverySessionRequested.reason</c>；没填时照旧是按恢复动作写死的那句。
/// </summary>
/// <remarks>
/// 过渡办法（重启车载端、中断结算报 <c>UNKNOWN</c>）留下的原因码与「程序意外退出」一样，判定人与说明只能靠这个字段进系统。
/// 协议字段本来就有（非空自由字符串），这里不改协议。
/// </remarks>
public sealed partial class RecoveryVectorG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task TheAdministratorsReasonIsTheRecoverySessionRequestsReason()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            cargoInTargetSlots: true);
        const string Reason = "张三，锁：1号仓锁舌断，门已关但锁传感器读未锁";

        await harness.Business.RequestResumeAfterRepairAsync($"  {Reason}  ", token);

        Assert.Equal(Reason, SessionRequestReason(harness));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task WithNoReasonEnteredTheResumeRequestKeepsItsFixedText()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            cargoInTargetSlots: true);

        await harness.Business.RequestResumeAfterRepairAsync(null, token);

        Assert.Equal("现场维修完成，申请恢复原仓位操作。", SessionRequestReason(harness));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task WithNoReasonEnteredTheCompensationRequestKeepsItsFixedText()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RecoveryVectorHarness harness = await RecoveryVectorHarness.StartAsync(
            token,
            server => server.RecoverySlotOperationAttemptId = AttemptId,
            loadAlreadySettled: true);

        Assert.True(await harness.Business.RequestLoadCompensationAsync("   ", token));

        Assert.Equal("现场确认装货无法继续，申请补偿清空目标仓位。", SessionRequestReason(harness));
    }

    private static string? SessionRequestReason(RecoveryVectorHarness harness)
    {
        string line = Assert.Single(harness.ResultsOfType("ExceptionRecoverySessionRequested"));
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("payload").GetProperty("reason").GetString();
    }
}

using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 拒收原因的中文文案（批次5-28，onboard-hmi#77）：候选包为 <c>SublotRejected</c> 新增的三个码与复用的
/// <c>EXPECTED_BASKET_COUNT_MISMATCH</c> 各有专门文案，其余码走兜底并显示原始码。
/// </summary>
/// <remarks>
/// 期望文案按票面举例逐字写死，不从实现里取。放在本项目是因为文案类在 <c>net8.0-windows</c> 的 WPF 程序集里。
/// 不带 trait：纯呈现，行为由 <see cref="SublotRejectedAfterEntryG2Tests"/> 证明。
/// </remarks>
public sealed class WireToGateSublotRejectionTextTests
{
    [Theory]
    [InlineData("SUBLOT_NOT_IN_DISPATCH_SCOPE", "子批号不在本次派车范围内")]
    [InlineData("SUBLOT_BOX_COUNT_UNAVAILABLE", "查不到该子批的箱数")]
    [InlineData("PACKAGE_CAPACITY_UNRESOLVED", "该 PACKAGE 的花篮容量未登记或有冲突")]
    [InlineData("EXPECTED_BASKET_COUNT_MISMATCH", "花篮数量与已预留仓位数不符")]
    public void EachRejectionCodeTheCandidateNamesHasItsOwnSentence(string reasonCode, string expected)
    {
        Assert.Equal(expected, WireToGateSublotRejectionText.Reason(reasonCode));
    }

    [Fact]
    public void AnyOtherCodeIsShownAsItselfRatherThanGuessed()
    {
        string reason = WireToGateSublotRejectionText.Reason("SOME_FUTURE_REASON");

        Assert.Equal("未识别的拒收原因（SOME_FUTURE_REASON）", reason);
    }

    [Fact]
    public void TheLineNamesTheRejectedEntryAndItsReason()
    {
        Assert.Equal(
            "子批 SUBLOT-042 被服务端拒收：查不到该子批的箱数。",
            WireToGateSublotRejectionText.Describe(Rejection("SUBLOT_BOX_COUNT_UNAVAILABLE", "SUBLOT-042", demandId: "11111111-1111-1111-1111-111111111111")));
    }

    [Fact]
    public void ANullDemandIdIsShownTheSameWay()
    {
        // 范围外的子批号没有需求可指，demandId 为 null；文案本来就不含 demandId，这里钉住它不因此失败或变样。
        Assert.Equal(
            "子批 OUTSIDE-001 被服务端拒收：子批号不在本次派车范围内。",
            WireToGateSublotRejectionText.Describe(Rejection("SUBLOT_NOT_IN_DISPATCH_SCOPE", "OUTSIDE-001", demandId: null)));
    }

    [Fact]
    public void TheNextStepFollowsWhetherTheOperatorCanEnterAgain()
    {
        Assert.Equal("请核对物料后重新扫码。", WireToGateSublotRejectionText.NextStep(canEnterAgain: true));
        Assert.Equal(
            "本站录入清单已变化，等待服务端新的录入请求。",
            WireToGateSublotRejectionText.NextStep(canEnterAgain: false));
    }

    private static WireToGateSublotRejection Rejection(string reasonCode, string sublot, string? demandId) =>
        new(
            "22222222-2222-4222-8222-222222222222",
            demandId,
            "77777777-7777-4777-8777-777777777777",
            reasonCode,
            1,
            sublot,
            EntryRequestKept: true,
            new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));
}

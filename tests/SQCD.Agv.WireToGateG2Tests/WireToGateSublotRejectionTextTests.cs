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
    // 站点提前结束之后迟到的扫码（onboard-hmi#199，control-server#324）。
    [InlineData("WORKLIST_REVISION_STALE", "本站作业已结束，不再接收扫码")]
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

    /// <summary>
    /// 除 <c>SLOT_INOPERABLE</c> 外，下一步只随录入请求是否保留：五个已知码与未识别码都钉住原来的两句（onboard-hmi#366）。
    /// </summary>
    [Theory]
    [InlineData("SUBLOT_NOT_IN_DISPATCH_SCOPE")]
    [InlineData("SUBLOT_BOX_COUNT_UNAVAILABLE")]
    [InlineData("PACKAGE_CAPACITY_UNRESOLVED")]
    [InlineData("EXPECTED_BASKET_COUNT_MISMATCH")]
    [InlineData("WORKLIST_REVISION_STALE")]
    [InlineData("SOME_FUTURE_REASON")]
    public void TheNextStepFollowsWhetherTheOperatorCanEnterAgain(string reasonCode)
    {
        Assert.Equal("请核对物料后重新扫码。", WireToGateSublotRejectionText.NextStep(reasonCode, canEnterAgain: true));
        Assert.Equal(
            "本站录入清单已变化，等待服务端新的录入请求。",
            WireToGateSublotRejectionText.NextStep(reasonCode, canEnterAgain: false));
    }

    /// <summary>
    /// <c>SLOT_INOPERABLE</c>：受理后有仓位被停用，这一侧可分的空仓不够（control-server#754）。重扫多少次都会被拒，
    /// 所以不论录入请求是否保留，下一步都是取消该需求并联系系统维护人员，不叫操作员重新扫码（onboard-hmi#366）。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnInoperableSlotTellsTheOperatorToCancelRatherThanScanAgain(bool canEnterAgain)
    {
        string reason = WireToGateSublotRejectionText.Reason("SLOT_INOPERABLE");
        string nextStep = WireToGateSublotRejectionText.NextStep("SLOT_INOPERABLE", canEnterAgain);

        Assert.Equal("可用的空仓位不足，有仓位已被停用", reason);
        Assert.Equal("重扫也无法装入，请取消该需求并联系系统维护人员。", nextStep);
        Assert.DoesNotContain("重新扫码", reason + nextStep, StringComparison.Ordinal);
    }

    /// <summary>
    /// 取消被拒同样按码说话：<c>WORKLIST_REVISION_STALE</c> 是「本站已结束」，其余码照旧显示原码（onboard-hmi#199）。
    /// </summary>
    [Fact]
    public void ARefusedCancellationSaysTheStopHasEndedWhenTheWorklistIsStale()
    {
        Assert.Equal(
            "服务端拒绝装货取消：本站作业已结束，无需再取消（WORKLIST_REVISION_STALE）。",
            WireToGateSublotRejectionText.LoadCancellationRefusal("WORKLIST_REVISION_STALE"));
        Assert.Equal(
            "服务端拒绝装货取消：ACTION_NOT_ALLOWED_IN_STATE。",
            WireToGateSublotRejectionText.LoadCancellationRefusal("ACTION_NOT_ALLOWED_IN_STATE"));
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

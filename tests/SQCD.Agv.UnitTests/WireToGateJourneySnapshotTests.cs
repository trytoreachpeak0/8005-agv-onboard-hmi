using SQCD.Agv.Core;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 多单多段之后 <see cref="WireToGateJourneySnapshot"/> 的两个判据都放宽了：清单可以有多项，
/// 计划点名的需求只要**属于**清单而不必是清单里唯一的那一项。这两条以前都收窄在「恰好一项」上，
/// 而收窄的方式各自不同——一个是 <c>Items.Count == 1</c>，一个是 <c>SingleOrDefault()</c>，后者
/// 在两项时**抛异常**而不是判不一致。
/// </summary>
public sealed class WireToGateJourneySnapshotTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MultiItemWorklistNoLongerThrowsWhenTheDemandIsChecked()
    {
        // 这是回归点本身：旧实现用 SingleOrDefault()，两项时抛 InvalidOperationException。抛在
        // 这里等于抛在 UI 线程的快照更新回调上，界面会停在上一次的内容上，看不出发生了什么。
        WireToGateJourneySnapshot snapshot = Snapshot(
            Worklist(("D-1", "SUBLOT-001"), ("D-2", "SUBLOT-002")),
            Plan("D-2"));

        Assert.True(snapshot.HasConsistentDemand);
    }

    [Fact]
    public void PlanDemandThatIsAbsentFromAMultiItemWorklistIsInconsistent()
    {
        WireToGateJourneySnapshot snapshot = Snapshot(
            Worklist(("D-1", "SUBLOT-001"), ("D-2", "SUBLOT-002")),
            Plan("D-3"));

        Assert.False(snapshot.HasConsistentDemand);
    }

    [Fact]
    public void PlanWithoutADemandIsConsistentWithAnyWorklist()
    {
        // 0.2.0 起一趟行程属于整趟而不属于其中某一个需求，计划的 demandId 因此可以是 null。
        WireToGateJourneySnapshot snapshot = Snapshot(
            Worklist(("D-1", "SUBLOT-001"), ("D-2", "SUBLOT-002")),
            Plan(null));

        Assert.True(snapshot.HasConsistentDemand);
    }

    [Fact]
    public void EmptyWorklistIsConsistentButAcceptsNothing()
    {
        WireToGateJourneySnapshot snapshot = Snapshot(Worklist(), Plan("D-1"));

        Assert.True(snapshot.HasConsistentDemand);
        Assert.False(snapshot.CanAcceptSublot);
    }

    [Fact]
    public void MultiItemWorklistCanAcceptSublot()
    {
        // 旧判据是 Items.Count == 1：一次停靠有两项待装就整个录不进去。
        WireToGateJourneySnapshot snapshot = Snapshot(
            Worklist(("D-1", "SUBLOT-001"), ("D-2", "SUBLOT-002")),
            Plan("D-1"));

        Assert.True(snapshot.CanAcceptSublot);
    }

    [Fact]
    public void SingleItemWorklistStillAcceptsSublot()
    {
        WireToGateJourneySnapshot snapshot = Snapshot(Worklist(("D-1", "SUBLOT-001")), Plan("D-1"));

        Assert.True(snapshot.CanAcceptSublot);
    }

    [Fact]
    public void InconsistentDemandBlocksAcceptanceEvenWithItemsPresent()
    {
        WireToGateJourneySnapshot snapshot = Snapshot(
            Worklist(("D-1", "SUBLOT-001"), ("D-2", "SUBLOT-002")),
            Plan("D-3"));

        Assert.False(snapshot.CanAcceptSublot);
    }

    [Theory]
    [InlineData("BLOCKED", false, "SUFFICIENT")]
    [InlineData("READY", true, "SUFFICIENT")]
    [InlineData("READY", false, "LOW")]
    public void VehicleStateStillGatesAcceptanceIndependentlyOfItemCount(
        string readiness,
        bool manualChargingHold,
        string batteryState)
    {
        // 放宽的只有项数，车辆侧那三条闸门没动。
        WireToGateJourneySnapshot snapshot = new(
            new WireToGateVehicleBusinessState(
                1,
                readiness,
                manualChargingHold,
                batteryState,
                [],
                Now,
                "sha"),
            Worklist(("D-1", "SUBLOT-001"), ("D-2", "SUBLOT-002")),
            Plan("D-1"),
            Now);

        Assert.False(snapshot.CanAcceptSublot);
    }

    private static WireToGateJourneySnapshot Snapshot(
        WireToGateCurrentStopWorklist worklist,
        WireToGateUpcomingStopPlan plan) =>
        new(ReadyVehicle(), worklist, plan, Now);

    private static WireToGateVehicleBusinessState ReadyVehicle() =>
        new(1, "READY", false, "SUFFICIENT", [], Now, "sha");

    private static WireToGateCurrentStopWorklist Worklist(
        params (string DemandId, string Sublot)[] items) =>
        new(
            "ST-01",
            1,
            "33333333-3333-3333-3333-333333333333",
            items
                .Select(item => new WireToGateWorklistItem(
                    item.DemandId,
                    $"TD-{item.Sublot}",
                    item.Sublot,
                    "LOAD",
                    "PICKUP",
                    1))
                .ToArray(),
            "sha");

    private static WireToGateUpcomingStopPlan Plan(string? demandId) =>
        new(1, demandId, [new WireToGateMovementLeg("L-1", "TO_PICKUP", 1, "ST-01", "MAP-01", "ACTIVE")], "sha");
}

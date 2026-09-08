namespace SQCD.Agv.Core;

public sealed record WireToGateVehicleBusinessState(
    long Revision,
    string Readiness,
    bool ManualChargingHold,
    string BatteryState,
    IReadOnlyList<WireToGateBlockingFact> BlockingFacts,
    DateTimeOffset ObservedAt,
    string ContentSha256);

public sealed record WireToGateBlockingFact(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

public sealed record WireToGateWorklistItem(
    string DemandId,
    string TransportDemandKey,
    string Sublot,
    string WorkType,
    string StopRole,
    int ExpectedBasketCount);

public sealed record WireToGateCurrentStopWorklist(
    string StationId,
    long Revision,
    string? OperationSessionId,
    IReadOnlyList<WireToGateWorklistItem> Items,
    string ContentSha256);

public sealed record WireToGateMovementLeg(
    string MovementLegId,
    string LegType,
    int Sequence,
    string StationId,
    string MapId,
    string State);

public sealed record WireToGateUpcomingStopPlan(
    long Revision,
    string? DemandId,
    IReadOnlyList<WireToGateMovementLeg> Legs,
    string ContentSha256);

public sealed record WireToGateJourneySnapshot(
    WireToGateVehicleBusinessState? VehicleBusinessState,
    WireToGateCurrentStopWorklist? CurrentStopWorklist,
    WireToGateUpcomingStopPlan? UpcomingStopPlan,
    DateTimeOffset UpdatedAt)
{
    public static WireToGateJourneySnapshot Empty { get; } = new(
        null,
        null,
        null,
        DateTimeOffset.MinValue);

    public bool HasAuthoritativeWorklist => CurrentStopWorklist is not null;

    /// <summary>
    /// 行程计划点名的需求必须是作业清单里的一个。
    /// </summary>
    /// <remarks>
    /// 从 protocol 0.2.0 起清单可以有多项，计划的 <c>demandId</c> 也可以是 null——一趟行程属于整趟
    /// 而不属于其中某一个需求。原先这里用 <c>SingleOrDefault()</c>：清单一旦有两项它**抛异常**，
    /// 而不是判不一致，于是多单会在渲染路径上炸掉而不是被拒。
    /// </remarks>
    public bool HasConsistentDemand =>
        UpcomingStopPlan?.DemandId is null
        || CurrentStopWorklist is null
        || CurrentStopWorklist.Items.Count == 0
        || CurrentStopWorklist.Items.Any(item =>
            string.Equals(item.DemandId, UpcomingStopPlan.DemandId, StringComparison.Ordinal));

    /// <summary>
    /// 清单里至少有一项待处理，才谈得上录入。**不再要求恰好一项**：一次停靠可以有多项待装，
    /// 服务端下发的可录入范围也可能横跨几个站点（FR-001 AC-3）。
    /// </summary>
    public bool CanAcceptSublot =>
        VehicleBusinessState?.Readiness == "READY"
        && VehicleBusinessState.ManualChargingHold is false
        && VehicleBusinessState.BatteryState == "SUFFICIENT"
        && CurrentStopWorklist?.Items.Count > 0
        && HasConsistentDemand;

    public bool CanAcceptSublotAt(DateTimeOffset now, TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero || !CanAcceptSublot)
        {
            return false;
        }

        DateTimeOffset[] observations = new[]
        {
            UpdatedAt,
            VehicleBusinessState?.ObservedAt ?? DateTimeOffset.MinValue
        };
        return observations.All(observed => observed != DateTimeOffset.MinValue
            && observed <= now
            && now - observed <= maxAge);
    }
}

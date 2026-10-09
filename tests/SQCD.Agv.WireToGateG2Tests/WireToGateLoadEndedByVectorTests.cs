using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 向量成功结清时，它那条需求要不要标成「已交接，待系统确认」（8005-agv-onboard-hmi#209）。纯函数，不起夹具。
/// </summary>
/// <remarks>
/// 服务端把交接、强制恢复、补偿、取消的成功结果都当作终结那条需求；修正只改正装货结果，货还在原处，需求照旧在车上、不标（审查 RA）。标记而不移出是审查 S4 的裁定：车分不出服务端对上没对上。
/// </remarks>
public sealed class WireToGateLoadEndedByVectorTests
{
    private const string DemandA = "aaaaaaaa-0000-4000-8000-00000000000a";
    private const string DemandB = "bbbbbbbb-0000-4000-8000-00000000000b";

    [Theory]
    [InlineData(WireToGateRecoveryVectorTypes.FaultCargoHandoff)]
    [InlineData(WireToGateRecoveryVectorTypes.ForcedMechanicalRecovery)]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCompensation)]
    [InlineData(WireToGateRecoveryVectorTypes.LoadCancellation)]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public void AVectorThatEndsItsDemandMarksItHandedOff(string vectorType)
    {
        WireToGateRecoveryState after = WireToGateBusinessService.WithLoadEndedBy(TwoOnBoard(), Vector(vectorType, DemandA));

        Assert.Equal(
            [(DemandA, true), (DemandB, false)],
            after.LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public void ACorrectionLeavesItsDemandOnBoard()
    {
        WireToGateRecoveryState after = WireToGateBusinessService.WithLoadEndedBy(
            TwoOnBoard(),
            Vector(WireToGateRecoveryVectorTypes.LoadCorrection, DemandA));

        Assert.Equal(
            [(DemandA, false), (DemandB, false)],
            after.LoadsOnBoard?.Select(load => (load.DemandId, load.HandedOffAwaitingServer)));
    }

    private static WireToGateRecoveryState TwoOnBoard() =>
        WireToGateRecoveryState.Empty
            .WithLoadOnBoard(Load(DemandA, "44444444-0000-4444-8444-00000000000a", [1]))
            .WithLoadOnBoard(Load(DemandB, "44444444-0000-4444-8444-00000000000b", [5]));

    private static WireToGateRecoveryVectorContext Vector(string vectorType, string demandId) =>
        new(
            vectorType,
            "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
            null,
            demandId,
            "44444444-0000-4444-8444-00000000000a",
            null,
            [1],
            null,
            "operator-1",
            "SESSION",
            DateTimeOffset.UnixEpoch);

    private static WireToGateRecoveryOperationContext Load(string demandId, string attemptId, int[] slots) =>
        new(
            Guid.NewGuid().ToString("D"),
            null,
            1,
            DateTimeOffset.UnixEpoch,
            demandId,
            "55555555-0000-4555-8555-000000000001",
            attemptId,
            OperationType.Load,
            slots,
            slots.Length,
            true,
            new string('0', 64));
}

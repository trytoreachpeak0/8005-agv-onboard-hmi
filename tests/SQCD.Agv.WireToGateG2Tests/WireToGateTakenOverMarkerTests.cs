using SQCD.Agv.Core;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// 「向量接管过这次尝试」的标记在几个状态转换里怎么走（8005-agv-onboard-hmi#278）。纯函数，不起夹具。
/// </summary>
public sealed class WireToGateTakenOverMarkerTests
{
    private const string AttemptA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string AttemptB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string DemandA = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string ActionId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

    /// <summary>向量开过门、以非完成结果被忘掉：标记记下它持有的未结尝试。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public void AVectorThatActedLeavesItsAttemptMarked()
    {
        WireToGateRecoveryState state = HeldBy(Handoff(AttemptA)) with
        {
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            ActiveUnlockSlots = [1]
        };

        WireToGateRecoveryState forgotten = WireToGateBusinessService.ForgetSettledVector(state, Handoff(AttemptA));

        Assert.Null(forgotten.RecoveryVector);
        Assert.Equal(AttemptA, forgotten.TakenOverSlotOperationAttemptId);
        Assert.Equal([1], forgotten.ActiveUnlockSlots);
    }

    /// <summary>
    /// 向量停在准备写入：没有活动开锁集、没有完成的仓、检查点仍是 Prepared。它没开过门，不写标记，这次尝试由它自己的
    /// 确认如实结清。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public void AVectorThatDidNothingLeavesNoMarker()
    {
        WireToGateRecoveryState state = HeldBy(Handoff(AttemptA)) with
        {
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared
        };

        WireToGateRecoveryState forgotten = WireToGateBusinessService.ForgetSettledVector(state, Handoff(AttemptA));

        Assert.Null(forgotten.RecoveryVector);
        Assert.Null(forgotten.TakenOverSlotOperationAttemptId);
        Assert.Equal(AttemptA, forgotten.UnsettledSlotOperationAttemptId);
    }

    /// <summary>
    /// 什么都没做的向量被忘掉时，同一尝试上更早的一个向量留下的标记照旧保留：那扇门的记录还在。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public void AVectorThatDidNothingKeepsAnEarlierMarkerOnTheSameAttempt()
    {
        WireToGateRecoveryState state = HeldBy(Handoff(AttemptA)) with
        {
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
            TakenOverSlotOperationAttemptId = AttemptA
        };

        WireToGateRecoveryState forgotten = WireToGateBusinessService.ForgetSettledVector(state, Handoff(AttemptA));

        Assert.Equal(AttemptA, forgotten.TakenOverSlotOperationAttemptId);
    }

    /// <summary>
    /// 向量结算时日志簿已经换成了别的尝试 B（hmi#267 第二道防线那一支）：只去掉向量，B 留着；标记还记着更早的 A，A 已不在
    /// 日志簿上，标记清掉。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public void ForgettingAVectorBesideAnotherAttemptDropsAMarkerOfAnAttemptThatIsGone()
    {
        WireToGateRecoveryState state = HeldBy(Handoff(AttemptA)) with
        {
            UnsettledSlotOperationAttemptId = AttemptB,
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.ActiveUnlockSet,
            ActiveUnlockSlots = [5],
            TakenOverSlotOperationAttemptId = AttemptA
        };

        WireToGateRecoveryState forgotten = WireToGateBusinessService.ForgetSettledVector(state, Handoff(AttemptA));

        Assert.Equal(AttemptB, forgotten.UnsettledSlotOperationAttemptId);
        Assert.Null(forgotten.TakenOverSlotOperationAttemptId);
    }

    /// <summary>服务端拒绝补偿以外的向量请求：尝试随向量去掉，标记跟着清掉。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public void ARejectedVectorThatTakesTheAttemptAwayDropsTheMarker()
    {
        WireToGateRecoveryState state = HeldBy(Handoff(AttemptA)) with
        {
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
            ActiveUnlockSlots = [1],
            TakenOverSlotOperationAttemptId = AttemptA
        };

        WireToGateRecoveryState released = WireToGateBusinessService.ReleaseRejectedVector(
            state,
            Handoff(AttemptA),
            compensation: false);

        Assert.Null(released.UnsettledSlotOperationAttemptId);
        Assert.Null(released.TakenOverSlotOperationAttemptId);
        Assert.Equal([1], released.ActiveUnlockSlots);
    }

    /// <summary>服务端拒绝补偿请求：补偿留着尝试再申请，标记也留着。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public void ARejectedCompensationKeepsItsAttemptAndTheMarker()
    {
        WireToGateRecoveryVectorContext compensation = Handoff(AttemptA) with
        {
            VectorType = WireToGateRecoveryVectorTypes.LoadCompensation
        };
        WireToGateRecoveryState state = HeldBy(compensation) with
        {
            ProvenRecoveryCheckpoint = WireToGateRecoveryCheckpoint.Prepared,
            TakenOverSlotOperationAttemptId = AttemptA
        };

        WireToGateRecoveryState released = WireToGateBusinessService.ReleaseRejectedVector(
            state,
            compensation,
            compensation: true);

        Assert.Equal(AttemptA, released.UnsettledSlotOperationAttemptId);
        Assert.Equal(AttemptA, released.TakenOverSlotOperationAttemptId);
    }

    private static WireToGateRecoveryVectorContext Handoff(string attemptId) => new(
        WireToGateRecoveryVectorTypes.FaultCargoHandoff,
        ActionId,
        null,
        DemandA,
        attemptId,
        null,
        [1, 2],
        null,
        "operator-1",
        "SESSION",
        DateTimeOffset.UnixEpoch);

    private static WireToGateRecoveryState HeldBy(WireToGateRecoveryVectorContext vector) =>
        WireToGateRecoveryState.Empty with
        {
            UnsettledSlotOperationAttemptId = vector.SlotOperationAttemptId,
            RecoveryVector = vector
        };
}

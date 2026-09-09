using System.Text.Json;
using SQCD.Agv.Contracts;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// protocol-v0.3.0 把 <c>slotOperationAttemptId</c> 加进了服务端下发的三条恢复消息，三处都是
/// <c>required</c>。车载端的序列化器是 <c>JsonUnmappedMemberHandling.Disallow</c>——契约是封闭
/// schema，多出来的字段不是被忽略而是**抛在反序列化上**。所以这三条断言不是形式：字段没补齐之前，
/// 服务端 <c>0f6b424</c> 实际发出的报文车上一条都接不住，恢复流程连开始都开始不了。
/// </summary>
public sealed class WireToGateRecoveryScopePayloadTests
{
    private const string AgvId = "AGV-01";
    private const string AttemptId = "44444444-4444-4444-4444-444444444444";
    private const string DemandId = "11111111-1111-4111-8111-111111111111";
    private const string SessionId = "77777777-7777-4777-8777-777777777777";

    private static readonly int[] Slots = [1];

    private static readonly string[] AllowedActions = ["COMPENSATE_LOAD_ALL_EMPTY"];

    private static readonly DateTimeOffset Now =
        new(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public void ExceptionRecoverySessionOpenedCarriesTheServerNamedAttempt()
    {
        ExceptionRecoverySessionOpenedPayload payload = Roundtrip<ExceptionRecoverySessionOpenedPayload>(
            "ExceptionRecoverySessionOpened",
            new
            {
                requestId = "55555555-5555-4555-8555-555555555555",
                exceptionRecoverySessionId = SessionId,
                openedAt = Now,
                eventId = "66666666-6666-4666-8666-666666666666",
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                slots = Slots,
                recoverySessionRevision = 1L
            });

        Assert.Equal(AttemptId, payload.SlotOperationAttemptId);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public void RecoveryActionAcceptedCarriesTheServerNamedAttempt()
    {
        RecoveryActionAcceptedPayload payload = Roundtrip<RecoveryActionAcceptedPayload>(
            "RecoveryActionAccepted",
            new
            {
                recoveryActionId = "88888888-8888-4888-8888-888888888888",
                exceptionRecoverySessionId = SessionId,
                slotOperationAttemptId = AttemptId,
                acceptedAction = "COMPENSATE_LOAD_ALL_EMPTY",
                recoverySessionRevision = 2L,
                acceptedAt = Now
            });

        Assert.Equal(AttemptId, payload.SlotOperationAttemptId);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public void ExceptionRecoverySessionSnapshotCarriesTheServerNamedAttempt()
    {
        ExceptionRecoverySessionSnapshotPayload payload =
            Roundtrip<ExceptionRecoverySessionSnapshotPayload>(
                "ExceptionRecoverySessionSnapshot",
                SnapshotPayload(AttemptId));

        Assert.Equal(AttemptId, payload.SlotOperationAttemptId);
    }

    /// <summary>
    /// 服务端在会话没有挂上任何 station operation 时发 <c>null</c>——那是合法值，不是缺字段。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public void ASessionWithoutAStationOperationCarriesANullAttempt()
    {
        ExceptionRecoverySessionSnapshotPayload payload =
            Roundtrip<ExceptionRecoverySessionSnapshotPayload>(
                "ExceptionRecoverySessionSnapshot",
                SnapshotPayload(null));

        Assert.Null(payload.SlotOperationAttemptId);
    }

    private static object SnapshotPayload(string? slotOperationAttemptId) =>
        new
        {
            exceptionRecoverySessionId = SessionId,
            recoverySessionRevision = 3L,
            state = "OPEN",
            administratorId = "maintenance-001",
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            eventId = "66666666-6666-4666-8666-666666666666",
            demandId = DemandId,
            slotOperationAttemptId,
            slots = Slots,
            selectedAction = (string?)null,
            allowedActions = AllowedActions,
            blockingFacts = Array.Empty<object>()
        };

    private static T Roundtrip<T>(string messageType, object payload)
    {
        WireToGateEnvelope envelope = WireToGateProtocolSerializer.Create(
            messageType,
            Guid.NewGuid().ToString("D"),
            null,
            AgvId,
            1,
            Now,
            payload);

        // 走一遍线上的字节，而不是直接反序列化那个匿名对象：Disallow 只在**读**的时候发作。
        string line = WireToGateProtocolSerializer.Serialize(envelope);
        return WireToGateProtocolSerializer.DeserializePayload<T>(
            WireToGateProtocolSerializer.DeserializeAndValidate(line, AgvId, 1));
    }
}

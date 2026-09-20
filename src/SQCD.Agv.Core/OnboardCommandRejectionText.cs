namespace SQCD.Agv.Core;

/// <summary>
/// The operator-facing wording of a refused UI command (8005-agv-onboard-hmi#171). One sentence per
/// code in <see cref="OnboardFailureClassification"/>'s operator-rejection registry, kept in one place.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "shown to the operator instead of latching the vehicle" is only worth anything
/// if what is shown says something. A refusal rendered as a bare <c>SUBLOT_NOT_IN_WORKLIST</c> tells
/// the person holding the scanner no more than the red banner did.
/// </para>
/// <para>
/// Every operator-rejection code has a sentence here, and
/// <c>LocalFailureCodeRegistryArchitectureTests</c> fails if one does not. An unknown code still falls
/// back to showing itself -- the same choice <c>WireToGateSublotRejectionText</c> makes, for the same
/// reason: a guessed meaning is worse than none, and the raw code is something the operator can read
/// out to whoever is on the other end of the phone.
/// </para>
/// </remarks>
public static class OnboardCommandRejectionText
{
    private static readonly Dictionary<string, string> Wording = new(StringComparer.Ordinal)
    {
        // The scan itself was refused. Same wording the controller already uses for this code, so the
        // operator reads one sentence whichever path refused the scan.
        ["SUBLOT_NOT_IN_WORKLIST"] = "当前条码不属于服务端下发的站点任务，请核对条码或等待任务刷新。",
        ["LOAD_CANCELLATION_IN_PROGRESS"] = "本站的取消装货正在处理中，请等结果回来后再扫码。",
        ["FATAL_FAULT_LATCHED"] = "本机已进入严重安全故障，扫码开门已停用。请联系维护人员复核并复位。",

        // Not ready yet. Waiting is the action.
        ["WIRE_TO_GATE_NOT_READY"] = "上层安全会话尚未就绪，已禁止扫码、开门和发车。请等待连接及恢复完成。",
        ["WIRE_TO_GATE_JOURNEY_NOT_READY"] = "服务端旅程或当前站点任务尚未同步，已禁止扫码和开门。请等待任务恢复。",
        ["WIRE_TO_GATE_JOURNAL_NOT_READY"] = "车载端作业记录尚未就绪，请稍候再试。",
        ["VEHICLE_NOT_READY"] = "车辆当前不满足操作条件，请稍候再试。",

        // Identity and credentials. Maintenance fixes these, not the operator at the vehicle.
        ["WIRE_TO_GATE_OPERATOR_NOT_READY"] = "本机尚未配置操作员工号，请联系维护人员。",
        ["RECOVERY_AUTHENTICATION_REQUIRED"] = "恢复操作需要管理员凭据，本机尚未配置，请联系维护人员。",
        ["RECOVERY_OPERATOR_MISMATCH"] = "本次恢复由另一位操作员发起，请由该操作员继续，或联系班组长。",
        ["FORCED_RECOVERY_NOT_AUTHORIZED"] = "强制机械取出尚未获得授权，请联系维护人员。",

        // Pressed at a moment that does not accept it. Nothing was sent.
        ["RECOVERY_SESSION_NOT_READY"] = "当前没有可以处理的恢复会话，请等待服务端开启。",
        ["RECOVERY_SESSION_STATE_PENDING"] = "上一次恢复申请还在等服务端答复，请稍候。",
        ["RECOVERY_ACTION_ALREADY_SELECTED"] = "本次恢复已经选过处理方式，请等待服务端下发后续命令。",
        ["RECOVERY_REASON_REQUIRED"] = "请先填写恢复原因，再提交。",
        ["RECOVERY_OPERATION_CONTEXT_MISSING"] = "找不到本次恢复对应的仓位操作，请等待服务端同步，或联系维护人员。",
        ["LOAD_CORRECTION_OPERATION_NOT_AVAILABLE"] = "当前没有可以修正的装货操作。",

        // The hardware-recovery record is incomplete, unnecessary, or cannot be judged yet.
        ["HARDWARE_RECOVERY_NOT_REQUIRED"] = "当前不需要填写硬件恢复记录。",
        ["HARDWARE_RECOVERY_OBSERVATIONS_REQUIRED"] = "请先逐仓填写现场确认结果，再提交。",
        ["HARDWARE_RECOVERY_RECORD_REQUIRED"] = "请先提交硬件恢复记录，再继续。",
        ["SLOT_STATE_UNKNOWN"] = "部分仓位状态暂时读不到，请等待恢复后再试。"
    };

    /// <summary>True when <paramref name="reasonCode"/> has a sentence of its own here.</summary>
    public static bool HasWording(string reasonCode) => Wording.ContainsKey(reasonCode);

    /// <summary>What the operator reads when a UI command was refused.</summary>
    public static string Describe(string reasonCode) =>
        Wording.TryGetValue(reasonCode, out string? wording)
            ? wording
            : $"操作被拒绝（{reasonCode}）。请核对后重试，持续出现请联系维护人员。";

    /// <summary>
    /// The same sentence with the raw code after it — what every operator-facing refusal actually
    /// uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The code stays in front of the operator on purpose</b>, the same way
    /// <c>WireToGateSublotRejectionText</c> keeps an unrecognised one: it is what they read out to
    /// whoever is on the other end of the phone. A sentence they understand plus a token maintenance
    /// can act on beats either alone.
    /// </para>
    /// <para>
    /// <b>It is also what makes a refusal identifiable from outside.</b> The G2 harness waits for a
    /// <c>RECOVERY_BLOCKED</c> event naming a specific code, precisely so a test cannot pass on some
    /// other guard's refusal (<c>RecoveryVectorG2Tests.WaitForRecoveryBlockedAsync</c>). Rendering
    /// only the Chinese sentence took that away and five of those tests stopped being able to tell
    /// which guard had fired — the right fix was to carry both, not to weaken the assertion.
    /// </para>
    /// <para>
    /// An unrecognised code is not repeated: <see cref="Describe"/>'s fallback already carries it.
    /// </para>
    /// </remarks>
    public static string DescribeWithCode(string reasonCode) =>
        HasWording(reasonCode)
            ? $"{Describe(reasonCode)}（{reasonCode}）"
            : Describe(reasonCode);
}

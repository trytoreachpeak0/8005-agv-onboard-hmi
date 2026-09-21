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
        // 这两句原来说「已禁止……开门」，那一半在 v2 上不成立，和本票修的是同一个缺陷：
        // 服务端下发的仓位命令走 WireToGateSlotOperationExecutor，它不看控制器状态，照样开门
        // （审查，判据路条目 12）。改成只陈述本界面真正挡得住的事。
        // NOT_READY 的条件是「会话 Ready 且车辆停稳」为假（App.xaml.cs 按位置传进来的
        // externalSafetyReadyProvider），所以这一句同时覆盖两种状态，只能说两种状态下都成立的事
        // （8005-agv-onboard-hmi#177）。会话没 Ready：发送口拒绝扫码。会话 Ready、车在动：本端没有
        // 任何一层挡——CanSubmitSublot、SubmitSublotAsync、发送口都不看车动没动——而录入请求可能
        // 还开着。**补上这个口子的是服务端，不是本界面**：车一动，车载端报 departureSafe=false，服务端
        // 把会话降出 Ready，扫码入口随之关闭，中间隔一次上报往返。这段时间里「本界面已禁止扫码」是假的，
        // 所以这一句不再说它。「发车」留着：这个状态下控制器自己不放行发车。
        // 前提由 LoadCancellationBeforeSublotG2Tests.TheEntryStaysOpenWhileTheSessionIsReadyAndTheVehicleMoves
        // 钉住：哪天扫码入口自己看车动没动了，那条会红，那时这一句才可以重新说禁止扫码。与控制器里那一份
        // 逐字相同由 OnboardControllerTests.ExternalSafetyNotReadyGuidanceClaimsOnlyWhatHoldsWhileTheVehicleMoves 守着。
        ["WIRE_TO_GATE_NOT_READY"] = "上层安全会话尚未就绪或车辆尚未停稳，本界面已禁止发车。请等待连接及恢复完成、车辆停稳。",
        ["WIRE_TO_GATE_JOURNEY_NOT_READY"] = "服务端旅程或当前站点任务尚未同步，本界面已禁止扫码。请等待任务恢复。",
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

    /// <summary>
    /// 恢复入口被挡下时操作员读到的话：为什么被挡，加上这条路**必须**保留的那半句现场指引。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>那半句是被静默丢掉过一次的。</b>原文案是「恢复向量被阻断：&lt;码&gt;。**请确认车辆停稳、
    /// 仓门状态和服务端授权。**」，改成按码查表时注意力全在「让操作员看得懂、让 G2 认得出」，
    /// 于是尾巴那句安全指引跟着裸码一起没了，换成了 <see cref="Describe"/> 兜底句里的
    /// 「请核对后重试」——**那句话暗示「什么都没发生，再按一次即可」**。
    /// </para>
    /// <para>
    /// 而落到这条路上的码有一批是登记表里的严重故障码（<c>RECOVERY_SCOPE_MISMATCH</c>、
    /// <c>RECOVERY_STATE_MISMATCH</c>、<c>RECOVERY_VECTOR_CONFLICT</c> 等），登记表对它们的说法是
    /// 「某处状态与另一处不一致，本进程对仓门的认知正是有疑问的那一件事」。**登记表说状态存疑，
    /// 界面说重试就行**——那正是这张票要消灭的形状，只是换了一句话
    /// （8005-agv-onboard-hmi#171 审查，产品路发现 4）。
    /// </para>
    /// <para>
    /// 教训写在这里而不是提交信息里：**改一段文案之前，先问原来那句话里有没有承载安全信息的成分。**
    /// </para>
    /// </remarks>
    public static string DescribeRecoveryBlocked(string reasonCode) =>
        $"{DescribeWithCode(reasonCode)}请确认车辆停稳、仓门状态和服务端授权，不要仅凭本提示重试。";
}

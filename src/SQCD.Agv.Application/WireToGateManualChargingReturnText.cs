namespace SQCD.Agv.Application;

/// <summary>
/// Why 「充电后返回服务」 cannot be pressed right now, as far as the business service can tell. The fatal-fault latch
/// is not here: the view model owns it (<c>RecoveryEntriesBlockedByFatalFault</c>).
/// </summary>
public enum WireToGateManualChargingReturnBlocker
{
    None,
    SessionNotReady,
    OperatorNotAuthorized
}

/// <summary>
/// The line shown beside the manual-charging hold when its exit cannot be pressed (8005-agv-onboard-hmi#293).
/// </summary>
/// <remarks>
/// <para>
/// Before #293 the button was simply collapsed, and with the factory setting (<c>recoveryResumeEnabled=false</c>)
/// it always is: the screen said 「需人工充电：服务端保持」 and offered nothing to press and nothing to read. This
/// line is read-only. It opens nothing and changes no condition of the button, so the entry still needs every
/// local check it needed before -- which matters here, because <c>ManualChargingReturnToServiceRequested</c>
/// carries no proof and the server checks only the role it names.
/// </para>
/// <para>
/// One sentence at a time, by a fixed priority: latch, then session, then authorization. Each earlier cause
/// shuts the entry whatever the later ones are, so naming a later one would send the operator to fix something
/// that would not open it. The screen's sentences name no ticket and no internal role; who to call is
/// 「系统维护人员」.
/// </para>
/// </remarks>
public static class WireToGateManualChargingReturnText
{
    public const string FatalFaultLatchedGuidance = "安全故障解除前不能申请返回服务，请联系系统维护人员。";

    public const string SessionNotReadyGuidance = "与服务端的连接就绪后才能申请返回服务。";

    public const string OperatorNotAuthorizedGuidance =
        "充电完成后需维护管理员在本机开启返回服务权限，再按「充电后返回服务」。请联系系统维护人员。";

    /// <summary>
    /// The sentence for this moment, or an empty string when there is no hold or the entry can be pressed.
    /// </summary>
    /// <param name="blocker">
    /// The business service's answer; <see cref="WireToGateManualChargingReturnBlocker.None"/> with the entry not
    /// offered and no latch is the instant between two reads of a changing state, and shows nothing.
    /// </param>
    public static string Guidance(
        bool manualChargingHold,
        bool entryOffered,
        bool fatalFaultLatched,
        WireToGateManualChargingReturnBlocker blocker)
    {
        if (!manualChargingHold || entryOffered)
        {
            return string.Empty;
        }

        if (fatalFaultLatched)
        {
            return FatalFaultLatchedGuidance;
        }

        return blocker switch
        {
            WireToGateManualChargingReturnBlocker.SessionNotReady => SessionNotReadyGuidance,
            WireToGateManualChargingReturnBlocker.OperatorNotAuthorized => OperatorNotAuthorizedGuidance,
            _ => string.Empty
        };
    }
}

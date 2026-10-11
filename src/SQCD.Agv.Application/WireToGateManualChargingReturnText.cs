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
public static class WireToGateManualChargingReturnText
{
    public static string Guidance(
        bool manualChargingHold,
        bool entryOffered,
        bool fatalFaultLatched,
        WireToGateManualChargingReturnBlocker blocker) => string.Empty;
}

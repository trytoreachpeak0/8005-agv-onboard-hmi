using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf;

/// <summary>
/// What the operator is told while an operation's unknown result waits for the server and the vehicle has no exit of its own
/// (<c>8005-agv-onboard-hmi#370</c>, agv03 2026-10-19), the one place it is written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it sends the operator to a maintainer and nowhere else.</b> Once an operation's own result that is not completed is
/// on file, the server puts it into <c>RecoveryRequired</c> and refuses a cancellation over it; every other recovery vector is
/// behind <c>recoveryResumeEnabled</c>, which vehicles ship with off. The user decided on 2026-10-19 that the way out of such a
/// stuck operation is a manual entry on the server, not that switch. So the screen says nothing on the vehicle can settle it
/// and names who can -- never the switch, a ticket or the coordinator (onboard-hmi#254's rule for operator text).
/// </para>
/// </remarks>
public static class WireToGateNoOnboardExitText
{
    private const string ContactMaintainer = "请联系系统维护人员在服务端处理。";

    /// <summary>The line the recovery area shows while the vehicle offers no exit.</summary>
    public static string Notice(OperationType operationType) =>
        $"这次{Describe(operationType)}的结果未知，已交服务端处理。车上没有可以处理它的按钮，{ContactMaintainer}";

    /// <summary>
    /// A 「取消装货」 confirmed after the load it was pressed over ended with its result handed to the server: the press did
    /// nothing, and pressing again cannot help, because that entry does not come back over this load.
    /// </summary>
    public const string LoadCancellationAfterResultHandedOver =
        "这次装货的结果未知，已交服务端处理，车上已不能再取消。这次取消没有执行，也没有开任何仓门。" + ContactMaintainer;

    private static string Describe(OperationType operationType) =>
        operationType == OperationType.Unload ? "卸货" : "装货";
}

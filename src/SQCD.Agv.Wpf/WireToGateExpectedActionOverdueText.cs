namespace SQCD.Agv.Wpf;

/// <summary>
/// The operator-facing line for an expected-action-overdue slot (REQ-0358, CP-0005 item one note 4,
/// onboard-hmi#109). Both sentences are the user's own wording of 2026-09-18, kept verbatim.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Reported" only while the session is up.</b> The server acknowledges an alarm snapshot but never
/// answers it, so the one thing the vehicle can stand behind is that the snapshot went out on a live
/// session. With the session down nothing reaches anyone, and the line says so instead.
/// </para>
/// <para>
/// It tells the operator what is expected and who is coming; it offers nothing to press. Declaring a slot
/// faulty is the administrator's, on the server (REQ-0359); the operator cannot tell a broken sensor from a
/// job not yet done, and neither can this screen.
/// </para>
/// </remarks>
public static class WireToGateExpectedActionOverdueText
{
    public const string Disconnected = "与服务端断开，请联系管理员";

    /// <param name="expectedAction">The alarm's message, e.g. 「关好3号仓门」.</param>
    /// <param name="sessionConnected">Whether the WIRE_TO_GATE session is connected right now.</param>
    public static string Describe(string expectedAction, bool sessionConnected) =>
        sessionConnected
            ? $"期待的操作（{expectedAction}）很久没有完成，已上报，班组长或管理员会到现场查看"
            : Disconnected;
}

namespace SQCD.Agv.Wpf;

/// <summary>
/// The 「取消装货」 entry's caption and dialogs, the one place they are written: the machine pickup's wording, and the staging
/// pickup stop's (CP-0012 revision four, <c>REQ-0211</c>; 8005-agv-onboard-hmi#348).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the staging pickup stop says something else.</b> There the cancellation before any sublot is not offered, so what is
/// left is the load in flight -- the door is open and the basket does not fit or the wrong one was scanned. The control server
/// puts that demand back to be dispatched instead of cancelling it (<c>8005-agv-control-server#713</c>), so a caption that says
/// 「取消」 would tell the operator the demand is gone when it is not.
/// </para>
/// <para>
/// <b>The machine pickup's wording does not change, character for character</b>: the real-rig L2 scenarios find the button and
/// its confirmation by these captions (<c>8005-agv-control-server</c> <c>scripts/l2/L2.psm1</c>, <c>InvokeButton</c> and
/// <c>Confirm</c>).
/// </para>
/// <para>
/// The dialog titles are not here: every title is a literal at its <c>ModalDialogs.Show</c> call
/// (<c>ModalDialogRegistrationArchitectureTests</c>), so <c>MainWindow.OnLoadCancellationClick</c> picks between two calls.
/// </para>
/// </remarks>
public static class WireToGateLoadCancellationText
{
    public const string MachineCaption = "取消装货";

    public const string StagingCaption = "撤销本次开仓";

    public static string Caption(bool atStagingPickupStop) =>
        atStagingPickupStop ? StagingCaption : MachineCaption;

    /// <param name="detail">The sentence naming what the press acts on; empty when there is nothing to name.</param>
    public static string ConfirmationText(bool atStagingPickupStop, string detail) =>
        atStagingPickupStop
            ? "请确认车辆已停稳，并由授权人员确认要撤销这一次开仓。\n\n"
                + detail
                + "\n\n系统只会执行服务端授权的目标仓位清空，不会重新选择仓位；这一篮这趟不带走。"
                + "这条需求不会取消，会放回待派，之后还会再派车来装。是否继续？"
            : "请确认车辆已停稳、目标仓门已锁好，并由授权人员确认本次装货应取消。\n\n"
                + detail
                + "\n\n系统只会执行服务端授权的目标仓位清空，不会重新选择仓位；本站尚未录入子批时不会打开任何仓门。是否继续？";

    public static string FailureText(bool atStagingPickupStop) =>
        atStagingPickupStop
            ? "撤销开仓未被接受。请检查授权、车辆停稳信号和服务端状态。"
            : "装货取消未被接受。请检查授权、车辆停稳信号和服务端状态。";

    /// <summary>The worklist row the press acts on, as the confirmation names it.</summary>
    public static string Subject(bool atStagingPickupStop, string sublot, string taskTypeText, string expectedBasketCountText) =>
        atStagingPickupStop
            ? $"将要撤销的是：子批 {sublot}，{taskTypeText}，{expectedBasketCountText}。"
            : $"将要取消的任务：子批 {sublot}，{taskTypeText}，{expectedBasketCountText}。";

    /// <summary>A load in flight whose demand is no longer in the worklist on screen.</summary>
    public static string LoadInFlight(bool atStagingPickupStop) =>
        atStagingPickupStop ? "将要撤销的是正在进行的这次开仓。" : "将要取消的是正在进行的这次装货。";

    public const string SubjectGone = "将要取消的任务已不在本站清单里。";
}

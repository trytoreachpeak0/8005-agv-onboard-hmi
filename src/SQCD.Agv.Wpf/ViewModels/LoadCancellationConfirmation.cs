namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 按下「取消装货」时取下的一次按键：确认框正文、被拒时弹框的正文，与确认后要执行的按键（8005-agv-onboard-hmi#291）。
/// 都出自同一次读取：待送点取货停靠上说「撤销本次开仓」，机台取货照旧（8005-agv-onboard-hmi#348）。
/// </summary>
/// <remarks>
/// 弹框标题不在这里：标题必须是 <c>ModalDialogs.Show</c> 调用处的字面量（<c>ModalDialogRegistrationArchitectureTests</c>），
/// 窗口按 <see cref="AtStagingPickupStop"/> 在两个调用之间选。
/// </remarks>
/// <param name="DetailText">确认框里复述的将要取消的任务；没有可复述的为空。</param>
/// <param name="SendAsync">确认框点「是」之后执行的按键。</param>
/// <param name="AtStagingPickupStop">按下时停靠是不是待送点取货停靠。</param>
public sealed record LoadCancellationConfirmation(
    string DetailText,
    Func<CancellationToken, Task<bool>> SendAsync,
    bool AtStagingPickupStop)
{
    /// <summary>确认框正文，含 <see cref="DetailText"/>。</summary>
    public string Text => WireToGateLoadCancellationText.ConfirmationText(AtStagingPickupStop, DetailText);

    /// <summary>按键未被接受时弹框的正文。</summary>
    public string FailureText => WireToGateLoadCancellationText.FailureText(AtStagingPickupStop);
}
